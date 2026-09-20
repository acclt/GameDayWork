using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace GameOrchestrator.Services;

public sealed class StartupService
{
    public const string StartupArgument = "--startup";
    private const string ShortcutFileName = "GameDayWork 开机自启.lnk";

    public string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        ShortcutFileName);

    public void Apply(bool enabled)
    {
        if (!enabled)
        {
            if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
            return;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            throw new InvalidOperationException("无法确定 GameDayWork 程序路径，未能创建开机启动项。");

        var startupDirectory = Path.GetDirectoryName(ShortcutPath)!;
        Directory.CreateDirectory(startupDirectory);

        object? shellLinkObject = null;
        try
        {
            shellLinkObject = new ShellLink();
            var shellLink = (IShellLinkW)shellLinkObject;
            shellLink.SetPath(executablePath);
            shellLink.SetArguments(StartupArgument);
            shellLink.SetWorkingDirectory(AppContext.BaseDirectory);
            shellLink.SetDescription("GameDayWork 开机自启");
            shellLink.SetIconLocation(executablePath, 0);
            ((IPersistFile)shellLinkObject).Save(ShortcutPath, true);
        }
        finally
        {
            if (shellLinkObject is not null && Marshal.IsComObject(shellLinkObject))
                Marshal.FinalReleaseComObject(shellLinkObject);
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink;

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, nint findData, uint flags);
        void GetIdList(out nint itemIdList);
        void SetIdList(nint itemIdList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCommand(out int showCommand);
        void SetShowCommand(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int iconPathLength, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(nint window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
