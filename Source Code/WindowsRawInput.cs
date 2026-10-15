using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DecoupledInputFix;

// Reads the mouse through Windows raw input on a hidden message-only window.
// RIDEV_INPUTSINK delivers input even though the engine process never has focus, so
// movement is only counted while a Resonite window is in the foreground.
internal static unsafe class WindowsRawInput
{
    private const uint WM_INPUT = 0x00FF;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIM_TYPEMOUSE = 0;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    private const ushort HID_USAGE_GENERIC_MOUSE = 0x02;
    private const ushort MOUSE_MOVE_ABSOLUTE = 0x0001;
    private const ushort RI_MOUSE_WHEEL = 0x0400;
    private static readonly nint HWND_MESSAGE = -3;

    private const string WindowClassName = "DecoupledInputFixRawInput";

    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;
    private static nint _lastForeground = -1;
    private static bool _gameHasFocus;

    // Counts input no matter which window is in front. Only for tests.
    internal static bool IgnoreFocus { get; set; }

    public static void Run(TaskCompletionSource<string?> started)
    {
        nint window = CreateMessageWindow();
        if (window == 0)
        {
            started.SetResult($"could not create the raw input window (error {Marshal.GetLastPInvokeError()})");
            return;
        }

        var device = new RAWINPUTDEVICE
        {
            usUsagePage = HID_USAGE_PAGE_GENERIC,
            usUsage = HID_USAGE_GENERIC_MOUSE,
            dwFlags = RIDEV_INPUTSINK,
            hwndTarget = window,
        };
        if (!RegisterRawInputDevices(&device, 1, (uint)sizeof(RAWINPUTDEVICE)))
        {
            started.SetResult($"RegisterRawInputDevices failed (error {Marshal.GetLastPInvokeError()})");
            return;
        }

        started.SetResult(null);
        MSG msg;
        while (GetMessageW(&msg, 0, 0, 0) > 0)
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    private static nint CreateMessageWindow()
    {
        nint instance = GetModuleHandleW(null);
        fixed (char* className = WindowClassName)
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WindowProc,
                hInstance = instance,
                lpszClassName = className,
            };
            if (RegisterClassExW(&windowClass) == 0)
                return 0;
            return CreateWindowExW(0, className, className, 0, 0, 0, 0, 0, HWND_MESSAGE, 0, instance, 0);
        }
    }

    [UnmanagedCallersOnly]
    private static nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == WM_INPUT)
        {
            // An exception must never unwind into user32.
            try
            {
                OnInput(lParam);
            }
            catch (Exception e)
            {
                RawMouse.ReportError(e);
            }
        }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private static void OnInput(nint rawInputHandle)
    {
        RAWINPUT input;
        uint size = (uint)sizeof(RAWINPUT);
        if (GetRawInputData(rawInputHandle, RID_INPUT, &input, &size, (uint)sizeof(RAWINPUTHEADER)) == uint.MaxValue)
            return;
        if (input.header.dwType != RIM_TYPEMOUSE || !GameHasFocus())
            return;

        RAWMOUSE mouse = input.mouse;
        // Tablets, touch screens and remote desktop report absolute positions, not movement.
        bool relative = (mouse.usFlags & MOUSE_MOVE_ABSOLUTE) == 0;
        double x = relative ? mouse.lLastX : 0;
        double y = relative ? mouse.lLastY : 0;
        double wheel = (mouse.usButtonFlags & RI_MOUSE_WHEEL) != 0 ? mouse.usButtonData : 0;
        RawMouse.Add(x, y, x, y, wheel);
    }

    private static bool GameHasFocus()
    {
        if (IgnoreFocus)
            return true;
        nint foreground = GetForegroundWindow();
        if (foreground != _lastForeground)
        {
            _lastForeground = foreground;
            _gameHasFocus = IsResoniteWindow(foreground);
        }
        return _gameHasFocus;
    }

    private static bool IsResoniteWindow(nint window)
    {
        if (window == 0)
            return false;
        GetWindowThreadProcessId(window, out uint processId);
        if (processId == OwnProcessId)
            return true;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName.StartsWith("Renderite.Renderer", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> lpfnWndProc;
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
    internal struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public nint hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public nint hDevice;
        public nint wParam;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct RAWMOUSE
    {
        [FieldOffset(0)] public ushort usFlags;
        [FieldOffset(4)] public uint ulButtons;
        [FieldOffset(4)] public ushort usButtonFlags;
        [FieldOffset(6)] public short usButtonData;
        [FieldOffset(8)] public uint ulRawButtons;
        [FieldOffset(12)] public int lLastX;
        [FieldOffset(16)] public int lLastY;
        [FieldOffset(20)] public uint ulExtraInformation;
    }

    // RAWINPUT with only the mouse member of its union, which is all we register for.
    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWINPUT
    {
        public RAWINPUTHEADER header;
        public RAWMOUSE mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
        public uint lPrivate;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern ushort RegisterClassExW(WNDCLASSEXW* windowClass);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, char* className, char* windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE* devices, uint count, uint size);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(nint rawInput, uint command, RAWINPUT* data, uint* size, uint headerSize);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(MSG* msg, nint hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern int TranslateMessage(MSG* msg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(MSG* msg);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
}
