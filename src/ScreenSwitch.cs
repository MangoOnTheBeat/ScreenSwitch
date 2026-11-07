using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// Thin wrapper over the Win32 display APIs (the same ones Win+P uses).
static class Display
{
    const uint QDC_ONLY_ACTIVE_PATHS = 0x2, QDC_DATABASE_CURRENT = 0x4;
    const uint SDC_TOPOLOGY_INTERNAL = 0x1, SDC_TOPOLOGY_CLONE = 0x2, SDC_TOPOLOGY_EXTEND = 0x4, SDC_TOPOLOGY_EXTERNAL = 0x8;
    const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20, SDC_APPLY = 0x80, SDC_SAVE_TO_DATABASE = 0x200, SDC_ALLOW_CHANGES = 0x400;
    const int ERROR_INSUFFICIENT_BUFFER = 122;

    // The snapshot is only ever handed back to Windows, so the structs are kept as opaque bytes.
    const int PathInfoSize = 72, ModeInfoSize = 64;

    [DllImport("user32.dll")]
    static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [DllImport("user32.dll")]
    static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] byte[] paths, ref uint numModes, [Out] byte[] modes, IntPtr topology);
    [DllImport("user32.dll", EntryPoint = "QueryDisplayConfig")]
    static extern int QueryDisplayTopology(uint flags, ref uint numPaths, [Out] byte[] paths, ref uint numModes, [Out] byte[] modes, out uint topology);
    [DllImport("user32.dll")]
    static extern int SetDisplayConfig(uint numPaths, byte[] paths, uint numModes, byte[] modes, uint flags);
    [DllImport("user32.dll")]
    static extern int DisplayConfigGetDeviceInfo(byte[] packet);

    public sealed class DisplayInfo
    {
        public uint TargetId;
        public string Device, Name;
        public bool Active;
    }

    // Every plugged-in display in Screen 1, Screen 2 order, with the name the monitor reports for itself.
    public static List<DisplayInfo> Connected()
    {
        const uint QDC_ALL_PATHS = 0x1;
        const int PathTargetAdapter = 20, PathTargetId = 28, PathTargetAvailable = 60, PathFlags = 68, PathActive = 1;

        List<DisplayInfo> displays = new List<DisplayInfo>();
        uint numPaths, numModes;
        byte[] paths;
        int error;
        do
        {
            error = GetDisplayConfigBufferSizes(QDC_ALL_PATHS, out numPaths, out numModes);
            if (error != 0) return displays;
            paths = new byte[numPaths * PathInfoSize];
            error = QueryDisplayConfig(QDC_ALL_PATHS, ref numPaths, paths, ref numModes, new byte[numModes * ModeInfoSize], IntPtr.Zero);
        } while (error == ERROR_INSUFFICIENT_BUFFER);
        if (error != 0) return displays;

        for (int i = 0; i < numPaths; i++)
        {
            int at = i * PathInfoSize;
            if (BitConverter.ToInt32(paths, at + PathTargetAvailable) == 0) continue;
            string device, name;
            if (!ReadTargetName(paths, at + PathTargetAdapter, out device, out name)) continue;
            bool active = (BitConverter.ToUInt32(paths, at + PathFlags) & PathActive) != 0;

            DisplayInfo known = displays.Find(delegate(DisplayInfo d) { return d.Device == device; });
            if (known != null) known.Active |= active;
            else displays.Add(new DisplayInfo { TargetId = BitConverter.ToUInt32(paths, at + PathTargetId), Device = device, Name = name, Active = active });
        }
        displays.Sort(delegate(DisplayInfo a, DisplayInfo b) { return a.TargetId.CompareTo(b.TargetId); });
        return displays;
    }

    // Reads DISPLAYCONFIG_TARGET_DEVICE_NAME for the adapter id and target id stored at the given offset.
    static bool ReadTargetName(byte[] paths, int target, out string device, out string name)
    {
        const int GetTargetName = 2, PacketSize = 420, NameAt = 36, NameBytes = 128, DevicePathAt = 164, DevicePathBytes = 256;
        byte[] packet = new byte[PacketSize];
        BitConverter.GetBytes(GetTargetName).CopyTo(packet, 0);
        BitConverter.GetBytes(PacketSize).CopyTo(packet, 4);
        Array.Copy(paths, target, packet, 8, 12);
        device = name = null;
        if (DisplayConfigGetDeviceInfo(packet) != 0) return false;
        device = CString(packet, DevicePathAt, DevicePathBytes);
        name = CString(packet, NameAt, NameBytes);
        if (name.Length == 0) name = "Unnamed display";
        return device.Length > 0;
    }

    static string CString(byte[] bytes, int at, int length)
    {
        string text = Encoding.Unicode.GetString(bytes, at, length);
        int end = text.IndexOf('\0');
        return end >= 0 ? text.Substring(0, end) : text;
    }

    public sealed class Snapshot
    {
        public uint NumPaths, NumModes, Topology;
        public byte[] Paths, Modes;
    }

    public static int Capture(out Snapshot snapshot)
    {
        snapshot = new Snapshot();
        int error;
        do
        {
            error = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out snapshot.NumPaths, out snapshot.NumModes);
            if (error != 0) return error;
            snapshot.Paths = new byte[snapshot.NumPaths * PathInfoSize];
            snapshot.Modes = new byte[snapshot.NumModes * ModeInfoSize];
            error = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref snapshot.NumPaths, snapshot.Paths, ref snapshot.NumModes, snapshot.Modes, IntPtr.Zero);
        } while (error == ERROR_INSUFFICIENT_BUFFER);
        if (error != 0) return error;

        snapshot.Topology = CurrentTopology();
        return 0;
    }

    static uint CurrentTopology()
    {
        uint numPaths, numModes, topology;
        int error;
        do
        {
            error = GetDisplayConfigBufferSizes(QDC_DATABASE_CURRENT, out numPaths, out numModes);
            if (error != 0) return 0;
            error = QueryDisplayTopology(QDC_DATABASE_CURRENT, ref numPaths, new byte[numPaths * PathInfoSize],
                ref numModes, new byte[numModes * ModeInfoSize], out topology);
        } while (error == ERROR_INSUFFICIENT_BUFFER);
        return error == 0 ? topology : 0;
    }

    static uint TopologyOf(Mode mode)
    {
        return mode == Mode.Screen1 ? SDC_TOPOLOGY_INTERNAL
            : mode == Mode.Screen2 ? SDC_TOPOLOGY_EXTERNAL
            : mode == Mode.Extend ? SDC_TOPOLOGY_EXTEND
            : SDC_TOPOLOGY_CLONE;
    }

    public static bool IsCurrent(Mode mode)
    {
        return CurrentTopology() == TopologyOf(mode);
    }

    public static int Apply(Mode mode)
    {
        int error = SetDisplayConfig(0, null, 0, null, SDC_APPLY | TopologyOf(mode));
        if (error == 0) MakeFirstScreenMain();
        return error;
    }

    // Windows can pick the second screen as main. If this fails the switch still happened and the countdown still protects it.
    static void MakeFirstScreenMain()
    {
        Snapshot now;
        if (Capture(out now) == 0 && ShiftFirstScreenToOrigin(now))
            SetDisplayConfig(now.NumPaths, now.Paths, now.NumModes, now.Modes,
                SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE);
    }

    // The main screen is whichever one sits at (0,0), so slide the whole layout to put Screen 1 there.
    // Screen 1 is the lowest target id, which is what Screen1 mode resolves to. Returns false if nothing needs to move.
    public static bool ShiftFirstScreenToOrigin(Snapshot s)
    {
        const int PathSourceModeIdx = 12, PathTargetId = 28, ModeType = 0, ModeSourceX = 28, ModeSourceY = 32, ModeTypeSource = 1;

        int first = -1;
        uint lowest = uint.MaxValue;
        for (int i = 0; i < s.NumPaths; i++)
        {
            uint target = BitConverter.ToUInt32(s.Paths, i * PathInfoSize + PathTargetId);
            if (target < lowest) { lowest = target; first = i; }
        }
        if (first < 0) return false;

        int index = BitConverter.ToInt32(s.Paths, first * PathInfoSize + PathSourceModeIdx);
        if (index < 0 || index >= s.NumModes) return false;

        int dx = BitConverter.ToInt32(s.Modes, index * ModeInfoSize + ModeSourceX);
        int dy = BitConverter.ToInt32(s.Modes, index * ModeInfoSize + ModeSourceY);
        if (dx == 0 && dy == 0) return false;

        for (int i = 0; i < s.NumModes; i++)
        {
            int at = i * ModeInfoSize;
            if (BitConverter.ToInt32(s.Modes, at + ModeType) != ModeTypeSource) continue;
            BitConverter.GetBytes(BitConverter.ToInt32(s.Modes, at + ModeSourceX) - dx).CopyTo(s.Modes, at + ModeSourceX);
            BitConverter.GetBytes(BitConverter.ToInt32(s.Modes, at + ModeSourceY) - dy).CopyTo(s.Modes, at + ModeSourceY);
        }
        return true;
    }

    public static int Restore(Snapshot s)
    {
        int error = SetDisplayConfig(s.NumPaths, s.Paths, s.NumModes, s.Modes,
            SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE);
        if (error != 0 && s.Topology != 0)
            error = SetDisplayConfig(0, null, 0, null, SDC_APPLY | s.Topology);
        return error;
    }
}

// Remembers every display ever seen, so only a truly new one gets added.
// A known display that's off stays off, otherwise choosing Screen 1 would be undone at the next check.
static class NewDisplays
{
    const string Key = @"Software\ScreenSwitch", ValueName = "KnownDisplays";

    // The first run has nothing to compare with, so whatever is plugged in then counts as known.
    public static void Seed()
    {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(Key))
            if (key.GetValue(ValueName) == null)
                key.SetValue(ValueName, Display.Connected().ConvertAll(d => d.Device).ToArray(), RegistryValueKind.MultiString);
    }

    // Returns the name of a display to add, or null. Each display is only offered once: it's remembered
    // before the prompt, so walking away doesn't bring the prompt back at every check.
    public static string Check()
    {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(Key))
        {
            List<string> known = new List<string>((key.GetValue(ValueName) as string[]) ?? new string[0]);
            List<Display.DisplayInfo> connected = Display.Connected();
            string found = FindNew(connected, known);
            foreach (Display.DisplayInfo display in connected)
                if (!known.Contains(display.Device)) known.Add(display.Device);
            key.SetValue(ValueName, known.ToArray(), RegistryValueKind.MultiString);
            return found;
        }
    }

    public static string FindNew(List<Display.DisplayInfo> connected, List<string> known)
    {
        foreach (Display.DisplayInfo display in connected)
            if (!display.Active && !known.Contains(display.Device)) return display.Name;
        return null;
    }
}

// "Open when Windows starts", stored per user so it needs no admin rights.
static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", ValueName = "ScreenSwitch";

    public static bool IsEnabled()
    {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
            return key != null && key.GetValue(ValueName) != null;
    }

    public static void Set(bool enabled)
    {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (enabled) key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"");
            else key.DeleteValue(ValueName, false);
        }
    }
}

// Black or white behind everything. Follows Windows until the person picks one with the button.
static class Ui
{
    const string SettingsKey = @"HKEY_CURRENT_USER\Software\ScreenSwitch", ThemeValue = "Theme";

    const string ColorsValue = "Colors";

    // A colour theme's own colours for each mode. Everything else is mixed from these and the background.
    public sealed class ColorTheme
    {
        public string Name;
        public int LightAccent, LightHighlight, LightSwitch, DarkAccent, DarkHighlight, DarkSwitch;
    }

    public static readonly ColorTheme[] Themes =
    {
        new ColorTheme { Name = "Ember",    LightAccent = 0xD93C22, LightHighlight = 0xA9650F, LightSwitch = 0xD93C22, DarkAccent = 0xE0482D, DarkHighlight = 0xF0A63C, DarkSwitch = 0xF0A63C },
        new ColorTheme { Name = "Ocean",    LightAccent = 0x1D5FD0, LightHighlight = 0x0B6E99, LightSwitch = 0x1D5FD0, DarkAccent = 0x2F6FE4, DarkHighlight = 0x5CC8F5, DarkSwitch = 0x5CC8F5 },
        new ColorTheme { Name = "Forest",   LightAccent = 0x1F7A3E, LightHighlight = 0x4D7C0F, LightSwitch = 0x1F7A3E, DarkAccent = 0x23874A, DarkHighlight = 0xA3D65C, DarkSwitch = 0xA3D65C },
        new ColorTheme { Name = "Graphite", LightAccent = 0x27272A, LightHighlight = 0x52525B, LightSwitch = 0x27272A, DarkAccent = 0xE7E7EA, DarkHighlight = 0xB4B4BA, DarkSwitch = 0xE7E7EA },
        new ColorTheme { Name = "Arctic",   LightAccent = 0x0369A1, LightHighlight = 0x0B6475, LightSwitch = 0x0369A1, DarkAccent = 0x0B7FC0, DarkHighlight = 0xA5F3FC, DarkSwitch = 0xA5F3FC },
    };

    public static bool IsDark { get; private set; }
    public static bool FollowsWindows { get; private set; }
    public static ColorTheme Theme { get; private set; }
    public static Color Background, Card, CardHover, CardPressed, Text, SubText, Accent, AccentHover, AccentPressed, AccentDeep, Honey, Neutral, NeutralHover, NeutralPressed, SwitchOn;
    // Text on the accent, and the switch knob, are white or black: whichever can actually be seen.
    public static Color OnAccent, Knob;

    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_CAPTION_COLOR = 35, DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    static Ui()
    {
        string chosen = Registry.GetValue(SettingsKey, ThemeValue, null) as string;
        FollowsWindows = chosen != "dark" && chosen != "light";
        string colors = Registry.GetValue(SettingsKey, ColorsValue, null) as string;
        Apply(Array.Find(Themes, delegate(ColorTheme t) { return t.Name == colors; }) ?? Themes[0], FollowsWindows ? WindowsIsDark() : chosen == "dark");
    }

    static bool WindowsIsDark()
    {
        object light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
        return light is int && (int)light == 0;
    }

    // Controls keep the colors they were made with, so the window is rebuilt straight after either of these.
    public static void ChooseMode(bool? dark)
    {
        FollowsWindows = dark == null;
        if (dark == null)
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\ScreenSwitch", true))
            {
                if (key != null) key.DeleteValue(ThemeValue, false);
            }
        else Registry.SetValue(SettingsKey, ThemeValue, dark.Value ? "dark" : "light");
        Apply(Theme, dark ?? WindowsIsDark());
    }

    public static void ChooseTheme(ColorTheme theme)
    {
        Registry.SetValue(SettingsKey, ColorsValue, theme.Name);
        Apply(theme, IsDark);
    }

    public static void Apply(ColorTheme theme, bool dark)
    {
        Theme = theme;
        IsDark = dark;
        Background = Rgb(dark ? 0x000000 : 0xFFFFFF);
        Text = Rgb(dark ? 0xF5F3EF : 0x1B1714);
        SubText = Rgb(dark ? 0xA9A29A : 0x5F5750);
        Accent = Rgb(dark ? theme.DarkAccent : theme.LightAccent);
        Honey = Rgb(dark ? theme.DarkHighlight : theme.LightHighlight);
        SwitchOn = Rgb(dark ? theme.DarkSwitch : theme.LightSwitch);

        // Surfaces carry a little of the accent, so each theme feels like itself without hurting readability.
        Card = Mix(Background, Accent, dark ? 0.10f : 0.05f);
        CardHover = Mix(Background, Accent, dark ? 0.16f : 0.09f);
        CardPressed = Mix(Background, Accent, dark ? 0.07f : 0.12f);
        Neutral = Mix(Background, Accent, dark ? 0.20f : 0.11f);
        NeutralHover = Mix(Background, Accent, dark ? 0.27f : 0.16f);
        NeutralPressed = Mix(Background, Accent, dark ? 0.34f : 0.21f);
        // White text when it's readable, otherwise black. Hover and press then move away from the text colour,
        // so a button never gets harder to read while it's being used.
        OnAccent = Contrast(Color.White, Accent) >= 4.5 || Contrast(Color.White, Accent) >= Contrast(Color.Black, Accent) ? Color.White : Color.Black;
        Color away = OnAccent == Color.White ? Color.Black : Color.White;
        AccentHover = Mix(Accent, away, 0.10f);
        AccentPressed = Mix(Accent, away, 0.18f);
        AccentDeep = Mix(Accent, Color.Black, 0.40f);
        Knob = Contrast(Color.White, SwitchOn) < 2 ? Rgb(0x1B1714) : Color.White;
    }

    static Color Rgb(int rgb)
    {
        return Color.FromArgb(unchecked((int)0xFF000000) | rgb);
    }

    static double Luminance(Color c)
    {
        Func<int, double> channel = delegate(int v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); };
        return 0.2126 * channel(c.R) + 0.7152 * channel(c.G) + 0.0722 * channel(c.B);
    }

    // The standard readability measure: 4.5 or more is comfortable for normal text.
    public static double Contrast(Color a, Color b)
    {
        double x = Luminance(a), y = Luminance(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    public static Color Mix(Color from, Color to, float amount)
    {
        float t = Math.Max(0, Math.Min(1, amount));
        return Color.FromArgb(
            (int)(from.A + (to.A - from.A) * t),
            (int)(from.R + (to.R - from.R) * t),
            (int)(from.G + (to.G - from.G) * t),
            (int)(from.B + (to.B - from.B) * t));
    }

    // Blends the title bar into the window and rounds the corners. Older Windows versions ignore these.
    public static void StyleWindow(Form form)
    {
        int dark = IsDark ? 1 : 0, corners = DWMWCP_ROUND, caption = ColorTranslator.ToWin32(Background);
        DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        DwmSetWindowAttribute(form.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));
        DwmSetWindowAttribute(form.Handle, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
    }

    public static Font Font(float px, bool semibold, float scale)
    {
        return new Font(semibold ? "Segoe UI Semibold" : "Segoe UI", px * scale, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    // For big headings. Comes with Windows, so nothing has to be downloaded.
    public static Font Display(float px, float scale)
    {
        return new Font("Bahnschrift SemiBold", px * scale, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    public static float DpiScale(Control c)
    {
        using (Graphics g = c.CreateGraphics()) return g.DpiX / 96f;
    }

    public static void Prepare(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
    }
}

// Button that tracks hover and press so subclasses can paint their own look.
abstract class SoftButton : Button
{
    // Run from 0 to 1, so hover and press fade in and out rather than switching.
    protected readonly Smooth Hover, Press;

    // What's behind the rounded corners.
    public Color Backdrop = Ui.Background;

    protected SoftButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Hover = new Smooth(this, 0.06f);
        Press = new Smooth(this, 0.03f);
    }

    protected Color Blend(Color rest, Color hover, Color pressed)
    {
        return Ui.Mix(Ui.Mix(rest, hover, Hover.Value), pressed, Press.Value);
    }

    protected override void OnMouseEnter(EventArgs e) { Hover.Target = 1; base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { Hover.Target = 0; Press.Target = 0; base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { Press.Target = 1; base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { Press.Target = 0; base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    // The ring only appears when moving around with the keyboard, not on every open.
    protected bool ShowRing { get { return Focused && ShowFocusCues; } }
}

// A soft rounded card showing the logo for its mode.
sealed class ModeTile : SoftButton
{
    readonly Mode mode;
    Bitmap icon;
    string subtitle = "";
    readonly float scale;
    readonly Font titleFont, subFont;

    public ModeTile(Mode mode, string title, float scale)
    {
        this.mode = mode;
        this.scale = scale;
        Text = title;
        titleFont = Ui.Display(18, scale);
        subFont = Ui.Font(13, false, scale);
    }

    public string Subtitle
    {
        set
        {
            subtitle = value;
            AccessibleName = Text + ": " + value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Ui.Background);
        Ui.Prepare(g);

        // Lifts towards the pointer and sinks back when pressed.
        float lift = (Hover.Value * 3 - Press.Value * 3) * scale;
        float margin = 6 * scale, radius = 18 * scale;
        float x = margin, y = margin - lift, w = Width - margin * 2, h = Height - margin * 2;

        if (!Motion.Light)
        {
            for (int i = 3; i >= 1; i--)
            {
                float spread = i * 2 * scale;
                using (GraphicsPath shadow = Logo.Rounded(x - spread / 2, y + spread, w + spread, h, radius))
                using (SolidBrush brush = new SolidBrush(Color.FromArgb((int)(18 + 26 * Hover.Value), 0, 0, 0)))
                    g.FillPath(brush, shadow);
            }
        }

        Color top = Blend(Ui.CardHover, Ui.Mix(Ui.CardHover, Ui.Honey, 0.10f), Ui.Card);
        Color bottom = Blend(Ui.Card, Ui.CardHover, Ui.CardPressed);
        using (GraphicsPath card = Logo.Rounded(x, y, w, h, radius))
        using (LinearGradientBrush brush = new LinearGradientBrush(new RectangleF(x, y - 1, w, h + 2), top, bottom, 90f))
        using (Pen hairline = new Pen(Ui.Mix(Color.FromArgb(30, Ui.Text), Color.FromArgb(150, Ui.Honey), Hover.Value), scale))
        {
            g.FillPath(brush, card);
            g.DrawPath(hairline, card);
            if (ShowRing)
                using (Pen ring = new Pen(Ui.Honey, 2.5f * scale)) g.DrawPath(ring, card);
        }

        // A soft glow that wakes up behind the icon.
        if (!Motion.Light)
        {
            float glow = 124 * scale, glowX = (Width - glow) / 2, glowY = y + 46 * scale - glow / 2;
            using (GraphicsPath circle = new GraphicsPath())
            {
                circle.AddEllipse(glowX, glowY, glow, glow);
                using (PathGradientBrush brush = new PathGradientBrush(circle))
                {
                    brush.CenterColor = Color.FromArgb((int)(24 + 74 * Hover.Value), Ui.Honey);
                    brush.SurroundColors = new[] { Color.FromArgb(0, Ui.Honey) };
                    g.FillPath(brush, circle);
                }
            }
        }

        // Drawn once and reused, since redrawing the icon on every frame of a hover is the slow part.
        if (icon == null)
        {
            int side = (int)Math.Ceiling(66 * scale);
            icon = new Bitmap(side, side);
            using (Graphics canvas = Graphics.FromImage(icon)) Logo.Draw(canvas, side, mode);
        }
        float size = 60 * scale * (1 + 0.07f * Hover.Value - 0.05f * Press.Value);
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.DrawImage(icon, (Width - size) / 2, y + 44 * scale - size / 2 + 2 * scale, size, size);

        using (StringFormat center = new StringFormat { Alignment = StringAlignment.Center })
        using (SolidBrush title = new SolidBrush(Ui.Text))
        using (SolidBrush sub = new SolidBrush(Ui.SubText))
        {
            g.DrawString(Text, titleFont, title, new RectangleF(0, y + 80 * scale, Width, 26 * scale), center);
            g.DrawString(subtitle, subFont, sub, new RectangleF(0, y + 106 * scale, Width, 22 * scale), center);
        }
    }
}

// A fully rounded pill button: solid with a chunky edge for the main action, outlined otherwise.
sealed class PillButton : SoftButton
{
    bool primary;
    readonly float scale;

    public PillButton(string text, bool primary, float scale)
    {
        this.primary = primary;
        this.scale = scale;
        Text = text;
        Font = Ui.Font(15, true, scale);
    }

    public bool Primary
    {
        set { primary = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Backdrop);
        Ui.Prepare(g);

        bool solid = primary && Enabled;
        Color fill = solid
            ? Blend(Ui.Accent, Ui.AccentHover, Ui.AccentPressed)
            : Blend(Ui.Mix(Backdrop, Ui.Text, 0.05f), Ui.Mix(Backdrop, Ui.Text, 0.12f), Ui.Mix(Backdrop, Ui.Text, 0.18f));

        // The solid button sits on a darker edge and pushes down into it when pressed.
        float inset = 2 * scale, edge = solid ? 3 * scale : 0, sink = edge * Press.Value;
        float w = Width - inset * 2 - 1, h = Height - inset * 2 - 1 - edge, y = inset + sink;

        if (solid)
            using (GraphicsPath under = Logo.Rounded(inset, inset + edge, w, h, h / 2))
            using (SolidBrush brush = new SolidBrush(Ui.AccentDeep))
                g.FillPath(brush, under);

        using (GraphicsPath pill = Logo.Rounded(inset, y, w, h, h / 2))
        using (SolidBrush brush = new SolidBrush(fill))
        {
            g.FillPath(brush, pill);
            if (!solid)
                using (Pen line = new Pen(Color.FromArgb((int)(60 + 90 * Hover.Value), Ui.Text), scale)) g.DrawPath(line, pill);
            if (ShowRing)
                using (Pen ring = new Pen(Ui.Honey, 2 * scale)) g.DrawPath(ring, pill);
        }

        using (StringFormat center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        using (SolidBrush text = new SolidBrush(!Enabled ? Ui.SubText : solid ? Ui.OnAccent : Ui.Text))
            g.DrawString(Text, Font, text, new RectangleF(0, y, Width, h), center);
    }
}

// An on/off slider. It stays a CheckBox underneath, so keyboard and screen readers still work.
sealed class ToggleSwitch : CheckBox
{
    readonly float scale;
    readonly Smooth position;
    public Color Backdrop = Ui.Background;

    public ToggleSwitch(float scale)
    {
        this.scale = scale;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        position = new Smooth(this, 0.05f);
    }

    protected override void OnCheckedChanged(EventArgs e)
    {
        // Only glides once it's on screen, so opening a page doesn't animate every switch.
        if (IsHandleCreated && Visible) position.Target = Checked ? 1 : 0;
        else position.Snap(Checked ? 1 : 0);
        base.OnCheckedChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Backdrop);
        Ui.Prepare(g);

        float inset = 2 * scale, w = Width - inset * 2 - 1, h = Height - inset * 2 - 1;
        using (GraphicsPath track = Logo.Rounded(inset, inset, w, h, h / 2))
        using (SolidBrush brush = new SolidBrush(Ui.Mix(Ui.Neutral, Ui.SwitchOn, position.Value)))
        {
            g.FillPath(brush, track);
            if (Focused && ShowFocusCues)
                using (Pen ring = new Pen(Ui.Text, 2 * scale)) g.DrawPath(ring, track);
        }

        float gap = 2 * scale, knob = h - gap * 2;
        float x = inset + gap + (w - gap * 2 - knob) * position.Value;
        using (SolidBrush brush = new SolidBrush(Ui.Mix(Color.White, Ui.Knob, position.Value)))
            g.FillEllipse(brush, x, inset + gap, knob, knob);
    }
}

// The 15-second "keep or go back" prompt. Anything other than pressing Keep reverts the change.
sealed class ConfirmForm : Form
{
    public const string DefaultHeading = "Keep these display settings?";
    const int Seconds = 15;
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 50 };
    readonly Label countdown;
    readonly float scale;
    readonly Rectangle bar;
    double remaining = Seconds;

    public ConfirmForm(string heading = DefaultHeading)
    {
        scale = Ui.DpiScale(this);
        AutoScaleMode = AutoScaleMode.None;
        Text = "Screen Switch";
        Icon = MainForm.AppIcon();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = Ui.Background;
        ForeColor = Ui.Text;
        ClientSize = new Size(S(380), S(280));

        AddLabel(heading, Ui.Font(20, true, scale), Ui.Text, 88, 32);
        countdown = AddLabel("", Ui.Font(14, false, scale), Ui.Text, 122, 24);
        AddLabel("Screen gone black? Just wait, it goes back on its own.", Ui.Font(13, false, scale), Ui.SubText, 146, 22);

        bar = new Rectangle(S(24), S(182), S(332), S(6));

        PillButton keep = new PillButton("Keep", true, scale);
        keep.Bounds = new Rectangle(S(194), S(208), S(166), S(48));
        keep.DialogResult = DialogResult.OK;
        PillButton cancel = new PillButton("Cancel", false, scale);
        cancel.Bounds = new Rectangle(S(20), S(208), S(166), S(48));
        cancel.DialogResult = DialogResult.Cancel;
        Controls.Add(cancel);
        Controls.Add(keep);

        // Keep needs a deliberate click: a stray Enter or Space on a black screen must never confirm it.
        AcceptButton = cancel;
        CancelButton = cancel;
        ActiveControl = cancel;

        UpdateCountdown();
        timer.Tick += delegate { Tick(); };
    }

    int S(int v) { return (int)Math.Round(v * scale); }

    Label AddLabel(string text, Font font, Color color, int y, int height)
    {
        Label label = new Label
        {
            Text = text,
            Font = font,
            ForeColor = color,
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(S(12), S(y), S(356), S(height))
        };
        Controls.Add(label);
        return label;
    }

    void Tick()
    {
        remaining = Seconds - clock.Elapsed.TotalSeconds;
        if (remaining <= 0)
        {
            DialogResult = DialogResult.Cancel;
            return;
        }
        UpdateCountdown();
        Invalidate(bar);
    }

    void UpdateCountdown()
    {
        int whole = (int)Math.Ceiling(remaining);
        string text = "Going back in " + whole + (whole == 1 ? " second" : " seconds");
        if (countdown.Text != text) countdown.Text = text;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        Ui.Prepare(g);

        float logo = S(60);
        GraphicsState state = g.Save();
        g.TranslateTransform((ClientSize.Width - logo) / 2, S(20));
        Logo.Draw(g, logo);
        g.Restore(state);

        using (GraphicsPath track = Logo.Rounded(bar.X, bar.Y, bar.Width, bar.Height, bar.Height / 2f))
        using (SolidBrush brush = new SolidBrush(Ui.Neutral))
            g.FillPath(brush, track);

        float filled = (float)(bar.Width * Math.Max(remaining, 0) / Seconds);
        if (filled >= bar.Height)
            using (GraphicsPath fill = Logo.Rounded(bar.X, bar.Y, filled, bar.Height, bar.Height / 2f))
            using (SolidBrush brush = new SolidBrush(Ui.Accent))
                g.FillPath(brush, fill);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Ui.StyleWindow(this);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        CenterOnPrimary();
        timer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        base.OnFormClosed(e);
    }

    void OnDisplayChanged(object sender, EventArgs e)
    {
        CenterOnPrimary();
    }

    // The layout just changed, so keep the prompt on whichever screen is the main one now.
    void CenterOnPrimary()
    {
        Rectangle area = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(area.X + (area.Width - Width) / 2, area.Y + (area.Height - Height) / 2);
        Activate();
    }
}

sealed class MainForm : Form
{
    // Holds everything on screen. When maximized it's enlarged and sits centered in the window.
    sealed class Stage : Panel
    {
        public Stage()
        {
            DoubleBuffered = true;
            BackColor = Ui.Background;
        }
    }

    // The tabs sit on their own row under the title, so pages start lower down.
    const int WindowWidth = 564, TallestWindow = 676, PageTop = 124;
    readonly float scale;
    readonly bool fullScreen;
    readonly Stage stage = new Stage();
    Tween statusTween;
    int currentPage;
    PillButton themeButton;
    // Plugging something in is caught straight away by Windows' device messages. This slow check is only
    // a safety net for anything those miss, and it's cheap enough to run often.
    const int SafetyCheckMs = 60 * 1000;
    const int WM_DEVICECHANGE = 0x219, WM_SYSCOMMAND = 0x112, SC_MAXIMIZE = 0xF030, SC_RESTORE = 0xF120;
    readonly Label status = new Label();
    readonly Font titleFont, subtitleFont;
    readonly NotifyIcon tray = new NotifyIcon();
    readonly System.Windows.Forms.Timer watch = new System.Windows.Forms.Timer { Interval = SafetyCheckMs };
    // One plug-in fires a burst of messages, so wait for it to settle before looking.
    readonly System.Windows.Forms.Timer refreshSoon = new System.Windows.Forms.Timer { Interval = 1500 };
    readonly ModeTile screen1, screen2, extend, duplicate;
    readonly Panel displaysPage = new Panel();
    readonly SettingsPage settingsPage;
    readonly AudioPage audioPage;
    readonly ResetPage resetPage;
    readonly ResetStore resetStore = new ResetStore();
    readonly ConfigStore configStore = new ConfigStore();
    readonly ShortcutConfig config;
    readonly HotkeyManager hotkeys;
    readonly ProfilesPage profilesPage;
    readonly BluetoothPage bluetoothPage;
    readonly SegmentButton[] tabs = new SegmentButton[6];
    Label startupLabel;
    ToggleSwitch startupToggle;
    IDisposable audioWatch;
    bool quitting;
    static bool toldAboutTray;

    // True once this window has closed only so a rebuilt one can open.
    public bool SwitchingMode { get; private set; }
    public bool NextFullScreen { get; private set; }
    public int CurrentPage { get { return currentPage; } }
    public Rectangle ScreenInUse { get; private set; }

    public MainForm(bool fullScreen, Rectangle area, int startPage)
    {
        this.fullScreen = fullScreen;
        Motion.Light = fullScreen;
        // Maximized, everything is drawn bigger, to the largest size where the tallest page still fits.
        scale = fullScreen ? (float)Math.Floor(Math.Min(area.Width / (float)WindowWidth, (area.Height - SystemInformation.CaptionHeight - 8) / (float)TallestWindow) * 20) / 20 : Ui.DpiScale(this);
        titleFont = Ui.Display(25, scale);
        subtitleFont = Ui.Font(14, true, scale);
        AutoScaleMode = AutoScaleMode.None;
        Text = "Screen Switch";
        Icon = AppIcon();
        ForeColor = Ui.Text;
        DoubleBuffered = true;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        BackColor = Ui.Background;
        if (fullScreen)
        {
            StartPosition = FormStartPosition.Manual;
            Location = area.Location;
            WindowState = FormWindowState.Maximized;
        }
        else
        {
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(S(WindowWidth), S(478));
            if (Motion.Enabled) Opacity = 0;
        }

        stage.Bounds = new Rectangle(0, 0, S(WindowWidth), S(478));
        stage.Paint += PaintHeader;
        Controls.Add(stage);

        displaysPage.Bounds = new Rectangle(0, S(PageTop), S(564), S(308));
        stage.Controls.Add(displaysPage);
        screen1 = AddTile(Mode.Screen1, "Screen 1", 14, 0);
        screen2 = AddTile(Mode.Screen2, "Screen 2", 282, 0);
        extend = AddTile(Mode.Extend, "Extend", 14, 154);
        duplicate = AddTile(Mode.Duplicate, "Duplicate", 282, 154);

        settingsPage = new SettingsPage(scale, Report, this) { Bounds = displaysPage.Bounds, Visible = false };
        stage.Controls.Add(settingsPage);
        audioPage = new AudioPage(scale, Report, NewAudioDevice) { Bounds = displaysPage.Bounds, Visible = false };
        stage.Controls.Add(audioPage);
        bluetoothPage = new BluetoothPage(scale, Report) { Bounds = displaysPage.Bounds, Visible = false };
        stage.Controls.Add(bluetoothPage);
        resetPage = new ResetPage(scale, resetStore, Report, RefreshDevices, this) { Bounds = displaysPage.Bounds, Visible = false };
        stage.Controls.Add(resetPage);
        config = configStore.Load();
        hotkeys = new HotkeyManager(this);
        profilesPage = new ProfilesPage(scale, config, configStore, hotkeys, Report, RegisterShortcuts, RunSetup, UseProfile) { Bounds = displaysPage.Bounds, Visible = false };
        stage.Controls.Add(profilesPage);
        AddTabs();

        status.Font = Ui.Font(13, false, scale);
        status.ForeColor = Ui.SubText;
        status.TextAlign = ContentAlignment.MiddleCenter;
        status.Bounds = new Rectangle(S(20), S(440), S(524), S(28));
        status.UseMnemonic = false;
        status.Text = "Every display change asks you to Keep or Cancel.";
        // Messages fade in rather than just appearing.
        status.TextChanged += delegate
        {
            if (statusTween != null) statusTween.Stop();
            statusTween = Motion.Run(status, 280, delegate(float t) { status.ForeColor = Ui.Mix(Ui.Background, Ui.SubText, t); });
        };
        stage.Controls.Add(status);

        AddStartupSwitch();
        AddThemeButton();
        SetUpTray();
        RefreshDevices();
        ShowPage(startPage);

        // Waits out a Keep or Cancel countdown, since the screens are mid-change then.
        watch.Tick += delegate { if (!Program.Busy) { RefreshDevices(); CheckForNewDisplays(); } };
        refreshSoon.Tick += delegate { refreshSoon.Stop(); if (!Program.Busy) { RefreshDevices(); CheckForNewDisplays(); } };
    }

    // One menu for light or dark and the colour theme. Picking anything rebuilds the window in the new colours.
    void AddThemeButton()
    {
        themeButton = new PillButton("Appearance", false, scale) { Bounds = new Rectangle(S(356), S(398), S(120), S(36)) };
        themeButton.Font = Ui.Font(13, true, scale);
        themeButton.AccessibleName = "Appearance: " + Ui.Theme.Name + ", " + (Ui.IsDark ? "dark" : "light");
        ContextMenuStrip menu = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new MenuColors()) { RoundedEdges = false },
            Font = Ui.Font(14, false, scale),
            BackColor = Ui.Card,
            ForeColor = Ui.Text
        };
        AddMenuItem(menu, "Match Windows", Ui.FollowsWindows, delegate { Ui.ChooseMode(null); });
        AddMenuItem(menu, "Light mode", !Ui.FollowsWindows && !Ui.IsDark, delegate { Ui.ChooseMode(false); });
        AddMenuItem(menu, "Dark mode", !Ui.FollowsWindows && Ui.IsDark, delegate { Ui.ChooseMode(true); });
        menu.Items.Add(new ToolStripSeparator());
        foreach (Ui.ColorTheme theme in Ui.Themes)
        {
            Ui.ColorTheme picked = theme;
            AddMenuItem(menu, theme.Name, theme == Ui.Theme, delegate { Ui.ChooseTheme(picked); });
        }
        themeButton.Click += delegate
        {
            menu.MinimumSize = new Size(themeButton.Width, 0);
            menu.Show(themeButton, 0, themeButton.Height + S(2));
        };
        stage.Controls.Add(themeButton);
    }

    void AddMenuItem(ContextMenuStrip menu, string text, bool chosen, Action choose)
    {
        ToolStripMenuItem item = new ToolStripMenuItem(text) { Checked = chosen, ForeColor = Ui.Text };
        item.Click += delegate
        {
            if (Program.Busy) { Report("Finish the current change first."); return; }
            choose();
            Rebuild(fullScreen);
        };
        menu.Items.Add(item);
    }

    // Everything is laid out for one size and color, so the reliable way to change either is a fresh window.
    void Rebuild(bool maximized)
    {
        if (Program.Busy) { Report("Finish the current change first."); return; }
        ScreenInUse = Screen.FromControl(this).WorkingArea;
        NextFullScreen = maximized;
        SwitchingMode = true;
        quitting = true;
        Close();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F11)
        {
            Rebuild(!fullScreen);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    void CenterStage()
    {
        stage.Location = new Point((ClientSize.Width - stage.Width) / 2, (ClientSize.Height - stage.Height) / 2);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (fullScreen) CenterStage();
    }

    void AddTabs()
    {
        SegmentTrack track = new SegmentTrack(scale) { Bounds = new Rectangle(S(20), S(80), S(524), S(36)) };
        string[] names = { "Displays", "Settings", "Audio", "Bluetooth", "Reset", "Profiles" };
        for (int i = 0; i < tabs.Length; i++)
        {
            int page = i;
            tabs[i] = new SegmentButton(names[i], scale) { Bounds = new Rectangle(S(3 + i * 86), S(3), S(86), S(30)) };
            tabs[i].Click += delegate { ShowPage(page); };
            track.Controls.Add(tabs[i]);
        }
        stage.Controls.Add(track);
    }

    public void ShowPage(int page)
    {
        currentPage = page;
        Panel[] pages = { displaysPage, settingsPage, audioPage, bluetoothPage, resetPage, profilesPage };
        int height = pages[page] == settingsPage || pages[page] == profilesPage || pages[page] == bluetoothPage ? 442 : 308;
        // Sized before the new page shows, so it never appears cut off or half drawn.
        stage.SuspendLayout();
        for (int i = 0; i < pages.Length; i++)
        {
            pages[i].Height = S(height);
            if (i != page) pages[i].Visible = false;
            tabs[i].Selected = i == page;
        }
        LayOut(S(PageTop + height + 86));
        pages[page].Visible = true;
        stage.ResumeLayout();
        if (IsHandleCreated) stage.Update();
    }

    // Everything under the page hangs off the bottom edge, so it follows along.
    void LayOut(int height)
    {
        stage.Height = height;
        startupLabel.Top = height - S(80);
        startupToggle.Top = height - S(78);
        themeButton.Top = height - S(80);
        status.Top = height - S(38);
        if (fullScreen) CenterStage();
        else ClientSize = new Size(S(WindowWidth), height);
    }

    void PaintHeader(object sender, PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Ui.Prepare(g);

        GraphicsState state = g.Save();
        g.TranslateTransform(S(20), S(18));
        Logo.Draw(g, S(52));
        g.Restore(state);

        using (SolidBrush title = new SolidBrush(Ui.Text))
        using (SolidBrush sub = new SolidBrush(Ui.Honey))
        using (SolidBrush dash = new SolidBrush(Ui.Accent))
        {
            g.DrawString("Screen Switch", titleFont, title, S(82), S(15));
            g.FillRectangle(dash, S(85), S(59), S(16), Math.Max(1, S(2)));
            g.DrawString("Screens and sound", subtitleFont, sub, S(106), S(49));
        }
    }

    void Report(string message)
    {
        status.Text = message;
    }

    // Tells Windows the current shortcuts. Called at startup and after every change to them.
    void RegisterShortcuts()
    {
        List<KeyValuePair<Keys, Action>> shortcuts = new List<KeyValuePair<Keys, Action>>();
        for (int i = 0; i < config.ScreenKeys.Count; i++)
        {
            if (config.ScreenKeys[i] == 0) continue;
            int slot = i;
            shortcuts.Add(new KeyValuePair<Keys, Action>((Keys)config.ScreenKeys[i], delegate { UseScreen(slot); }));
        }
        foreach (SwitchProfile profile in config.Profiles)
        {
            if (profile.Shortcut == 0) continue;
            SwitchProfile picked = profile;
            shortcuts.Add(new KeyValuePair<Keys, Action>((Keys)profile.Shortcut, delegate { UseProfile(picked); }));
        }
        hotkeys.Set(shortcuts);
        if (hotkeys.Failed.Count > 0) Report("Taken by another app: " + string.Join(", ", hotkeys.Failed) + ". Pick other keys in the Profiles tab.");
    }

    // The window may be hidden in the tray, and a hidden window can't own the Keep or Cancel prompt.
    IWin32Window PromptOwner { get { return Visible ? this : null; } }

    void UseScreen(int slot)
    {
        status.Text = Program.Switch(Hotkey.ScreenModes[slot], Hotkey.ScreenNames[slot], PromptOwner);
    }

    void UseProfile(SwitchProfile profile)
    {
        status.Text = ProfileRunner.Use(profile, PromptOwner);
    }

    void RunSetup()
    {
        hotkeys.Pause();
        using (SetupForm setup = new SetupForm(scale, config, hotkeys))
        {
            DialogResult answer = setup.ShowDialog(this);
            setup.Finish(Report);
            config.SetupDone = true;
            startupToggle.Checked = Startup.IsEnabled();
            status.Text = answer == DialogResult.OK
                ? "Setup finished. You can change all of it any time in the Profiles tab."
                : "Setup skipped. You can run it again any time from the Profiles tab.";
        }
        hotkeys.Resume();
        try { configStore.Save(config); }
        catch (Exception e)
        {
            if (!(e is System.IO.IOException || e is UnauthorizedAccessException)) throw;
            Report("Couldn't save your setup: " + e.Message);
        }
        RegisterShortcuts();
        profilesPage.Reload();
    }

    void NewAudioDevice(string name)
    {
        Report("New audio device: " + name);
        if (tray.Visible) tray.ShowBalloonTip(5000, "New audio device", name, ToolTipIcon.None);
    }

    void RefreshDevices()
    {
        List<Display.DisplayInfo> displays = Display.Connected();
        string first = displays.Count > 0 ? displays[0].Name : "Not connected";
        string second = displays.Count > 1 ? displays[1].Name : "Not connected";
        string both = displays.Count > 1 ? first + " + " + second : "Needs a second screen";
        screen1.Subtitle = first;
        screen2.Subtitle = second;
        extend.Subtitle = both;
        duplicate.Subtitle = both;
        settingsPage.RefreshMonitors();

        // An audio device can disappear halfway through this. The next refresh picks it up.
        try { audioPage.RefreshDevices(); }
        catch (COMException) { }
    }

    void BuildTrayMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();
        ToolStripMenuItem open = TrayItem("Open Screen Switch", delegate { ShowFromTray(); });
        open.Font = new Font(menu.Font, FontStyle.Bold);
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());

        ToolStripMenuItem screens = TraySubmenu(menu, "Screens");
        for (int i = 0; i < Hotkey.ScreenNames.Length; i++)
        {
            int slot = i;
            ToolStripMenuItem item = TrayItem(Hotkey.ScreenNames[i], delegate { UseScreen(slot); });
            if (config.ScreenKeys[i] != 0) item.ShortcutKeyDisplayString = Hotkey.Describe((Keys)config.ScreenKeys[i]);
            screens.DropDownItems.Add(item);
        }

        ToolStripMenuItem profiles = TraySubmenu(menu, "Profiles");
        foreach (SwitchProfile profile in config.Profiles)
        {
            SwitchProfile picked = profile;
            ToolStripMenuItem item = TrayItem(profile.Name, delegate { UseProfile(picked); });
            if (profile.Shortcut != 0) item.ShortcutKeyDisplayString = Hotkey.Describe((Keys)profile.Shortcut);
            profiles.DropDownItems.Add(item);
        }
        if (config.Profiles.Count == 0) profiles.DropDownItems.Add(new ToolStripMenuItem("No profiles yet") { Enabled = false });

        ToolStripMenuItem output = TraySubmenu(menu, "Sound output");
        try
        {
            string current = Audio.DefaultId(AudioFlow.Output);
            foreach (AudioDevice device in Audio.List(AudioFlow.Output))
            {
                AudioDevice picked = device;
                ToolStripMenuItem item = TrayItem(device.Name, delegate { UseOutput(picked); });
                item.Checked = device.Id == current;
                output.DropDownItems.Add(item);
            }
        }
        catch (COMException) { } // Shown as empty below and read again next time the menu opens.
        if (output.DropDownItems.Count == 0) output.DropDownItems.Add(new ToolStripMenuItem("No sound devices found") { Enabled = false });

        ToolStripMenuItem bluetooth = TraySubmenu(menu, "Bluetooth audio");
        try
        {
            if (!Bluetooth.HasRadio())
                bluetooth.DropDownItems.Add(new ToolStripMenuItem("Bluetooth is off") { Enabled = false });
            else
                foreach (BluetoothDevice device in Bluetooth.Paired(false))
                {
                    if (!device.CanConnect) continue;
                    BluetoothDevice picked = device;
                    bluetooth.DropDownItems.Add(TrayItem((device.Connected ? "Disconnect " : "Connect ") + device.Name, delegate { ToggleBluetooth(picked); }));
                }
        }
        catch (Exception) { } // Bluetooth can be busy or switched off midway. The empty note below covers it.
        if (bluetooth.DropDownItems.Count == 0) bluetooth.DropDownItems.Add(new ToolStripMenuItem("No paired headphones or speakers") { Enabled = false });

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(TrayItem("Quit", delegate { Quit(); }));
    }

    ToolStripMenuItem TrayItem(string text, Action click)
    {
        ToolStripMenuItem item = new ToolStripMenuItem(text.Replace("&", "&&")) { ForeColor = Ui.Text };
        item.Click += delegate { click(); };
        return item;
    }

    ToolStripMenuItem TraySubmenu(ContextMenuStrip menu, string text)
    {
        ToolStripMenuItem item = new ToolStripMenuItem(text) { ForeColor = Ui.Text };
        item.DropDown.BackColor = Ui.Card;
        ((ToolStripDropDownMenu)item.DropDown).Renderer = menu.Renderer;
        menu.Items.Add(item);
        return item;
    }

    // Results show in the window when it's open, or as a notification from the tray when it isn't.
    void Tell(string title, string message)
    {
        Report(message);
        if (!Visible && tray.Visible) tray.ShowBalloonTip(3000, title, message, ToolTipIcon.None);
    }

    void UseOutput(AudioDevice device)
    {
        try
        {
            Audio.SetDefault(device.Id);
            Tell("Sound output", "Now using: " + device.Name);
        }
        catch (COMException e)
        {
            Tell("Sound output", "Couldn't switch to " + device.Name + ": " + e.Message);
        }
        try { audioPage.RefreshDevices(); }
        catch (COMException) { }
    }

    void ToggleBluetooth(BluetoothDevice device)
    {
        bool connect = !device.Connected;
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            string problem;
            try { problem = Bluetooth.SetConnected(device, connect); }
            catch (Exception e) { problem = e.Message; }
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new Action(delegate
            {
                Tell("Bluetooth", problem == null
                    ? (connect ? "Connecting to " : "Disconnecting from ") + device.Name + "..."
                    : "Couldn't " + (connect ? "connect " : "disconnect ") + device.Name + ". Make sure it's on and nearby.");
            }));
        });
    }

    // Only ever tells the person. Nothing here changes a display: that's their call.
    void CheckForNewDisplays()
    {
        if (Program.Busy) return;
        try
        {
            string name = NewDisplays.Check();
            if (name == null) return;
            Report("New display found: " + name + ". Press Extend or Duplicate to use it.");
            if (tray.Visible) tray.ShowBalloonTip(5000, "New display found", name + ". Press Extend or Duplicate to use it.", ToolTipIcon.None);
        }
        catch (Exception) { } // Tried again at the next check.
    }

    void SetUpTray()
    {
        // Normal size even when the window is maximized, since the menu sits by the taskbar.
        float menuScale = Ui.DpiScale(this);
        ContextMenuStrip menu = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new MenuColors()) { RoundedEdges = false },
            Font = Ui.Font(14, false, menuScale),
            BackColor = Ui.Card,
            ForeColor = Ui.Text
        };
        menu.Items.Add("Open Screen Switch", null, delegate { ShowFromTray(); });
        // Rebuilt every time it opens, so devices and profiles are always current.
        menu.Opening += delegate { BuildTrayMenu(menu); };
        tray.ContextMenuStrip = menu;
        tray.Icon = AppIcon();
        tray.Text = "Screen Switch";
        tray.MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowFromTray(); };
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        tray.Visible = true;
        try { NewDisplays.Seed(); }
        catch (Exception) { }
        try
        {
            resetStore.EnsureOriginal();
            resetPage.Reload();
        }
        catch (Exception ex)
        {
            Report("Couldn't save your original settings: " + ex.Message);
        }
        watch.Start();
        // Called on an audio thread, so hop back to this one first.
        try { audioWatch = Audio.Watch(delegate { BeginInvoke(new Action(refreshSoon.Start)); }); }
        catch (COMException) { } // The safety check still covers audio.
        RegisterShortcuts();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (Opacity < 1) Motion.Run(this, 260, delegate(float t) { Opacity = t; });
        if (!config.SetupDone) BeginInvoke(new Action(RunSetup));
    }

    void ShowFromTray()
    {
        Show();
        WindowState = fullScreen ? FormWindowState.Maximized : FormWindowState.Normal;
        Activate();
    }

    void Quit()
    {
        // Quitting mid-countdown would skip the automatic revert.
        if (Program.Busy) return;
        quitting = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && !quitting)
        {
            e.Cancel = true;
            Hide();
            if (!toldAboutTray)
            {
                toldAboutTray = true;
                tray.ShowBalloonTip(5000, "Screen Switch is still running", "It keeps checking for new displays. Right-click this icon to quit.", ToolTipIcon.None);
            }
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        watch.Stop();
        refreshSoon.Stop();
        if (audioWatch != null) audioWatch.Dispose();
        tray.Dispose();
        base.OnFormClosed(e);
    }

    // Opening the app again while it's in the tray brings this window back.
    protected override void WndProc(ref Message m)
    {
        // The maximize button rebuilds the window at the bigger size instead of just stretching it.
        if (m.Msg == WM_SYSCOMMAND)
        {
            int command = m.WParam.ToInt32() & 0xFFF0;
            bool maximize = command == SC_MAXIMIZE && !fullScreen && WindowState != FormWindowState.Minimized;
            bool restore = command == SC_RESTORE && fullScreen && WindowState == FormWindowState.Maximized;
            if (maximize || restore)
            {
                Rebuild(maximize);
                return;
            }
        }
        if (m.Msg == Program.ShowMessage) ShowFromTray();
        if (m.Msg == HotkeyManager.WM_HOTKEY) hotkeys.Fire(m.WParam.ToInt32());
        if (m.Msg == WM_DEVICECHANGE)
        {
            refreshSoon.Stop();
            refreshSoon.Start();
        }
        base.WndProc(ref m);
    }

    void AddStartupSwitch()
    {
        startupLabel = new Label
        {
            Text = "Open when Windows starts",
            Font = Ui.Font(15, false, scale),
            ForeColor = Ui.Text,
            TextAlign = ContentAlignment.MiddleLeft,
            Bounds = new Rectangle(S(22), S(398), S(300), S(36))
        };
        stage.Controls.Add(startupLabel);

        ToggleSwitch toggle = new ToggleSwitch(scale);
        startupToggle = toggle;
        toggle.AccessibleName = "Open when Windows starts";
        toggle.Bounds = new Rectangle(S(490), S(400), S(54), S(32));
        toggle.Checked = Startup.IsEnabled();
        // Click rather than CheckedChanged, so setting the initial state above doesn't write anything.
        toggle.Click += delegate
        {
            try
            {
                Startup.Set(toggle.Checked);
                status.Text = toggle.Checked ? "Screen Switch will open when Windows starts." : "Screen Switch won't open when Windows starts.";
            }
            catch (Exception ex)
            {
                toggle.Checked = Startup.IsEnabled();
                status.Text = "Couldn't change the startup setting: " + ex.Message;
            }
        };
        stage.Controls.Add(toggle);
    }

    public static Icon AppIcon()
    {
        return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
    }

    int S(int v) { return (int)Math.Round(v * scale); }

    ModeTile AddTile(Mode mode, string title, int x, int y)
    {
        ModeTile tile = new ModeTile(mode, title, scale);
        tile.Bounds = new Rectangle(S(x), S(y), S(268), S(154));
        tile.Click += delegate { status.Text = Program.Switch(mode, title, this); };
        displaysPage.Controls.Add(tile);
        return tile;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Ui.StyleWindow(this);
    }

}

static class Program
{
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);
    public static readonly int ShowMessage = RegisterWindowMessage("ScreenSwitch-show");

    // True from the moment a switch starts until it's kept or reverted.
    public static bool Busy { get; private set; }

    // Switches, asks for confirmation, and restores the snapshot unless Keep was pressed.
    public static string Switch(Mode mode, string name, IWin32Window owner)
    {
        Display.Snapshot before;
        int error = Display.Capture(out before);
        if (error != 0)
            return Fail("Couldn't read your current display setup, so nothing was changed.", error, owner);
        return Confirm(name, delegate { return Display.Apply(mode); }, delegate { return Display.Restore(before); }, owner);
    }

    // The safety net for every display change: apply it, ask, and undo it unless Keep was pressed.
    public static string Confirm(string name, Func<int> apply, Func<int> restore, IWin32Window owner)
    {
        if (Busy) return "Finish the current change first.";
        Busy = true;
        try
        {
            int error = apply();
            if (error != 0)
                return Fail("Windows refused to change to " + name + ", so nothing was changed.", error, owner);

            DialogResult answer;
            using (ConfirmForm confirm = new ConfirmForm())
                answer = confirm.ShowDialog(owner);
            if (answer == DialogResult.OK)
                return "Now using: " + name;

            error = restore();
            if (error != 0)
                return Fail("Couldn't go back to your old setting automatically. You can change it in Windows Settings.", error, owner);
            return "Went back to your old setting.";
        }
        finally
        {
            Busy = false;
        }
    }

    public static string Fail(string message, int error, IWin32Window owner)
    {
        string text = message + " (error " + error + ")";
        MessageBox.Show(owner, text, "Screen Switch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return text;
    }

    static bool TryParse(string arg, out Mode mode, out string name)
    {
        switch (arg.ToLowerInvariant())
        {
            case "1": mode = Mode.Screen1; name = "Screen 1"; return true;
            case "2": mode = Mode.Screen2; name = "Screen 2"; return true;
            case "extend": mode = Mode.Extend; name = "Extend"; return true;
            case "duplicate": mode = Mode.Duplicate; name = "Duplicate"; return true;
        }
        mode = Mode.Screen1;
        name = null;
        return false;
    }

    [STAThread]
    static void Main(string[] args)
    {
        // A second copy during a countdown would snapshot the unconfirmed layout as the "old" one.
        bool first;
        using (Mutex single = new Mutex(true, "ScreenSwitch-single-instance", out first))
        {
            if (!first)
            {
                PostMessage(HWND_BROADCAST, ShowMessage, IntPtr.Zero, IntPtr.Zero);
                return;
            }

            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Length == 0)
            {
                bool fullScreen = false;
                Rectangle area = Rectangle.Empty;
                int page = 0;
                while (true)
                {
                    using (MainForm form = new MainForm(fullScreen, area, page))
                    {
                        Application.Run(form);
                        if (!form.SwitchingMode) return;
                        fullScreen = form.NextFullScreen;
                        area = form.ScreenInUse;
                        page = form.CurrentPage;
                    }
                }
            }

            Mode mode;
            string name;
            if (args.Length == 1 && TryParse(args[0], out mode, out name))
                Switch(mode, name, null);
            else
                MessageBox.Show("Usage: ScreenSwitch.exe [1 | 2 | extend | duplicate]", "Screen Switch");
        }
    }
}
