using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace ScriptStacks;

// The Windows activation handshake is Node's own node-debug-handler-PID mapping.
// See nodejs/node src/inspector_agent.cc. No memory or code is written to the target.
public static class JavaScript
{
    private const int MaxWorkers = 32;
    private const string PidExpression = "typeof process === 'object' && process !== null ? process.pid : -1";
    private sealed record Endpoint(Uri WebSocket, int Port);

    public static async Task<CaptureResult> CaptureAsync(Request request, CancellationToken token)
    {
        ProcessIdentity.Verify(request);
        Endpoint? endpoint = await DiscoverAsync(request, token);
        bool activated = false;
        if (endpoint is null)
        {
            if (!request.AllowActivate)
                return new("needs_activation", "No loopback Node inspector was found for this process. Capturing requires enabling its inspector, briefly pausing JavaScript, and then resuming it. Activation uses Node's registered Windows debug handler. Electron renderers and builds with inspector support disabled cannot use this mechanism.");
            try
            {
                ProcessIdentity.Verify(request);
                Native.Activate(request);
                activated = true;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                return new("unsupported", "The process does not expose a usable Node Windows debug handler: " + ex.Message + "\nFor Node or Electron's main process, start it with --inspect=127.0.0.1:0. Packaged Electron builds may disable Node inspection through a fuse; renderer processes require Chromium DevTools and are not supported by this Node capture path. Renaming an executable is not a restriction.");
            }
            var until = Stopwatch.StartNew();
            do
            {
                await Task.Delay(50, token);
                endpoint = await DiscoverAsync(request, token);
            } while (endpoint is null && until.ElapsedMilliseconds < 2500);
            if (endpoint is null)
                return CaptureResult.Error("Node's debug handler was signaled, but no PID-owned loopback inspector appeared. The configured port may be occupied, the listener may use a non-loopback address, or this runtime may not support activation. An inspector might still appear later; no unverified endpoint was closed.");
        }

        await using var connection = await Inspector.ConnectAsync(endpoint.WebSocket, token);
        var text = new StringBuilder();
        bool verified = false;
        try
        {
            await VerifyPidAsync(connection.Main, request, token);
            verified = true;
            text.AppendLine($"JavaScript stacks - PID {request.Pid}");
            text.AppendLine("A short inspector pause samples each isolate separately; this is not an atomic process snapshot.");
            bool workersEnabled = false;
            try
            {
                await connection.Main.CommandAsync("NodeWorker.enable", new { waitForDebuggerOnStart = false }, token);
                workersEnabled = true;
            }
            catch (InspectorException ex) { text.AppendLine("Worker enumeration unavailable: " + ex.Message); }
            try { await CaptureIsolateAsync(connection.Main, "Main thread", request, text, token); }
            catch (TimeoutException ex) { text.AppendLine("Main thread: " + ex.Message); }
            if (workersEnabled)
            {
                // Existing-worker events may arrive immediately after the enable response.
                await Task.Delay(50, token);
                var workers = connection.Workers.Values.Take(MaxWorkers).ToArray();
                foreach (var worker in workers)
                {
                    token.ThrowIfCancellationRequested();
                    try { await CaptureIsolateAsync(worker, worker.Title, request, text, token); }
                    catch (Exception ex) when (ex is InspectorException or TimeoutException or WebSocketException)
                    { text.AppendLine($"\n{worker.Title}: capture unavailable ({ex.Message})"); }
                }
                if (connection.Workers.Count > MaxWorkers)
                    text.AppendLine($"Worker capture limited to {MaxWorkers} isolates.");
                text.AppendLine($"\nWorker isolates sampled: {workers.Length}. Workers starting or stopping during capture may be absent.");
            }
        }
        finally
        {
            // These are only our protocol sessions. Never detach another debugger's sessions.
            await Task.WhenAll(connection.Workers.Values.Append(connection.Main).Select(s => s.CleanupAsync()));
            if (connection.Workers.Values.Append(connection.Main).Any(s => s.ResumeNeeded))
                text.AppendLine("Warning: resume could not be confirmed for an isolate; our debugger session is being disconnected.");
            if (activated && request.CloseInspector && verified)
            {
                try
                {
                    ProcessIdentity.Verify(request);
                    await VerifyPidAsync(connection.Main, request, CancellationToken.None, 1000);
                    // inspector.close() disconnects all clients. Preserve it if another TCP client joined.
                    if (Native.ConnectionCount(request.Pid, endpoint.Port) != 1)
                        text.AppendLine("Inspector left enabled: another connection is present or ownership could not be confirmed.");
                    else
                    {
                        // Schedule after the protocol response; synchronous close would deadlock on our socket.
                        // process.getBuiltinModule supports ESM on recent Node; require covers older CJS runtimes.
                        const string close = "(() => { const i = typeof process.getBuiltinModule === 'function' ? process.getBuiltinModule('inspector') : (typeof require === 'function' ? require('inspector') : null); if (!i) return false; setImmediate(() => i.close()); return true; })()";
                        var result = await connection.Main.CommandAsync("Runtime.evaluate", new { expression = close, returnByValue = true, silent = true }, CancellationToken.None, 1000);
                        if (!ResultValue(result, out var value) || value.ValueKind != JsonValueKind.True)
                            text.AppendLine("Inspector left enabled: this runtime could not load its inspector module for cleanup.");
                    }
                }
                catch (Exception ex) { text.AppendLine("Inspector cleanup could not be confirmed: " + ex.Message); }
            }
            else if (activated)
                text.AppendLine("Inspector remains enabled for this process.");
        }
        return CaptureResult.Ok(text.ToString());
    }

    private static async Task VerifyPidAsync(Session session, Request request, CancellationToken token, int timeoutMilliseconds = 2500)
    {
        ProcessIdentity.Verify(request);
        var response = await session.CommandAsync("Runtime.evaluate", new { expression = PidExpression, returnByValue = true, throwOnSideEffect = true, silent = true }, token, timeoutMilliseconds);
        if (!ResultValue(response, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int pid) || pid != request.Pid)
            throw new InspectorException("Inspector process.pid does not match the requested process. Capture was refused.");
    }

    private static bool ResultValue(JsonElement response, out JsonElement value)
    {
        value = default;
        return !response.TryGetProperty("exceptionDetails", out _) && response.TryGetProperty("result", out var remote) && remote.TryGetProperty("value", out value);
    }

    private static async Task CaptureIsolateAsync(Session session, string label, Request request, StringBuilder text, CancellationToken token)
    {
        await VerifyPidAsync(session, request, token);
        ProcessIdentity.Verify(request);
        await session.CommandAsync("Debugger.enable", new { maxScriptsCacheSize = 0 }, token);
        session.DebuggerEnabled = true;
        text.AppendLine("\n" + label);
        var elapsed = Stopwatch.StartNew();
        JsonElement? sample = null;
        bool alreadyPaused = session.Pauses.Reader.TryRead(out var existing);
        if (alreadyPaused)
        {
            sample = existing;
            text.AppendLine("Already paused when attached; preserving the existing pause.");
        }
        else
        {
            try
            {
                do
                {
                    ProcessIdentity.Verify(request);
                    session.ResumeNeeded = true;
                    await session.CommandAsync("Debugger.pause", null, token);
                    using var pauseTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    pauseTimeout.CancelAfter(1500);
                    try { sample = await session.Pauses.Reader.ReadAsync(pauseTimeout.Token); }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    { throw new TimeoutException("No JavaScript pause arrived; the isolate may be idle or inside native code."); }
                    if (!OnlyTimerFrame(sample.Value) || elapsed.ElapsedMilliseconds >= 200) break;
                    await session.CommandAsync("Debugger.resume", null, token);
                    session.ResumeNeeded = false;
                    await Task.Delay(10, token);
                } while (elapsed.ElapsedMilliseconds < 200);
            }
            finally { await session.ResumeAsync(); }
        }
        if (sample is null) { text.AppendLine("No JavaScript frames were available."); return; }
        if (!sample.Value.TryGetProperty("callFrames", out var frames) || frames.GetArrayLength() == 0)
        { text.AppendLine("No JavaScript frames; the isolate may be idle or in native code."); return; }
        int frameNumber = 0;
        foreach (var frame in frames.EnumerateArray().Take(256))
        {
            var location = frame.GetProperty("location");
            string scriptId = location.GetProperty("scriptId").GetString() ?? "";
            string url = GetString(frame, "url");
            if (string.IsNullOrEmpty(url) && session.Scripts.TryGetValue(scriptId, out var scriptUrl)) url = scriptUrl;
            string name = GetString(frame, "functionName");
            int line = location.GetProperty("lineNumber").GetInt32() + 1;
            int column = location.TryGetProperty("columnNumber", out var col) ? col.GetInt32() + 1 : 1;
            text.AppendLine($"  #{frameNumber++} {(string.IsNullOrEmpty(name) ? "(anonymous)" : name)} at {(string.IsNullOrEmpty(url) ? "script:" + scriptId : url)}:{line}:{column}");
        }
        if (OnlyTimerFrame(sample.Value)) text.AppendLine("Only Node's timer dispatcher was observed after up to 200 ms of retries.");
    }

    private static bool OnlyTimerFrame(JsonElement pause) => pause.TryGetProperty("callFrames", out var frames) && frames.GetArrayLength() == 1 && GetString(frames[0], "functionName") == "processTimers";
    private static string GetString(JsonElement element, string property) => element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static async Task<Endpoint?> DiscoverAsync(Request request, CancellationToken token)
    {
        ProcessIdentity.Verify(request);
        using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromMilliseconds(250) };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(450) };
        foreach (var listener in Native.Listeners(request.Pid).Take(128))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var address = new UriBuilder("http", listener.Address.ToString(), listener.Port, "/json/list").Uri;
                using var response = await http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, token);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 1024 * 1024) continue;
                using var input = await response.Content.ReadAsStreamAsync(token);
                using var data = new MemoryStream();
                byte[] chunk = new byte[8192];
                using var readLimit = CancellationTokenSource.CreateLinkedTokenSource(token);
                readLimit.CancelAfter(450);
                int count;
                while ((count = await input.ReadAsync(chunk, readLimit.Token)) != 0)
                {
                    if (data.Length + count > 1024 * 1024) throw new IOException("Inspector discovery response too large.");
                    data.Write(chunk, 0, count);
                }
                using var json = JsonDocument.Parse(data.ToArray());
                if (json.RootElement.ValueKind != JsonValueKind.Array) continue;
                foreach (var target in json.RootElement.EnumerateArray())
                {
                    string raw = GetString(target, "webSocketDebuggerUrl");
                    if (!Uri.TryCreate(raw, UriKind.Absolute, out var ws) || ws.Scheme != "ws" || ws.Port != listener.Port || !string.IsNullOrEmpty(ws.UserInfo) || !IPAddress.TryParse(ws.Host.Trim('[', ']'), out var ip) || !IPAddress.IsLoopback(ip)) continue;
                    // Use the enumerated owned address, never an address supplied by an unrelated endpoint.
                    var safe = new UriBuilder(ws) { Host = listener.Address.ToString() }.Uri;
                    ProcessIdentity.Verify(request);
                    return new(safe, listener.Port);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException || ex is OperationCanceledException && !token.IsCancellationRequested) { }
        }
        return null;
    }

    private sealed class InspectorException(string message) : Exception(message);

    private sealed class Session(Inspector owner, string? id, string title)
    {
        public string? Id { get; } = id;
        public string Title { get; } = title;
        public bool DebuggerEnabled { get; set; }
        public bool ResumeNeeded { get; set; }
        public ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> Pending { get; } = new();
        public ConcurrentDictionary<string, string> Scripts { get; } = new();
        public Channel<JsonElement> Pauses { get; } = Channel.CreateBounded<JsonElement>(new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest });

        public async Task<JsonElement> CommandAsync(string method, object? parameters, CancellationToken token, int timeoutMilliseconds = 2500)
        {
            int commandId = owner.NextId();
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            Pending[commandId] = completion;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token, owner.Lifetime.Token);
            limit.CancelAfter(timeoutMilliseconds);
            try
            {
                string command = JsonSerializer.Serialize(new { id = commandId, method, @params = parameters });
                if (Id is null) await owner.SendAsync(command, limit.Token);
                else await owner.Main.CommandAsync("NodeWorker.sendMessageToWorker", new { sessionId = Id, message = command }, limit.Token);
                JsonElement reply;
                try { reply = await completion.Task.WaitAsync(limit.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && !owner.Lifetime.IsCancellationRequested)
                { throw new TimeoutException(method + " timed out."); }
                if (reply.TryGetProperty("error", out var error)) throw new InspectorException(GetString(error, "message"));
                return reply.TryGetProperty("result", out var result) ? result : default;
            }
            finally { Pending.TryRemove(commandId, out _); }
        }

        public void Receive(JsonElement message)
        {
            if (message.TryGetProperty("id", out var idValue) && idValue.TryGetInt32(out int responseId))
            {
                if (Pending.TryRemove(responseId, out var pending)) pending.TrySetResult(message.Clone());
                return;
            }
            string method = GetString(message, "method");
            if (!message.TryGetProperty("params", out var p)) return;
            if (method == "Debugger.paused") Pauses.Writer.TryWrite(p.Clone());
            if (method == "Debugger.scriptParsed" && Scripts.Count < 50000) Scripts[GetString(p, "scriptId")] = GetString(p, "url");
        }

        public async Task ResumeAsync()
        {
            if (!ResumeNeeded) return;
            try { await CommandAsync("Debugger.resume", null, CancellationToken.None, 1000); ResumeNeeded = false; }
            catch { /* CleanupAsync retries before disconnecting our session. */ }
        }
        public async Task CleanupAsync()
        {
            await ResumeAsync();
            if (DebuggerEnabled)
            {
                try { await CommandAsync("Debugger.disable", null, CancellationToken.None, 500); } catch { }
                DebuggerEnabled = false;
            }
        }
        public void Fail(Exception error)
        {
            foreach (var pending in Pending.Values) pending.TrySetException(error);
            Pauses.Writer.TryComplete(error);
        }
    }

    private sealed class Inspector : IAsyncDisposable
    {
        private readonly ClientWebSocket socket = new();
        private readonly SemaphoreSlim sendLock = new(1, 1);
        private Task? receiver;
        private int nextId;
        public CancellationTokenSource Lifetime { get; } = new();
        public Session Main { get; }
        public ConcurrentDictionary<string, Session> Workers { get; } = new();
        private Inspector() { Main = new(this, null, "Main thread"); socket.Options.Proxy = null; }
        public int NextId() => Interlocked.Increment(ref nextId);
        public static async Task<Inspector> ConnectAsync(Uri uri, CancellationToken token)
        {
            var result = new Inspector();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(2500);
                await result.socket.ConnectAsync(uri, timeout.Token);
                result.receiver = result.ReceiveAsync();
                return result;
            }
            catch { result.socket.Dispose(); result.Lifetime.Dispose(); result.sendLock.Dispose(); throw; }
        }
        public async Task SendAsync(string command, CancellationToken token)
        {
            await sendLock.WaitAsync(token);
            try { await socket.SendAsync(Encoding.UTF8.GetBytes(command).AsMemory(), WebSocketMessageType.Text, true, token); }
            finally { sendLock.Release(); }
        }
        private async Task ReceiveAsync()
        {
            var buffer = new byte[16384];
            try
            {
                while (!Lifetime.IsCancellationRequested)
                {
                    using var data = new MemoryStream();
                    ValueWebSocketReceiveResult part;
                    do
                    {
                        part = await socket.ReceiveAsync(buffer.AsMemory(), Lifetime.Token);
                        if (part.MessageType == WebSocketMessageType.Close) throw new WebSocketException("Inspector disconnected.");
                        if (part.MessageType != WebSocketMessageType.Text || data.Length + part.Count > 8 * 1024 * 1024) throw new InspectorException("Invalid or oversized inspector message.");
                        data.Write(buffer, 0, part.Count);
                    } while (!part.EndOfMessage);
                    using var document = JsonDocument.Parse(data.ToArray());
                    var message = document.RootElement;
                    string method = GetString(message, "method");
                    if (method == "NodeWorker.attachedToWorker")
                    {
                        var p = message.GetProperty("params");
                        string id = GetString(p, "sessionId");
                        string title = p.TryGetProperty("workerInfo", out var info) ? GetString(info, "title") : "";
                        if (Workers.Count < 256) Workers.TryAdd(id, new(this, id, string.IsNullOrEmpty(title) ? "Worker " + id : title));
                    }
                    else if (method == "NodeWorker.receivedMessageFromWorker")
                    {
                        var p = message.GetProperty("params");
                        if (Workers.TryGetValue(GetString(p, "sessionId"), out var worker))
                        {
                            using var child = JsonDocument.Parse(GetString(p, "message"));
                            worker.Receive(child.RootElement);
                        }
                    }
                    else if (method == "NodeWorker.detachedFromWorker")
                    {
                        string id = GetString(message.GetProperty("params"), "sessionId");
                        if (Workers.TryRemove(id, out var worker)) worker.Fail(new InspectorException("Worker exited or detached."));
                    }
                    else Main.Receive(message);
                }
            }
            catch (Exception ex)
            {
                Main.Fail(ex);
                foreach (var worker in Workers.Values) worker.Fail(ex);
                Lifetime.Cancel();
            }
        }
        public async ValueTask DisposeAsync()
        {
            Lifetime.Cancel();
            socket.Abort();
            if (receiver is not null) { try { await receiver; } catch { } }
            socket.Dispose(); sendLock.Dispose(); Lifetime.Dispose();
        }
    }

    private static class Native
    {
        internal sealed record Listener(IPAddress Address, int Port);
        [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle OpenFileMapping(uint access, bool inherit, string name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr MapViewOfFile(SafeFileHandle mapping, uint access, uint high, uint low, UIntPtr count);
        [DllImport("kernel32.dll")] private static extern bool UnmapViewOfFile(IntPtr view);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsWow64Process(SafeProcessHandle process, out bool wow64);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern UIntPtr VirtualQueryEx(SafeProcessHandle process, IntPtr address, out MemoryInfo info, UIntPtr length);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateRemoteThread(SafeProcessHandle process, IntPtr attributes, UIntPtr stackSize, IntPtr start, IntPtr parameter, uint flags, out uint threadId);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeFileHandle token);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeFileHandle token, int informationClass, IntPtr buffer, int size, out int required);
        [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(SafeFileHandle handle, int objectType, uint information, out IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr descriptor);
        [DllImport("advapi32.dll")] private static extern bool EqualSid(IntPtr first, IntPtr second);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
        [StructLayout(LayoutKind.Sequential)] private struct MemoryInfo { public IntPtr BaseAddress, AllocationBase; public uint AllocationProtect; public UIntPtr RegionSize; public uint State, Protect, Type; }

        internal static void Activate(Request request)
        {
            using var process = OpenProcess(0x0002 | 0x0008 | 0x0010 | 0x0020 | 0x0400, false, request.Pid);
            if (process.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open the process for Node's registered debug-handler activation.");
            if (!IsWow64Process(process, out bool wow64)) throw new Win32Exception(Marshal.GetLastWin32Error());
            int pointerSize = Environment.Is64BitOperatingSystem && !wow64 ? 8 : 4;
            if (pointerSize > IntPtr.Size) throw new NotSupportedException("A 64-bit helper is required for this process.");
            using var mapping = OpenFileMapping(0x20004, false, "node-debug-handler-" + request.Pid);
            if (mapping.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Node's registered debug-handler mapping is unavailable.");
            VerifyMappingOwner(mapping, process);
            IntPtr view = MapViewOfFile(mapping, 4, 0, 0, (UIntPtr)pointerSize);
            if (view == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr handler;
            try { handler = pointerSize == 8 ? new IntPtr(Marshal.ReadInt64(view)) : (IntPtr.Size == 4 ? new IntPtr(Marshal.ReadInt32(view)) : new IntPtr(unchecked((long)(uint)Marshal.ReadInt32(view)))); }
            finally { UnmapViewOfFile(view); }
            if (handler == IntPtr.Zero || VirtualQueryEx(process, handler, out var info, (UIntPtr)Marshal.SizeOf<MemoryInfo>()) == UIntPtr.Zero || info.State != 0x1000 || info.Type != 0x1000000 || (info.Protect & 0xF0) == 0 || (info.Protect & 0x100) != 0)
                throw new InvalidOperationException("The registered handler is not in executable image memory of the selected process.");
            ProcessIdentity.Verify(request);
            using var thread = CreateRemoteThread(process, IntPtr.Zero, UIntPtr.Zero, handler, IntPtr.Zero, 0, out _);
            if (thread.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Node's registered debug handler could not be invoked.");
        }

        private static void VerifyMappingOwner(SafeFileHandle mapping, SafeProcessHandle process)
        {
            uint error = GetSecurityInfo(mapping, 6, 1, out var owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var descriptor);
            if (error != 0) throw new Win32Exception((int)error, "Cannot verify the debug-handler mapping owner.");
            try
            {
                if (!OpenProcessToken(process, 8, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
                using (token)
                {
                    GetTokenInformation(token, 4, IntPtr.Zero, 0, out int length); // TokenOwner
                    if (length <= 0 || length > 65536) throw new InvalidOperationException("Cannot read process token owner.");
                    IntPtr data = Marshal.AllocHGlobal(length);
                    try
                    {
                        if (!GetTokenInformation(token, 4, data, length, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                        if (!EqualSid(owner, Marshal.ReadIntPtr(data))) throw new InvalidOperationException("The debug-handler mapping owner differs from the process token owner.");
                    }
                    finally { Marshal.FreeHGlobal(data); }
                }
            }
            finally { if (descriptor != IntPtr.Zero) LocalFree(descriptor); }
        }

        internal static List<Listener> Listeners(int pid) => ReadTcp(pid, listenersOnly: true).Where(r => IPAddress.IsLoopback(r.Address)).Distinct().ToList();
        internal static int ConnectionCount(int pid, int port)
        {
            try { return ReadTcp(pid, listenersOnly: false).Count(r => r.Port == port); }
            catch { return -1; }
        }
        private static List<Listener> ReadTcp(int pid, bool listenersOnly)
        {
            var results = new List<Listener>();
            foreach (int family in new[] { 2, 23 })
            {
                int size = 0;
                uint status = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, listenersOnly ? 3 : 5, 0);
                if (status == 50 && family == 23) continue;
                if (status != 122 && status != 0) throw new Win32Exception((int)status);
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (size < 4 || size > 64 * 1024 * 1024) throw new InvalidOperationException("Unexpected TCP table size.");
                    IntPtr buffer = Marshal.AllocHGlobal(size);
                    try
                    {
                        int capacity = size;
                        status = GetExtendedTcpTable(buffer, ref size, false, family, listenersOnly ? 3 : 5, 0);
                        if (status == 122) continue;
                        if (status != 0) throw new Win32Exception((int)status);
                        int count = Marshal.ReadInt32(buffer);
                        int stride = family == 2 ? 24 : 56;
                        if (count < 0 || count > (capacity - 4) / stride) throw new InvalidOperationException("Invalid TCP table.");
                        for (int i = 0; i < count; i++)
                        {
                            IntPtr row = IntPtr.Add(buffer, 4 + i * stride);
                            if (Marshal.ReadInt32(row, family == 2 ? 20 : 52) != pid) continue;
                            int state = Marshal.ReadInt32(row, family == 2 ? 0 : 48);
                            if (state != (listenersOnly ? 2 : 5)) continue;
                            int portOffset = family == 2 ? 8 : 20;
                            int port = (Marshal.ReadByte(row, portOffset) << 8) | Marshal.ReadByte(row, portOffset + 1);
                            var bytes = new byte[family == 2 ? 4 : 16];
                            Marshal.Copy(IntPtr.Add(row, family == 2 ? 4 : 0), bytes, 0, bytes.Length);
                            var address = family == 2 ? new IPAddress(bytes) : new IPAddress(bytes, unchecked((uint)Marshal.ReadInt32(row, 16)));
                            results.Add(new(address, port));
                        }
                        break;
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
                if (status != 0) throw new Win32Exception((int)status);
            }
            return results;
        }
    }
}
