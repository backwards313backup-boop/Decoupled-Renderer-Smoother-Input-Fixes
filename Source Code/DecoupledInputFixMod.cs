using FrooxEngine;
using HarmonyLib;
using Renderite.Shared;
using ResoniteModLoader;

namespace DecoupledInputFix;

public sealed class DecoupledInputFixMod : ResoniteMod
{
    public override string Name => "DecoupledInputFix";
    public override string Author => "backwards";
    public override string Version => "1.0.0";
    public override string Link => "https://github.com/backwards313backup-boop";

    [AutoRegisterConfigKey] private static readonly ModConfigurationKey<bool> EnabledKey =
        new("enabled", "Rebuild mouse movement from raw input while the renderer is decoupled. The renderer only reports the movement of its last frame, so the movement from every other frame it draws between engine updates is lost.", () => true);
    [AutoRegisterConfigKey] private static readonly ModConfigurationKey<bool> FixScrollKey =
        new("fix_scroll", "Also rebuild scroll wheel movement, so wheel notches between engine updates aren't dropped.", () => true);
    [AutoRegisterConfigKey] private static readonly ModConfigurationKey<float> ScaleKey =
        new("mouse_scale", "Multiplier for the rebuilt mouse movement. 1 matches the game with decoupling off.", () => 1f, valueValidator: x => x > 0f && x <= 10f);
    [AutoRegisterConfigKey] private static readonly ModConfigurationKey<bool> ForceDecoupleKey =
        new("force_decouple_on_start", "Turn on the game's Force Decouple setting at startup. The game doesn't save that setting, so it's otherwise off every launch. You can still turn it off during a session.", () => true);

    private static ModConfiguration? _config;
    private static int _reportedErrors;

    internal static bool Enabled { get; private set; } = true;
    internal static bool FixScroll { get; private set; } = true;
    internal static float Scale { get; private set; } = 1f;
    internal static bool ForceDecoupleOnStart { get; private set; } = true;

    public override void OnEngineInit()
    {
        _config = GetConfiguration();
        LoadConfig();
        if (_config != null)
        {
            _config.OnThisConfigurationChanged += _ => LoadConfig();
            _config.Save(saveDefaultValues: true);
        }

        RawMouse.ErrorHandler = Report;
        RawMouse.Start();
        new Harmony("local.decoupledinputfix").PatchAll();

        string input = RawMouse.Available
            ? $"{RawMouse.BackendName} ok"
            : $"raw input unavailable ({RawMouse.Error}), game input unchanged";
        Msg($"DecoupledInputFix loaded: {input}, enabled={Enabled} fix_scroll={FixScroll} mouse_scale={Scale} force_decouple_on_start={ForceDecoupleOnStart}");
    }

    private static void LoadConfig()
    {
        Enabled = _config?.GetValue(EnabledKey) ?? true;
        FixScroll = _config?.GetValue(FixScrollKey) ?? true;
        Scale = _config?.GetValue(ScaleKey) ?? 1f;
        ForceDecoupleOnStart = _config?.GetValue(ForceDecoupleKey) ?? true;
    }

    internal static void Log(string message) => Msg(message);

    internal static void Report(Exception e)
    {
        if (Interlocked.Increment(ref _reportedErrors) <= 10)
            Error($"DecoupledInputFix error: {e}");
    }
}

// Turns Force Decouple on once for each decoupling settings component the game creates.
// The game marks ForceDecouple non-persistent, so it starts off every launch. This hooks the
// engine's own change listener instead of registering one through Settings: registering early
// adds the setting type to its category first, which moves it to the top of the Graphics page.
// Only the first call per component acts, so turning it off during a session sticks.
[HarmonyPatch(typeof(RenderSystem), "OnDecouplingSettingsChanged")]
internal static class ForceDecouplePatch
{
    private static RendererDecouplingSettings? _handled;

    private static void Postfix(RendererDecouplingSettings settings)
    {
        if (!DecoupledInputFixMod.ForceDecoupleOnStart || settings == null || settings == _handled)
            return;
        _handled = settings;
        settings.RunSynchronously(() =>
        {
            settings.ForceDecouple.Value = true;
            DecoupledInputFixMod.Log("DecoupledInputFix: Force Decouple turned on");
        });
    }
}

// Runs as each engine frame starts, before the renderer's input is processed.
[HarmonyPatch(typeof(Engine), "HandleFrameStart")]
internal static class FrameStartPatch
{
    private static void Prefix(FrameStartData startData)
    {
        try
        {
            InputRebuilder.Apply(startData);
        }
        catch (Exception e)
        {
            DecoupledInputFixMod.Report(e);
        }
    }
}

// Replaces the renderer's mouse delta with the full movement since the last engine frame.
// While decoupled, the renderer draws several frames per engine update but sends only the
// delta of the last one (Unity's Mouse.delta), so the rest of the movement is lost.
// On coupled frames its value is complete, which makes those frames a clean reference.
internal static class InputRebuilder
{
    private const int ComparisonFrames = 300;

    // Renderer scroll units per wheel notch, learned from the first scroll it reports
    // (Unity's input system uses 120 on Windows, but 1 on some platforms).
    private static float _scrollPerNotch;

    private static bool _useAccelerated;
    private static readonly AxisFit Unaccelerated = new();
    private static readonly AxisFit Accelerated = new();
    private static int _comparedFrames;
    private static bool _sourceChosen;

    public static void Apply(FrameStartData startData)
    {
        if (!RawMouse.Available)
            return;

        // Always drain, so movement from an unfocused or coupled frame never leaks into a later one.
        MouseMotion motion = RawMouse.Drain();

        MouseState? mouse = startData.inputs?.mouse;
        if (!DecoupledInputFixMod.Enabled || mouse is not { isActive: true })
            return;
        if (startData.inputs!.window is { isWindowFocused: false })
            return;

        RenderVector2 rendererDelta = mouse.directDelta;
        RenderVector2 rendererScroll = mouse.scrollWheelDelta;
        if (_scrollPerNotch == 0 && rendererScroll.y != 0)
            _scrollPerNotch = Math.Abs(rendererScroll.y) >= 10 ? 120 : 1;

        int renderedFrames = startData.performance?.renderedFramesSinceLast ?? 1;
        if (renderedFrames <= 1)
        {
            ChooseMotionSource(rendererDelta, motion);
            return;
        }

        double x = _useAccelerated ? motion.AcceleratedX : motion.X;
        double y = _useAccelerated ? motion.AcceleratedY : motion.Y;

        // If the renderer saw movement that raw input didn't (a tablet or remote desktop), keep the renderer's.
        bool rawMoved = x != 0 || y != 0;
        bool rendererMoved = rendererDelta.x != 0 || rendererDelta.y != 0;
        if (rawMoved || !rendererMoved)
        {
            // Raw Y grows downward, the renderer's grows upward.
            float scale = DecoupledInputFixMod.Scale;
            mouse.directDelta = new RenderVector2((float)(x * scale), (float)(-y * scale));
        }

        if (DecoupledInputFixMod.FixScroll && _scrollPerNotch != 0 && (motion.Wheel != 0 || rendererScroll.y == 0))
        {
            float notches = (float)(motion.Wheel / RawMouse.WheelNotch);
            mouse.scrollWheelDelta = new RenderVector2(rendererScroll.x, notches * _scrollPerNotch);
        }
    }

    // X11 reports both accelerated and unaccelerated motion, and under Proton it depends on Wine
    // which of the two Unity sees. Coupled frames show which one the renderer's delta matches.
    private static void ChooseMotionSource(RenderVector2 rendererDelta, MouseMotion motion)
    {
        if (!RawMouse.ReportsAcceleration || _sourceChosen || (motion.X == 0 && motion.Y == 0))
            return;

        Unaccelerated.Add(rendererDelta, motion.X, -motion.Y);
        Accelerated.Add(rendererDelta, motion.AcceleratedX, -motion.AcceleratedY);
        if (++_comparedFrames < ComparisonFrames)
            return;

        _sourceChosen = true;
        _useAccelerated = Accelerated.DistanceFromOne < Unaccelerated.DistanceFromOne / 2;
    }

    // Least-squares fit of renderer delta = k * raw delta, per axis.
    private sealed class AxisFit
    {
        private double _crossX, _rawSquaredX, _crossY, _rawSquaredY;

        public double X => _rawSquaredX > 0 ? _crossX / _rawSquaredX : double.NaN;
        public double Y => _rawSquaredY > 0 ? _crossY / _rawSquaredY : double.NaN;

        // Measured in log space, so 0.5x and 2x count as equally wrong.
        public double DistanceFromOne => Math.Abs(Math.Log(Math.Abs(X))) + Math.Abs(Math.Log(Math.Abs(Y)));

        public void Add(RenderVector2 renderer, double rawX, double rawY)
        {
            _crossX += renderer.x * rawX;
            _rawSquaredX += rawX * rawX;
            _crossY += renderer.y * rawY;
            _rawSquaredY += rawY * rawY;
        }
    }
}
