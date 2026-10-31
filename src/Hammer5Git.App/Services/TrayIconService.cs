using System;
using System.Runtime.InteropServices;

namespace Hammer5Git.App.Services;

/// <summary>
/// Lightweight Win32 System Tray (Shell_NotifyIcon) integration for WinUI 3 unpackaged desktop app.
/// Allows minimizing to tray, showing balloon notifications when a VMAP crash/corruption is intercepted,
/// and restoring the main window on click/double-click.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;
    private const int NIF_INFO = 0x00000010;

    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;

    private const int NIIF_INFO = 0x00000001;
    private const int NIIF_WARNING = 0x00000002;
    private const int NIIF_ERROR = 0x00000003;

    private const int WM_USER = 0x0400;
    public const int WM_TRAYICON = WM_USER + 105;

    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int NIN_BALLOONUSERCLICK = WM_USER + 5;

    private const int GWLP_WNDPROC = -4;
    private const int IDI_APPLICATION = 32512;
    private const int IDI_SHIELD = 32518;

    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint MF_GRAYED = 0x0001;
    private const uint MF_ENABLED = 0x0000;

    private const uint CMD_RESTORE = 1001;
    private const uint CMD_SYNC = 1002;
    private const uint CMD_PUSH = 1003;
    private const uint CMD_EXIT = 1004;

    private readonly IntPtr _hwnd;
    private bool _isAdded;
    private IntPtr _oldWndProc;
    private readonly WndProcDelegate _wndProcDelegate;

    public event Action? RestoreRequested;
    public event Action? SyncRequested;
    public event Action? PushRequested;
    public event Action? ExitRequested;

    public Func<bool>? CanSyncProvider;
    public Func<bool>? CanPushProvider;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public TrayIconService(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _wndProcDelegate = SubclassWndProc;
        Initialize();
    }

    private void Initialize()
    {
        if (_hwnd == IntPtr.Zero)
            return;

        _oldWndProc = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));

        IntPtr hIcon = LoadIcon(IntPtr.Zero, (IntPtr)IDI_SHIELD);
        if (hIcon == IntPtr.Zero)
            hIcon = LoadIcon(IntPtr.Zero, (IntPtr)IDI_APPLICATION);

        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = hIcon,
            szTip = "CS2 Hammer5Git (双击打开面板)"
        };

        _isAdded = Shell_NotifyIcon(NIM_ADD, ref data);
    }

    public void ShowNotification(string title, string message, bool isError = false)
    {
        if (!_isAdded)
            return;

        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_INFO,
            szInfoTitle = title.Length > 60 ? title[..60] : title,
            szInfo = message.Length > 250 ? message[..250] : message,
            dwInfoFlags = isError ? NIIF_ERROR : NIIF_INFO
        };

        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private IntPtr SubclassWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_TRAYICON)
        {
            int eventId = (int)(lParam.ToInt64() & 0xFFFF);
            if (eventId == WM_LBUTTONUP || eventId == WM_LBUTTONDBLCLK || eventId == NIN_BALLOONUSERCLICK)
            {
                RestoreRequested?.Invoke();
            }
            else if (eventId == WM_RBUTTONUP)
            {
                ShowContextMenu();
            }
        }

        return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        if (!GetCursorPos(out POINT pt))
            return;

        IntPtr hMenu = CreatePopupMenu();
        if (hMenu == IntPtr.Zero)
            return;

        try
        {
            AppendMenu(hMenu, MF_STRING, CMD_RESTORE, "显示主界面");

            bool canSync = CanSyncProvider?.Invoke() ?? true;
            uint syncFlags = MF_STRING | (canSync ? MF_ENABLED : MF_GRAYED);
            AppendMenu(hMenu, syncFlags, CMD_SYNC, "立即同步当前项目");

            bool canPush = CanPushProvider?.Invoke() ?? true;
            uint pushFlags = MF_STRING | (canPush ? MF_ENABLED : MF_GRAYED);
            AppendMenu(hMenu, pushFlags, CMD_PUSH, "推送至远程仓库");

            AppendMenu(hMenu, MF_SEPARATOR, 0, null);
            AppendMenu(hMenu, MF_STRING, CMD_EXIT, "退出程序");

            SetForegroundWindow(_hwnd);
            uint selected = TrackPopupMenuEx(hMenu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.X, pt.Y, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, 0x0000 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero);

            switch (selected)
            {
                case CMD_RESTORE:
                    RestoreRequested?.Invoke();
                    break;
                case CMD_SYNC:
                    SyncRequested?.Invoke();
                    break;
                case CMD_PUSH:
                    PushRequested?.Invoke();
                    break;
                case CMD_EXIT:
                    ExitRequested?.Invoke();
                    break;
            }
        }
        finally
        {
            DestroyMenu(hMenu);
        }
    }

    public void Dispose()
    {
        if (_isAdded)
        {
            var data = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1
            };
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _isAdded = false;
        }

        if (_oldWndProc != IntPtr.Zero && _hwnd != IntPtr.Zero)
        {
            SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _oldWndProc);
            _oldWndProc = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpdata);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
}
