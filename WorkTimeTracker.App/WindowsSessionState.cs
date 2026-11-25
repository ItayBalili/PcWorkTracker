using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WorkTimeTracker.App;

internal static class WindowsSessionState
{
    internal static (bool Locked, DateTimeOffset? LogonUtc) Read()
    {
        using var process = Process.GetCurrentProcess();
        var session = process.SessionId;
        if (!WTSQuerySessionInformation(0, session, 25, out var buffer, out var length))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot establish initial session availability.");
        bool locked;
        try
        {
            if (length < 20 || Marshal.ReadInt32(buffer) != 1)
                throw new InvalidOperationException("Unsupported WTS session information.");
            var state = Marshal.ReadInt32(buffer, 12);
            var flags = Marshal.ReadInt32(buffer, 16);
            if (flags is not (0 or 1))
                throw new InvalidOperationException("Windows did not provide a known session lock state.");
            locked = flags == 0 || state != 0;
        }
        finally { WTSFreeMemory(buffer); }
        if (!WTSQuerySessionInformation(0, session, 24, out buffer, out length))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot establish session logon time.");
        try
        {
            if (length < Marshal.SizeOf<SessionInfo>()) throw new InvalidOperationException("Incomplete WTS session information.");
            var info = Marshal.PtrToStructure<SessionInfo>(buffer);
            return (locked, info.LogonTime > 0 ? DateTimeOffset.FromFileTime(info.LogonTime).ToUniversalTime() : null);
        }
        finally { WTSFreeMemory(buffer); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SessionInfo
    {
        public uint State, SessionId, IncomingBytes, OutgoingBytes, IncomingFrames, OutgoingFrames, IncomingCompressedBytes, OutgoingCompressedBytes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string WinStationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)] public string Domain;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string UserName;
        public long ConnectTime, DisconnectTime, LastInputTime, LogonTime, CurrentTime;
    }

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(nint server, int session, int informationClass, out nint buffer, out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(nint buffer);
}
