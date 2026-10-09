using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Text;
using System.Text.Json;

namespace ScriptStacks;

internal static class Program
{
    private const int MaxRequestCharacters = 16 * 1024;
    private const int MaxResponseCharacters = 512 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static async Task<int> Main()
    {
        // All diagnostics belong in the response. Library output must never
        // corrupt the single JSON object consumed by the native plugin.
        Console.OutputEncoding = new UTF8Encoding(false);
        TextWriter responseWriter = Console.Out;
        Console.SetOut(TextWriter.Null);
        CaptureResult result;
        try
        {
            using CancellationTokenSource inputTimeout = new(TimeSpan.FromSeconds(10));
            string input = await ReadRequestAsync(inputTimeout.Token);
            Request request = JsonSerializer.Deserialize<Request>(input, JsonOptions)
                ?? throw new ArgumentException("The request must be a JSON object.");
            request = request with
            {
                Operation = (request.Operation ?? "").Trim().ToLowerInvariant(),
                Runtime = (request.Runtime ?? "").Trim().ToLowerInvariant()
            };
            if (request.Operation is not ("capture" or "install"))
                throw new ArgumentException("Unknown operation. Use capture or install.");
            if (request.Runtime is not ("js" or "python" or "java"))
                throw new ArgumentException("Unsupported runtime. Use js, python, or java.");

            ProcessIdentity.EnableDebugPrivilege();
            using var process = ProcessIdentity.Open(request);
            using CancellationTokenSource timeout = new(request.Operation == "install"
                ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(30));
            using var cancelEvent = request.CancelEventName is null ? null
                : new CancellationEventRegistration(request.CancelEventName, timeout);
            // Runtime entry points revalidate identity immediately before their
            // own side effects. Retain our handle throughout the whole request.
            Task<CaptureResult> operation = DispatchAsync(request, timeout.Token);
            try
            {
                result = await operation.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                // Inspector cleanup and child-process termination need a short
                // grace period after cancellation before this helper exits.
                try { await operation.WaitAsync(TimeSpan.FromSeconds(8)); }
                catch (Exception) { /* Preserve the original timeout. */ }
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            result = CaptureResult.Error("The operation timed out or was cancelled. The target may be busy or may not support this capture method.");
        }
        catch (JsonException ex)
        {
            result = CaptureResult.Error($"Invalid request JSON: {ex.Message}");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            result = CaptureResult.Error("Access to the selected process was denied. Run System Informer elevated if the target requires it; protected processes may remain inaccessible.");
        }
        catch (Exception ex)
        {
            result = CaptureResult.Error(ex.Message);
        }

        if (result.Text.Length > MaxResponseCharacters)
            result = result with { Text = result.Text[..MaxResponseCharacters] + "\n[Output truncated at 512 Ki characters.]" };
        await responseWriter.WriteLineAsync(JsonSerializer.Serialize(result, JsonOptions));
        await responseWriter.FlushAsync();
        return result.Status == "error" ? 1 : 0;
    }

    private static Task<CaptureResult> DispatchAsync(Request request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ProcessIdentity.Verify(request);
        if (request.Operation == "install")
            return RuntimeTools.InstallAsync(request, token);
        return request.Runtime == "js"
            ? JavaScript.CaptureAsync(request, token)
            : RuntimeTools.CaptureAsync(request, token);
    }

    private static async Task<string> ReadRequestAsync(CancellationToken token)
    {
        StringBuilder input = new();
        char[] buffer = new char[512];
        while (true)
        {
            int count = await Console.In.ReadAsync(buffer.AsMemory(), token);
            if (count == 0)
                return input.ToString();
            if (input.Length + count > MaxRequestCharacters)
                throw new ArgumentException("Request exceeds the 16 Ki character limit.");
            input.Append(buffer, 0, count);
        }
    }

    private sealed class CancellationEventRegistration : IDisposable
    {
        private readonly ExistingEvent signal;
        private readonly RegisteredWaitHandle registration;

        public CancellationEventRegistration(string name, CancellationTokenSource cancellation)
        {
            signal = new ExistingEvent(name);
            try
            {
                // Catch a pre-signaled event before dispatching any operation.
                if (signal.WaitOne(0))
                    cancellation.Cancel();
                registration = ThreadPool.RegisterWaitForSingleObject(signal, static (state, _) =>
                {
                    try { ((CancellationTokenSource)state!).Cancel(); }
                    catch (ObjectDisposedException) { /* Request already completed. */ }
                }, cancellation, Timeout.Infinite, executeOnlyOnce: true);
            }
            catch
            {
                signal.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            registration.Unregister(null);
            signal.Dispose();
        }

        private sealed class ExistingEvent : WaitHandle
        {
            public ExistingEvent(string name)
            {
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("The cancellation event name must not be empty.");
                SafeWaitHandle handle = OpenEvent(0x00100000, false, name); // SYNCHRONIZE only.
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    throw new InvalidOperationException($"Cannot open the cancellation event: {new Win32Exception(error).Message}");
                }
                SafeWaitHandle = handle;
            }

            // OpenEvent never creates an event. A missing or inaccessible name
            // must fail the request instead of silently disabling cancellation.
            [DllImport("kernel32.dll", EntryPoint = "OpenEventW", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern SafeWaitHandle OpenEvent(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);
        }
    }
}