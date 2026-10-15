using System.Runtime.InteropServices;

namespace DecoupledInputFix;

// Reads the mouse through XInput2 raw events on Linux. Resonite runs the engine natively there
// and the renderer under Proton, which is an X client (directly or through XWayland).
// The structs below mirror Xlib.h and XInput2.h for 64-bit Linux, where C's long and
// X's Time/Atom are 8 bytes (nuint).
internal static unsafe class X11RawInput
{
    private const string LibX11 = "libX11.so.6";
    private const string LibXi = "libXi.so.6";

    private const int GenericEvent = 35;
    private const int XI_HierarchyChanged = 11;
    private const int XI_RawButtonPress = 15;
    private const int XI_RawMotion = 17;
    private const int XIAllDevices = 0;
    private const int XISlavePointer = 3;
    private const int XIValuatorClass = 2;
    private const int XIScrollClass = 3;
    private const int XIModeRelative = 0;
    private const int XIScrollTypeVertical = 1;
    private const int XIPointerEmulated = 1 << 16;
    private const int ScrollUpButton = 4;
    private const int ScrollDownButton = 5;

    private sealed class PointerDevice
    {
        public bool MovesRelative;
        public int ScrollValuator = -1;
        public double ScrollIncrement;
    }

    // Only slave pointers are kept. Master pointers repeat the same events.
    private static readonly Dictionary<int, PointerDevice> Pointers = new();
    private static nint _display;

    public static void Run(TaskCompletionSource<string?> started)
    {
        _display = XOpenDisplay(null);
        if (_display == 0)
        {
            started.SetResult($"could not open X display '{Environment.GetEnvironmentVariable("DISPLAY")}'");
            return;
        }

        int opcode, firstEvent, firstError;
        if (XQueryExtension(_display, "XInputExtension", &opcode, &firstEvent, &firstError) == 0)
        {
            started.SetResult("the X server has no XInput extension");
            return;
        }

        // XI 2.0 clients stop getting raw events while another client grabs the pointer,
        // which is exactly what Wine does during mouse look. 2.1 gets them regardless.
        int major = 2, minor = 1;
        if (XIQueryVersion(_display, &major, &minor) != 0 || major < 2 || (major == 2 && minor < 1))
        {
            started.SetResult($"XInput 2.1 is not available (server has {major}.{minor})");
            return;
        }

        RefreshDevices();

        const int maskLength = 4;
        byte* bits = stackalloc byte[maskLength];
        new Span<byte>(bits, maskLength).Clear();
        SetMaskBit(bits, XI_HierarchyChanged);
        SetMaskBit(bits, XI_RawMotion);
        SetMaskBit(bits, XI_RawButtonPress);
        var mask = new XIEventMask { deviceid = XIAllDevices, mask_len = maskLength, mask = bits };
        XISelectEvents(_display, XDefaultRootWindow(_display), &mask, 1);
        XFlush(_display);

        started.SetResult(null);

        XEvent xevent;
        var cookie = (XGenericEventCookie*)&xevent;
        while (true)
        {
            XNextEvent(_display, &xevent);
            if (cookie->type != GenericEvent || cookie->extension != opcode || XGetEventData(_display, cookie) == 0)
                continue;
            try
            {
                OnEvent(cookie->evtype, cookie->data);
            }
            catch (Exception e)
            {
                RawMouse.ReportError(e);
            }
            finally
            {
                XFreeEventData(_display, cookie);
            }
        }
    }

    private static void OnEvent(int eventType, void* data)
    {
        if (eventType == XI_HierarchyChanged)
        {
            RefreshDevices();
            return;
        }

        var raw = (XIRawEvent*)data;
        if (!Pointers.TryGetValue(raw->deviceid, out PointerDevice? device))
            return;

        // The server converts scrolling both ways: a wheel click also produces a scroll valuator
        // motion, and smooth scrolling also produces button 4/5 clicks. The copies are marked as
        // emulated, so skipping them counts every scroll once.
        bool emulated = (raw->flags & XIPointerEmulated) != 0;

        if (eventType == XI_RawButtonPress)
        {
            if (!emulated && raw->detail == ScrollUpButton)
                RawMouse.Add(0, 0, 0, 0, RawMouse.WheelNotch);
            else if (!emulated && raw->detail == ScrollDownButton)
                RawMouse.Add(0, 0, 0, 0, -RawMouse.WheelNotch);
            return;
        }

        if (eventType != XI_RawMotion)
            return;

        // values/raw_values are packed: one entry per valuator set in the mask, in order.
        double x = 0, y = 0, rawX = 0, rawY = 0, scroll = 0;
        XIValuatorState valuators = raw->valuators;
        int index = 0;
        for (int valuator = 0; valuator < valuators.mask_len * 8; valuator++)
        {
            if (!IsMaskBitSet(valuators.mask, valuator))
                continue;
            if (valuator == 0)
            {
                x = valuators.values[index];
                rawX = raw->raw_values[index];
            }
            else if (valuator == 1)
            {
                y = valuators.values[index];
                rawY = raw->raw_values[index];
            }
            else if (valuator == device.ScrollValuator && !emulated)
            {
                scroll = valuators.values[index];
            }
            index++;
        }

        if (!device.MovesRelative)
            x = y = rawX = rawY = 0;

        // One increment is one notch, and a positive delta scrolls down (XI 2.1 spec).
        double wheel = scroll != 0 ? -scroll / device.ScrollIncrement * RawMouse.WheelNotch : 0;

        if (x != 0 || y != 0 || rawX != 0 || rawY != 0 || wheel != 0)
            RawMouse.Add(rawX, rawY, x, y, wheel);
    }

    private static void RefreshDevices()
    {
        Pointers.Clear();
        int count;
        XIDeviceInfo* devices = XIQueryDevice(_display, XIAllDevices, &count);
        if (devices == null)
            return;
        try
        {
            for (int i = 0; i < count; i++)
            {
                if (devices[i].use == XISlavePointer)
                    Pointers[devices[i].deviceid] = DescribePointer(devices[i]);
            }
        }
        finally
        {
            XIFreeDeviceInfo(devices);
        }
    }

    private static PointerDevice DescribePointer(in XIDeviceInfo info)
    {
        var relativeValuators = new HashSet<int>();
        for (int i = 0; i < info.num_classes; i++)
        {
            var valuator = (XIValuatorClassInfo*)info.classes[i];
            if (valuator->type == XIValuatorClass && valuator->mode == XIModeRelative)
                relativeValuators.Add(valuator->number);
        }

        // Tablets and XWayland's main pointer report absolute positions. Only devices whose
        // X and Y (valuators 0 and 1) are relative count as mouse movement.
        var device = new PointerDevice { MovesRelative = relativeValuators.Contains(0) && relativeValuators.Contains(1) };

        for (int i = 0; i < info.num_classes; i++)
        {
            var scroll = (XIScrollClassInfo*)info.classes[i];
            if (scroll->type == XIScrollClass && scroll->scroll_type == XIScrollTypeVertical
                && scroll->increment != 0 && relativeValuators.Contains(scroll->number))
            {
                device.ScrollValuator = scroll->number;
                device.ScrollIncrement = scroll->increment;
            }
        }
        return device;
    }

    private static void SetMaskBit(byte* mask, int bit) => mask[bit >> 3] |= (byte)(1 << (bit & 7));

    private static bool IsMaskBitSet(byte* mask, int bit) => (mask[bit >> 3] & (1 << (bit & 7))) != 0;

    // Xlib's XEvent union is 24 longs.
    [StructLayout(LayoutKind.Sequential)]
    internal struct XEvent
    {
        public fixed long pad[24];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XGenericEventCookie
    {
        public int type;
        public nuint serial;
        public int send_event;
        public nint display;
        public int extension;
        public int evtype;
        public uint cookie;
        public void* data;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XIEventMask
    {
        public int deviceid;
        public int mask_len;
        public byte* mask;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XIValuatorState
    {
        public int mask_len;
        public byte* mask;
        public double* values;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XIRawEvent
    {
        public int type;
        public nuint serial;
        public int send_event;
        public nint display;
        public int extension;
        public int evtype;
        public nuint time;
        public int deviceid;
        public int sourceid;
        public int detail;
        public int flags;
        public XIValuatorState valuators;
        public double* raw_values;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XIDeviceInfo
    {
        public int deviceid;
        public byte* name;
        public int use;
        public int attachment;
        public int enabled;
        public int num_classes;
        public void** classes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XIValuatorClassInfo
    {
        public int type;
        public int sourceid;
        public int number;
        public nuint label;
        public double min;
        public double max;
        public double value;
        public int resolution;
        public int mode;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XIScrollClassInfo
    {
        public int type;
        public int sourceid;
        public int number;
        public int scroll_type;
        public double increment;
        public int flags;
    }

    [DllImport(LibX11)]
    private static extern nint XOpenDisplay(byte* displayName);

    [DllImport(LibX11)]
    private static extern int XQueryExtension(nint display, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int* opcode, int* firstEvent, int* firstError);

    [DllImport(LibX11)]
    private static extern nuint XDefaultRootWindow(nint display);

    [DllImport(LibX11)]
    private static extern int XFlush(nint display);

    [DllImport(LibX11)]
    private static extern int XNextEvent(nint display, XEvent* xevent);

    [DllImport(LibX11)]
    private static extern int XGetEventData(nint display, XGenericEventCookie* cookie);

    [DllImport(LibX11)]
    private static extern void XFreeEventData(nint display, XGenericEventCookie* cookie);

    [DllImport(LibXi)]
    private static extern int XIQueryVersion(nint display, int* major, int* minor);

    [DllImport(LibXi)]
    private static extern int XISelectEvents(nint display, nuint window, XIEventMask* masks, int count);

    [DllImport(LibXi)]
    private static extern XIDeviceInfo* XIQueryDevice(nint display, int deviceId, int* count);

    [DllImport(LibXi)]
    private static extern void XIFreeDeviceInfo(XIDeviceInfo* info);
}
