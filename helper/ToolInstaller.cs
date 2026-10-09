using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;

namespace ScriptStacks;

internal sealed record ToolPackage(string Directory, string Url, string Sha256, ushort Machine);

internal static class ToolInstaller
{
    // Versioned official distribution URLs and SHA-256 values, verified 2026-10-09.
    // Sources: https://pypi.org/pypi/py-spy/0.4.2/json and
    // https://api.adoptium.net/v3/assets/latest/{major}/hotspot?architecture={x64|x32}&image_type=jdk&os=windows&vendor=eclipse
    // Downloading never executes an installer, modifies PATH, or requires administrator rights.
    private static readonly ToolPackage Python = new("py-spy-0.4.2",
        "https://files.pythonhosted.org/packages/6f/ed/1409cdb557e558a6c98003ab12fdd4284699e158c167c187cb0f124eea4c/py_spy-0.4.2-py2.py3-none-win_amd64.whl",
        "8b06a353c177677e4e1701b288d8c58e2f8d4208ee81a8048d9f72ba800918f8", ToolFiles.X64);

    private static readonly Dictionary<(int, ushort), ToolPackage> Java = new()
    {
        [(8, ToolFiles.X64)] = Jdk(8, "jdk8u504-b01", "x64", "8u504b01", "ea43d46ede95b51e44a12c66711706cddc762e0a766c54bccea18954e902b2aa"),
        [(11, ToolFiles.X64)] = Jdk(11, "jdk-11.0.32.1+1", "x64", "11.0.32.1_1", "d5008f02174c1ad21c10407cbf104815cb390ec4c263d53946e8d0507d7a77f9"),
        [(17, ToolFiles.X64)] = Jdk(17, "jdk-17.0.20.1+1", "x64", "17.0.20.1_1", "e53a79c3c3d86865bd7e787903884331068e71321714ffd44f145785affc7cb0"),
        [(21, ToolFiles.X64)] = Jdk(21, "jdk-21.0.12.1+1", "x64", "21.0.12.1_1", "f9d6e191ab098c0d416e7d588a24420a8621cd2f4720dab2459b8b7b2d2d8b4e"),
        [(25, ToolFiles.X64)] = Jdk(25, "jdk-25.0.4.1+1", "x64", "25.0.4.1_1", "00c847d804f4a78e9f04f2683faf14fed898535b177b7fc704486cb0284e9283"),
        [(26, ToolFiles.X64)] = Jdk(26, "jdk-26.0.2.1+1", "x64", "26.0.2.1_1", "cf914bf4cc6bf9cedcabec216165bafd10331c5b4d4f2e424b55f6a2e68a1cf5"),
        [(8, ToolFiles.X86)] = Jdk(8, "jdk8u472-b08", "x86-32", "8u472b08", "15df042dcb03bac23a0b2d8d010bf26c49e1247a9a7b58deea19c85d29039f2d"),
        [(11, ToolFiles.X86)] = Jdk(11, "jdk-11.0.29+7", "x86-32", "11.0.29_7", "0b5bb836546f86aab9ffe53a14ae51f4801c5a020383d7ea88a054091676698c"),
        [(17, ToolFiles.X86)] = Jdk(17, "jdk-17.0.17+10", "x86-32", "17.0.17_10", "34ecb7e53b87ecb288be113ad1c76f3e55e7dc2f2b32427605e356feac95cb74")
    };

    private static ToolPackage Jdk(int major, string release, string architecture, string fileVersion, string hash) =>
        new($"jdk-{major}-{architecture}-{release}",
            $"https://github.com/adoptium/temurin{major}-binaries/releases/download/{Uri.EscapeDataString(release)}/OpenJDK{major}U-jdk_{architecture}_windows_hotspot_{fileVersion}.zip",
            hash, architecture == "x64" ? ToolFiles.X64 : ToolFiles.X86);

    internal static bool SupportsJava(int major, ushort machine) => Java.ContainsKey((major, machine));

    internal static async Task<CaptureResult> InstallAsync(Request request, CancellationToken cancellationToken)
    {
        ProcessIdentity.Verify(request);
        ToolPackage package;
        string runtime = request.Runtime.ToLowerInvariant();
        if (runtime == "python") package = Python;
        else if (runtime == "java")
        {
            var target = RuntimeTools.InspectJava(request);
            if (target.Major is not int major || !Java.TryGetValue((major, target.Machine), out var found))
                return CaptureResult.Error($"No pinned official JDK installer is available for {target.Description}. Install a matching JDK manually and configure JAVA_HOME.");
            package = found;
        }
        else return CaptureResult.Error("No tool installer is available for runtime: " + request.Runtime);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(4));
        var token = deadline.Token;
        EnsurePrivateRoot();
        string scratch = Path.Combine(ToolFiles.Root, ".install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            string archive = Path.Combine(scratch, "download.zip");
            await DownloadAsync(new Uri(package.Url), archive, runtime == "python" ? 20 * 1024 * 1024 : 400L * 1024 * 1024, token);
            await using (var stream = File.OpenRead(archive))
            {
                string actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
                if (!actual.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The downloaded tool failed SHA-256 verification. Nothing was installed or executed.");
            }
            string extracted = Path.Combine(scratch, "extracted");
            Directory.CreateDirectory(extracted);
            await ExtractAsync(archive, extracted, token);
            string staged = Path.Combine(scratch, "ready");
            string executable;
            if (runtime == "python")
            {
                string source = Path.Combine(extracted, "py_spy-0.4.2.data", "scripts", "py-spy.exe");
                ToolFiles.RequireExecutable(source);
                Directory.CreateDirectory(staged);
                executable = Path.Combine(staged, "py-spy.exe");
                File.Move(source, executable);
                // Keep the license bundled in the wheel beside the standalone executable.
                string license = Directory.EnumerateFiles(extracted, "LICENSE*", SearchOption.AllDirectories).FirstOrDefault() ?? "";
                if (File.Exists(license)) File.Copy(license, Path.Combine(staged, "LICENSE"));
            }
            else
            {
                string[] homes = Directory.GetDirectories(extracted);
                if (homes.Length != 1 || !File.Exists(Path.Combine(homes[0], "bin", "jcmd.exe")))
                    throw new IOException("The verified JDK archive does not have the expected layout.");
                Directory.Move(homes[0], staged);
                executable = Path.Combine(staged, "bin", "jcmd.exe");
                ToolFiles.RequireExecutable(executable);
            }
            if (ToolFiles.Machine(executable) != package.Machine)
                throw new IOException("The verified package has an unexpected executable architecture.");
            await File.WriteAllTextAsync(Path.Combine(staged, "script-stacks-package.txt"),
                $"Source: {package.Url}\nSHA256: {package.Sha256}\n", token);
            ProcessIdentity.Verify(request);
            string destination = Path.Combine(ToolFiles.Root, package.Directory);
            // A completed package is immutable. Never remove arbitrary existing tools or directories.
            if (Directory.Exists(destination))
            {
                string existing = Path.Combine(destination, runtime == "python" ? "py-spy.exe" : Path.Combine("bin", "jcmd.exe"));
                ToolFiles.RequireExecutable(existing);
                if (ToolFiles.Machine(existing) != package.Machine) throw new IOException("The existing cached package has an unexpected architecture.");
                return CaptureResult.Ok("The matching private tool is already installed. Retry stack capture.\n" + destination);
            }
            Directory.Move(staged, destination);
            return CaptureResult.Ok("Installed the SHA-256 verified diagnostic tool. Retry stack capture.\n" + destination);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CaptureResult.Error("The tool download or extraction timed out. No incomplete installation was activated.");
        }
        finally
        {
            // This GUID directory was created by this invocation and never shared as a tool location.
            try { Directory.Delete(scratch, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void EnsurePrivateRoot()
    {
        Directory.CreateDirectory(ToolFiles.Root);
        var directory = new DirectoryInfo(ToolFiles.Root);
        for (int i = 0; i < 2 && directory is not null; i++, directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The private tools directory must not be a junction or symbolic link.");
    }

    private static async Task DownloadAsync(Uri uri, string path, long maxBytes, CancellationToken token)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SystemInformerScriptStacks/1.0");
        for (int redirects = 0; redirects < 8; redirects++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !AllowedDownloadHost(uri.Host))
                throw new IOException("The tool download attempted an untrusted destination.");
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                uri = response.Headers.Location is Uri next ? new Uri(uri, next) : throw new IOException("The tool server returned an invalid redirect.");
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > maxBytes) throw new IOException("Tool archive exceeds the download size limit.");
            await using var source = await response.Content.ReadAsStreamAsync(token);
            await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
            byte[] buffer = new byte[65536];
            long total = 0;
            while (true)
            {
                int count = await source.ReadAsync(buffer, token);
                if (count == 0) return;
                total += count;
                if (total > maxBytes) throw new IOException("Tool archive exceeds the download size limit.");
                await destination.WriteAsync(buffer.AsMemory(0, count), token);
            }
        }
        throw new IOException("Too many tool download redirects.");
    }

    private static bool AllowedDownloadHost(string host) => host.Equals("files.pythonhosted.org", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase);

    private static async Task ExtractAsync(string archivePath, string root, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 50000) throw new IOException("Too many files in tool archive.");
        long total = 0;
        string prefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            total = checked(total + entry.Length);
            if (total > 1500L * 1024 * 1024) throw new IOException("Tool archive exceeds the extraction size limit.");
            string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Split(Path.DirectorySeparatorChar).Any(part => part is ".." or "." || part.EndsWith(' ') || part.EndsWith('.')) ||
                ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000)
                throw new IOException("Unsafe path or symbolic link in tool archive.");
            string destination = Path.GetFullPath(Path.Combine(root, relative));
            if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("Tool archive path escapes its destination.");
            if (relative.EndsWith(Path.DirectorySeparatorChar)) { Directory.CreateDirectory(destination); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = entry.Open();
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
            await input.CopyToAsync(output, token);
        }
    }
}
