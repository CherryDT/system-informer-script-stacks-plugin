using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ScriptStacks;

public static class ProcessIdentity
{
    // Holding this handle keeps the process object alive, preventing its PID
    // from being reused during an operation. Identity checks need no VM access.
    public static SafeProcessHandle Open(Request request)
    {
        if (request.Pid <= 0 || request.CreationTime == 0)
            throw new ArgumentException("A positive PID and its Windows process creation time are required.");

        SafeProcessHandle handle = OpenProcess(0x00100000 | 0x1000, false, request.Pid);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "Cannot open the selected process. It may have exited or access may be denied.");
        }
        try
        {
            if (!GetProcessTimes(handle, out ulong creationTime, out _, out _, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read the target process creation time.");
            if (creationTime != request.CreationTime)
                throw new InvalidOperationException("The selected process has exited and its PID belongs to a different process. Reopen process properties.");
            uint wait = WaitForSingleObject(handle, 0);
            if (wait == 0)
                throw new InvalidOperationException("The selected process has exited.");
            if (wait == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot verify whether the selected process is still running.");
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static void Verify(Request request)
    {
        using SafeProcessHandle handle = Open(request);
    }

    internal static void EnableDebugPrivilege()
    {
        // Elevation remains a user choice. Enabling a privilege that is already
        // present helps access elevated targets; absence is deliberately harmless.
        if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out SafeAccessTokenHandle token))
            return;
        using (token)
        {
            if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out Luid luid))
                return;
            TokenPrivileges privileges = new() { Count = 1, Luid = luid, Attributes = 0x00000002 };
            _ = AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out ulong creation, out ulong exit, out ulong kernel, out ulong user);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges privileges, uint length, IntPtr previous, IntPtr returnLength);
}