namespace DecoupledInputFix;

// Mouse movement summed since the last engine frame, in raw device counts.
// Y grows downward, like Windows raw input and X11. Wheel uses the Windows convention:
// 120 per notch, positive away from the user.
// The accelerated values only differ on Linux, where X11 reports both.
internal readonly record struct MouseMotion(double X, double Y, double AcceleratedX, double AcceleratedY, double Wheel);

// Collects raw mouse input on a background thread, independent of the renderer.
internal static class RawMouse
{
    public const double WheelNotch = 120;

    private static readonly object Lock = new();
    private static double _x, _y, _acceleratedX, _acceleratedY, _wheel;

    public static bool Available { get; private set; }
    public static string? Error { get; private set; }
    public static string BackendName { get; private set; } = "none";
    public static bool ReportsAcceleration { get; private set; }

    // Receives exceptions thrown while handling input on the backend thread.
    public static Action<Exception>? ErrorHandler { get; set; }

    public static void Start()
    {
        Action<TaskCompletionSource<string?>> backend;
        if (OperatingSystem.IsWindows())
        {
            BackendName = "Windows raw input";
            backend = WindowsRawInput.Run;
        }
        else if (OperatingSystem.IsLinux())
        {
            BackendName = "X11 XInput2";
            ReportsAcceleration = true;
            backend = X11RawInput.Run;
        }
        else
        {
            Error = "unsupported OS";
            return;
        }

        // The backend reports null once it's listening, or a reason it couldn't start.
        var started = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                backend(started);
            }
            catch (Exception e)
            {
                started.TrySetResult(e.Message);
            }
            Available = false;
        })
        {
            IsBackground = true,
            Name = "DecoupledInputFix raw mouse",
        };
        thread.Start();

        if (!started.Task.Wait(TimeSpan.FromSeconds(5)))
            Error = "the raw input thread did not start within 5 seconds";
        else
            Error = started.Task.Result;
        Available = Error == null;
    }

    public static void Add(double x, double y, double acceleratedX, double acceleratedY, double wheel)
    {
        lock (Lock)
        {
            _x += x;
            _y += y;
            _acceleratedX += acceleratedX;
            _acceleratedY += acceleratedY;
            _wheel += wheel;
        }
    }

    public static void ReportError(Exception e) => ErrorHandler?.Invoke(e);

    public static MouseMotion Drain()
    {
        lock (Lock)
        {
            var motion = new MouseMotion(_x, _y, _acceleratedX, _acceleratedY, _wheel);
            _x = _y = _acceleratedX = _acceleratedY = _wheel = 0;
            return motion;
        }
    }
}
