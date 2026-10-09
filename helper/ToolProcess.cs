using System.Diagnostics;
using System.Text;

namespace ScriptStacks;

internal sealed record ToolOutput(int ExitCode, string StdOut, string StdErr);

internal static class ToolProcess
{
    public static async Task<ToolOutput> RunAsync(Request request, string executable,
        IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ToolFiles.RequireExecutable(executable);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // Inherited Java options can start unrelated agents or change jcmd's operation.
        start.Environment.Remove("JAVA_TOOL_OPTIONS");
        start.Environment.Remove("JDK_JAVA_OPTIONS");
        start.Environment.Remove("_JAVA_OPTIONS");
        ProcessIdentity.Verify(request);
        using var process = new Process { StartInfo = start };
        process.Start();
        try
        {
            var stdout = ReadBoundedAsync(process.StandardOutput, deadline);
            var stderr = ReadBoundedAsync(process.StandardError, deadline);
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), stdout, stderr);
            return new ToolOutput(process.ExitCode, await stdout, await stderr);
        }
        catch
        {
            // Only this newly created tool process is terminated, never the inspected process.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            if (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
                throw new TimeoutException("The diagnostic tool timed out or exceeded its output limit. Its process was stopped.");
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationTokenSource cancellation)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            int count = await reader.ReadAsync(buffer.AsMemory(), cancellation.Token);
            if (count == 0) return output.ToString();
            if (output.Length + count > 1024 * 1024)
            {
                cancellation.Cancel();
                throw new IOException("Diagnostic tool output exceeds 1 Mi characters.");
            }
            output.Append(buffer, 0, count);
        }
    }
}

internal static class ToolFiles
{
    internal const ushort X86 = 0x014c;
    internal const ushort X64 = 0x8664;
    internal const ushort Arm64 = 0xaa64;
    internal static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SystemInformerScriptStacks", "tools");

    internal static void RequireExecutable(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path) ||
            (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("The diagnostic executable is missing or is not a regular file: " + path);
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Expected a Windows executable: " + path);
        _ = Machine(path);
    }

    internal static ushort Machine(string path)
    {
        using var file = File.OpenRead(path);
        using var reader = new BinaryReader(file);
        if (file.Length < 64 || reader.ReadUInt16() != 0x5a4d) throw new IOException("Invalid PE image: " + path);
        file.Position = 0x3c;
        int offset = reader.ReadInt32();
        if (offset < 64 || offset > file.Length - 6) throw new IOException("Invalid PE header: " + path);
        file.Position = offset;
        if (reader.ReadUInt32() != 0x00004550) throw new IOException("Invalid PE signature: " + path);
        return reader.ReadUInt16();
    }

    internal static string Architecture(ushort machine) => machine switch { X86 => "x86", X64 => "x64", Arm64 => "ARM64", _ => $"PE machine 0x{machine:x4}" };

    internal static IEnumerable<string> OnPath(string filename)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string cleaned = directory.Trim().Trim('"');
            if (Path.IsPathFullyQualified(cleaned)) yield return Path.Combine(cleaned, filename);
        }
    }
}
