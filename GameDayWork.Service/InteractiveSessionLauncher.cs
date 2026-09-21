using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class InteractiveSessionLauncher
{
    public static IReadOnlyList<int> GetActiveSessionIds()
    {
        var result = new List<int>();
        if (!WTSEnumerateSessions(nint.Zero, 0, 1, out var pointer, out var count)) return result;
        try
        {
            var size = Marshal.SizeOf<WtsSessionInfo>();
            for (var index = 0; index < count; index++)
            {
                var info = Marshal.PtrToStructure<WtsSessionInfo>(pointer + index * size);
                if (info.State == WtsConnectState.Active) result.Add(info.SessionId);
            }
        }
        finally { WTSFreeMemory(pointer); }
        return result;
    }

    public static Process Start(int sessionId, string executablePath, string arguments)
    {
        if (!WTSQueryUserToken((uint)sessionId, out var userToken)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法取得交互用户令牌");
        nint primaryToken = nint.Zero;
        nint environment = nint.Zero;
        try
        {
            if (!DuplicateTokenEx(userToken, 0xF01FF, nint.Zero, 2, 1, out primaryToken)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法复制交互用户令牌");
            if (!CreateEnvironmentBlock(out environment, primaryToken, false)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建用户环境");
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = "winsta0\\default" };
            var commandLine = $"\"{executablePath}\" {arguments}";
            if (!CreateProcessAsUser(primaryToken, null, commandLine, nint.Zero, nint.Zero, false, 0x00000400 | 0x00000010,
                    environment, Path.GetDirectoryName(executablePath), ref startup, out var processInfo))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法在用户桌面启动 GameDayWork");
            try { return Process.GetProcessById((int)processInfo.ProcessId); }
            finally { CloseHandle(processInfo.Thread); CloseHandle(processInfo.Process); }
        }
        finally
        {
            if (environment != nint.Zero) DestroyEnvironmentBlock(environment);
            if (primaryToken != nint.Zero) CloseHandle(primaryToken);
            CloseHandle(userToken);
        }
    }

    private enum WtsConnectState { Active, Connected, ConnectQuery, Shadow, Disconnected, Idle, Listen, Reset, Down, Init }
    [StructLayout(LayoutKind.Sequential)] private struct WtsSessionInfo { public int SessionId; public nint WinStationName; public WtsConnectState State; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo { public int Size; public string? Reserved; public string? Desktop; public string? Title; public int X; public int Y; public int XSize; public int YSize; public int XCountChars; public int YCountChars; public int FillAttribute; public int Flags; public short ShowWindow; public short Reserved2; public nint ReservedPointer; public nint StandardInput; public nint StandardOutput; public nint StandardError; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public nint Process; public nint Thread; public uint ProcessId; public uint ThreadId; }
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSEnumerateSessions(nint server, int reserved, int version, out nint sessionInfo, out int count);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(nint memory);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint sessionId, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(nint existingToken, uint desiredAccess, nint tokenAttributes, int impersonationLevel, int tokenType, out nint newToken);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out nint environment, nint token, bool inherit);
    [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(nint environment);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessAsUser(nint token, string? applicationName, string commandLine, nint processAttributes, nint threadAttributes, bool inheritHandles, uint creationFlags, nint environment, string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
