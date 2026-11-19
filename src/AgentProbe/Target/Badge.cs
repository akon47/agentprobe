using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AgentProbe.Native;

namespace AgentProbe.Target;

/// <summary>
/// The "this PC is being controlled remotely" banner in the top-left corner of the screen.
/// <para>
/// It is always shown while a developer PC is connected and there is deliberately no switch (local or remote) to hide it:
/// whoever sits at the PC must be able to see that it is being driven. The window is click-through and excluded from
/// screen capture (WDA_EXCLUDEFROMCAPTURE), so it never gets in the way of the app under test or of screenshots.
/// </para>
/// </summary>
internal static unsafe partial class Badge
{
    private const uint WmPaint = 0x000F, WmTimer = 0x0113, WmDestroy = 0x0002, WmAppShow = 0x8001, WmAppHide = 0x8002;
    private static readonly object Gate = new();
    private static nint _hwnd;
    private static string _title = "";
    private static string _detail = "";
    private static int _scale = 100;

    public static void Show(string title, string detail)
    {
        lock (Gate)
        {
            _title = title;
            _detail = detail;
            if (_hwnd == 0)
            {
                var ready = new ManualResetEventSlim();
                new Thread(() => WindowThread(ready)) { IsBackground = true, Name = "badge" }.Start();
                ready.Wait(3000);
            }
        }

        if (_hwnd != 0) PostMessageW(_hwnd, WmAppShow, 0, 0);
    }

    public static void SetDetail(string detail)
    {
        _detail = detail;
        if (_hwnd != 0) InvalidateRect(_hwnd, 0, 1);
    }

    public static void Hide()
    {
        if (_hwnd != 0) PostMessageW(_hwnd, WmAppHide, 0, 0);
    }

    private static void WindowThread(ManualResetEventSlim ready)
    {
        try
        {
            _scale = (int)(GetDpiForSystem() * 100 / 96);
            var instance = GetModuleHandleW(0);
            fixed (char* cls = "agentprobe.badge")
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    lpfnWndProc = &WndProc,
                    hInstance = instance,
                    lpszClassName = cls,
                };
                RegisterClassExW(&wc);

                // topmost | toolwindow (no taskbar button) | noactivate | layered | transparent (clicks pass through)
                const uint exStyle = 0x00000008 | 0x00000080 | 0x08000000 | 0x00080000 | 0x00000020;
                _hwnd = CreateWindowExW(exStyle, cls, cls, 0x80000000, Px(12), Px(12), Px(470), Px(48), 0, 0, instance, 0);
            }

            if (_hwnd != 0)
            {
                SetLayeredWindowAttributes(_hwnd, 0, 236, 2);
                SetWindowDisplayAffinity(_hwnd, 0x11); // WDA_EXCLUDEFROMCAPTURE (Windows 10 2004+); ignored on older builds
                SetTimer(_hwnd, 1, 2000, 0);
            }
        }
        finally
        {
            ready.Set();
        }

        if (_hwnd == 0) return;
        MSG msg;
        while (GetMessageW(&msg, 0, 0, 0) > 0)
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    private static int Px(int v) => v * _scale / 100;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WmAppShow:
                SetWindowPos(hwnd, -1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040); // topmost, nosize, nomove, noactivate, show
                InvalidateRect(hwnd, 0, 1);
                return 0;
            case WmAppHide:
                Win32.ShowWindow(hwnd, 0);
                return 0;
            case WmTimer:
                // Other topmost windows (full-screen apps, the taskbar) can cover the badge: put it back on top.
                if (Win32.IsWindowVisible(hwnd)) SetWindowPos(hwnd, -1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
                return 0;
            case WmPaint:
                Paint(hwnd);
                return 0;
            case WmDestroy:
                PostQuitMessage(0);
                return 0;
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private static void Paint(nint hwnd)
    {
        PAINTSTRUCT ps;
        var hdc = BeginPaint(hwnd, &ps);
        Win32.GetWindowRect(hwnd, out var wr);
        var rc = new RECT { Left = 0, Top = 0, Right = wr.Width, Bottom = wr.Height };

        var back = CreateSolidBrush(0x001E26B3); // BGR: a clear warning red
        FillRect(hdc, &rc, back);
        Win32.DeleteObject(back);

        SetBkMode(hdc, 1);
        SetTextColor(hdc, 0x00FFFFFF);
        fixed (char* face = "Segoe UI")
        {
            var bold = CreateFontW(-Px(14), 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 5, 0, face);
            var normal = CreateFontW(-Px(12), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, face);
            var old = Win32.SelectObject(hdc, bold);
            var line1 = new RECT { Left = Px(12), Top = Px(5), Right = rc.Right - Px(8), Bottom = Px(25) };
            DrawLine(hdc, "● " + _title, &line1);
            Win32.SelectObject(hdc, normal);
            var line2 = new RECT { Left = Px(12), Top = Px(25), Right = rc.Right - Px(8), Bottom = rc.Bottom - Px(3) };
            DrawLine(hdc, _detail, &line2);
            Win32.SelectObject(hdc, old);
            Win32.DeleteObject(bold);
            Win32.DeleteObject(normal);
        }

        EndPaint(hwnd, &ps);
    }

    private static void DrawLine(nint hdc, string text, RECT* rc)
    {
        fixed (char* p = text) DrawTextW(hdc, p, text.Length, rc, 0x0020 | 0x8000 | 0x0800); // single line, end ellipsis, no prefix
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint> lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public char* lpszMenuName;
        public char* lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int x;
        public int y;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public nint hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;
        public fixed byte rgbReserved[32];
    }

    [LibraryImport("user32.dll")]
    private static partial ushort RegisterClassExW(WNDCLASSEXW* wc);

    [LibraryImport("user32.dll")]
    private static partial nint CreateWindowExW(uint exStyle, char* className, char* windowName, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(MSG* msg, nint hwnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    private static partial int TranslateMessage(MSG* msg);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(MSG* msg);

    [LibraryImport("user32.dll")]
    private static partial int PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial void PostQuitMessage(int code);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowDisplayAffinity(nint hwnd, uint affinity);

    [LibraryImport("user32.dll")]
    private static partial int SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    private static partial int InvalidateRect(nint hwnd, nint rect, int erase);

    [LibraryImport("user32.dll")]
    private static partial nuint SetTimer(nint hwnd, nuint id, uint elapse, nint proc);

    [LibraryImport("user32.dll")]
    private static partial nint BeginPaint(nint hwnd, PAINTSTRUCT* ps);

    [LibraryImport("user32.dll")]
    private static partial int EndPaint(nint hwnd, PAINTSTRUCT* ps);

    [LibraryImport("user32.dll")]
    private static partial int FillRect(nint hdc, RECT* rect, nint brush);

    [LibraryImport("user32.dll")]
    private static partial int DrawTextW(nint hdc, char* text, int count, RECT* rect, uint format);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForSystem();

    [LibraryImport("kernel32.dll")]
    private static partial nint GetModuleHandleW(nint name);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateSolidBrush(uint color);

    [LibraryImport("gdi32.dll")]
    private static partial int SetBkMode(nint hdc, int mode);

    [LibraryImport("gdi32.dll")]
    private static partial uint SetTextColor(nint hdc, uint color);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, char* face);
}
