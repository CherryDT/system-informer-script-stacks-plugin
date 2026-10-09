using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ScriptStacks;

internal sealed record JavaTarget(ushort Machine, int? Major, IReadOnlyList<string> Homes, string Description);

public static class RuntimeTools
{
    public static Task<CaptureResult> CaptureAsync(Request request, CancellationToken cancellationToken) =>
        request.Runtime.ToLowerInvariant() switch
        {
            "python" => CapturePythonAsync(request, cancellationToken),
            "java" => CaptureJavaAsync(request, cancellationToken),
            _ => Task.FromResult(CaptureResult.Error("Unsupported external runtime: " + request.Runtime))
        };

    public static Task<CaptureResult> InstallAsync(Request request, CancellationToken cancellationToken) =>
        ToolInstaller.InstallAsync(request, cancellationToken);

    private static async Task<CaptureResult> CapturePythonAsync(Request request, CancellationToken cancellationToken)
    {
        ProcessIdentity.Verify(request);
        using var target = Process.GetProcessById(request.Pid);
        string targetPath = target.MainModule?.FileName ?? throw new IOException("Cannot inspect the Python executable.");
        ushort machine = ToolFiles.Machine(targetPath);
        if (machine != ToolFiles.X64)
            return CaptureResult.Error($"Python target architecture is {ToolFiles.Architecture(machine)}. The supported Windows py-spy distribution is x64 only. No incompatible tool was launched.");

        string? tool = FindPython();
        if (tool is null) return CaptureResult.Missing("py-spy", "Install py-spy 0.4.2", "Python stack capture requires py-spy. Install its verified standalone x64 executable in the plugin's private tools folder.");
        var version = await ToolProcess.RunAsync(request, tool, ["--version"], TimeSpan.FromSeconds(5), cancellationToken);
        var help = await ToolProcess.RunAsync(request, tool, ["dump", "--help"], TimeSpan.FromSeconds(5), cancellationToken);
        if (help.ExitCode != 0 || !help.StdOut.Contains("--nonblocking", StringComparison.Ordinal))
            return CaptureResult.Missing("py-spy", "Install py-spy 0.4.2", $"This py-spy does not support nonblocking dump capture: {tool}\n{version.StdOut.Trim()}\nInstall the supported version. A suspending capture is not used because cancellation could leave the target suspended.");
        var output = await ToolProcess.RunAsync(request, tool,
            ["dump", "--pid", request.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture), "--nonblocking"],
            TimeSpan.FromSeconds(20), cancellationToken);
        string heading = $"Python — {version.StdOut.Trim()} — {ToolFiles.Architecture(machine)}\nTool: {tool}\nNonblocking snapshot; concurrently changing frames may be incomplete.\n\n";
        return output.ExitCode == 0 && !string.IsNullOrWhiteSpace(output.StdOut)
            ? CaptureResult.Ok(heading + output.StdOut + (string.IsNullOrWhiteSpace(output.StdErr) ? "" : "\nDiagnostics:\n" + output.StdErr))
            : CaptureResult.Error(heading + $"py-spy exited with code {output.ExitCode}.\n" + output.StdErr + output.StdOut +
                "\nCheck access rights/elevation, CPython version support, and whether the process has exited. PyPy is not supported by py-spy.");
    }

    internal static string? FindPython()
    {
        var candidates = new[] { Path.Combine(ToolFiles.Root, "py-spy-0.4.2", "py-spy.exe") }
            .Concat(ToolFiles.OnPath("py-spy.exe"));
        foreach (string candidate in candidates)
        {
            try { ToolFiles.RequireExecutable(candidate); if (ToolFiles.Machine(candidate) == ToolFiles.X64) return candidate; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }

    private static async Task<CaptureResult> CaptureJavaAsync(Request request, CancellationToken cancellationToken)
    {
        JavaTarget target = InspectJava(request);
        var homes = target.Homes.ToList();
        string? javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome) && Path.IsPathFullyQualified(javaHome)) homes.Add(javaHome);
        if (Directory.Exists(ToolFiles.Root)) homes.AddRange(Directory.EnumerateDirectories(ToolFiles.Root, "jdk-*"));
        var candidates = homes.Select(home => Path.Combine(home, "bin", "jcmd.exe"))
            .Concat(ToolFiles.OnPath("jcmd.exe")).Distinct(StringComparer.OrdinalIgnoreCase);
        var skipped = new List<string>();
        foreach (string candidate in candidates)
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                ToolFiles.RequireExecutable(candidate);
                ushort machine = ToolFiles.Machine(candidate);
                if (machine != target.Machine) { skipped.Add($"{candidate}: {ToolFiles.Architecture(machine)} does not match target"); continue; }
                string home = Path.GetDirectoryName(Path.GetDirectoryName(candidate))!;
                int? major = ReadJavaMajor(home);
                // The target's own JDK is usable even when an old vendor omitted its release file.
                bool targetHome = target.Homes.Contains(home, StringComparer.OrdinalIgnoreCase);
                if (!targetHome && (target.Major is null || major != target.Major))
                { skipped.Add($"{candidate}: Java {major?.ToString() ?? "version unknown"} cannot be validated against target"); continue; }
                var output = await ToolProcess.RunAsync(request, candidate,
                    [request.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture), "Thread.print", "-l"],
                    TimeSpan.FromSeconds(25), cancellationToken);
                string heading = $"Java — {target.Description}\nTool: {candidate}\n\n";
                return output.ExitCode == 0 && !string.IsNullOrWhiteSpace(output.StdOut)
                    ? CaptureResult.Ok(heading + output.StdOut + (string.IsNullOrWhiteSpace(output.StdErr) ? "" : "\nDiagnostics:\n" + output.StdErr))
                    : CaptureResult.Error(heading + $"jcmd exited with code {output.ExitCode}.\n" + output.StdErr + output.StdOut +
                        "\nUse the same Windows account and suitable elevation as the target. The JVM may disable attach (-XX:+DisableAttachMechanism), be unresponsive, or use an unsupported vendor/version.");
            }
            catch (IOException error) { skipped.Add(candidate + ": " + error.Message); }
            catch (UnauthorizedAccessException error) { skipped.Add(candidate + ": " + error.Message); }
        }
        string details = $"No matching jcmd was found for {target.Description}.\n" + string.Join("\n", skipped.Take(12));
        if (target.Major is not int version || !ToolInstaller.SupportsJava(version, target.Machine))
            return CaptureResult.Error(details + "\nThe target Java version/architecture has no pinned official installer (Windows x86 packages are available only for Java 8, 11 and 17). Install a matching JDK and set JAVA_HOME or add its bin directory to PATH, then restart System Informer.");
        return CaptureResult.Missing("jdk", $"Install private JDK {version} ({ToolFiles.Architecture(target.Machine)})", details + $"\nInstall a verified Eclipse Temurin JDK {version} ZIP in the plugin's private tools folder to obtain jcmd.");
    }

    internal static JavaTarget InspectJava(Request request)
    {
        ProcessIdentity.Verify(request);
        using var process = Process.GetProcessById(request.Pid);
        string executable = process.MainModule?.FileName ?? throw new IOException("Cannot read the target executable path.");
        ushort machine = ToolFiles.Machine(executable);
        var homes = new List<string>();
        int? moduleMajor = null;
        foreach (ProcessModule module in process.Modules)
        {
            bool namedJvm = string.Equals(Path.GetFileName(module.FileName), "jvm.dll", StringComparison.OrdinalIgnoreCase);
            FileVersionInfo? version = null;
            try { version = FileVersionInfo.GetVersionInfo(module.FileName); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (!namedJvm && !string.Equals(version?.OriginalFilename, "jvm.dll", StringComparison.OrdinalIgnoreCase)) continue;
            if (version is not null)
                moduleMajor = version.FileMajorPart == 1 ? version.FileMinorPart : version.FileMajorPart;
            machine = ToolFiles.Machine(module.FileName);
            var directory = new DirectoryInfo(Path.GetDirectoryName(module.FileName)!);
            for (int level = 0; directory is not null && level < 4; level++, directory = directory.Parent)
            {
                if (directory.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) && directory.Parent is not null)
                {
                    homes.Add(directory.Parent.FullName);
                    if (directory.Parent.Name.Equals("jre", StringComparison.OrdinalIgnoreCase) && directory.Parent.Parent is not null)
                        homes.Add(directory.Parent.Parent.FullName);
                    break;
                }
            }
        }
        // This also covers standard java.exe launchers when modules could not identify a home.
        var executableDirectory = Directory.GetParent(executable);
        string executableName = Path.GetFileName(executable);
        if ((executableName.Equals("java.exe", StringComparison.OrdinalIgnoreCase) || executableName.Equals("javaw.exe", StringComparison.OrdinalIgnoreCase)) &&
            executableDirectory?.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) == true && executableDirectory.Parent is not null)
            homes.Add(executableDirectory.Parent.FullName);
        homes = homes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        int? major = homes.Select(ReadJavaMajor).FirstOrDefault(value => value is not null) ?? (moduleMajor > 0 ? moduleMajor : null);
        return new JavaTarget(machine, major, homes, $"Java {major?.ToString() ?? "version unknown"}, {ToolFiles.Architecture(machine)}");
    }

    internal static int? ReadJavaMajor(string home)
    {
        string release = Path.Combine(home, "release");
        if (!File.Exists(release) || new FileInfo(release).Length > 64 * 1024) return null;
        try
        {
            string contents = File.ReadAllText(release);
            var match = Regex.Match(contents, "(?m)^JAVA_VERSION=\"(?<version>[0-9]+(?:\\.[0-9]+)?)", RegexOptions.CultureInvariant);
            if (!match.Success) return null;
            string version = match.Groups["version"].Value;
            return int.TryParse(version.StartsWith("1.", StringComparison.Ordinal) ? version[2..] : version.Split('.')[0], out int major) ? major : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
