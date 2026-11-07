using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

// Reads and changes per-display settings. Every value and option comes from Windows for the display
// in question, so nothing here assumes a particular monitor.
static class DisplaySettings
{
    const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
    const int GetSourceName = 1, GetTargetName = 2, GetPreferredMode = 3, GetAdvancedColor = 9, SetAdvancedColor = 10, GetSdrWhite = 11;
    // These three aren't in the public headers, but they're what the Settings app itself calls.
    const int GetDpiScale = -3, SetDpiScale = -4, SetSdrWhite = -18;
    const int ScopeCurrentUser = 1, ScopeSystemWide = 0, ProfileIcc = 0, SubtypeStandard = 7, SubtypeExtended = 8;
    const int ENUM_CURRENT_SETTINGS = -1, CDS_UPDATEREGISTRY = 1, CDS_TEST = 2;
    const int DM_DISPLAYORIENTATION = 0x80, DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000;

    // Windows' scale steps. A monitor reports its range relative to its recommended step.
    static readonly int[] ScaleSteps = { 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500 };

    public static readonly string[] OrientationNames = { "Landscape", "Portrait", "Landscape (flipped)", "Portrait (flipped)" };

    [DllImport("user32.dll")]
    static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [DllImport("user32.dll")]
    static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] byte[] paths, ref uint numModes, [Out] byte[] modes, IntPtr topology);
    [DllImport("user32.dll")]
    static extern int DisplayConfigGetDeviceInfo(byte[] packet);
    [DllImport("user32.dll")]
    static extern int DisplayConfigSetDeviceInfo(byte[] packet);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public short SpecVersion, DriverVersion, Size, DriverExtra;
        public int Fields, PositionX, PositionY, Orientation, FixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public short LogPixels;
        public int BitsPerPel, Width, Height, DisplayFlags, Frequency, IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettings(string device, int mode, ref DevMode devMode);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int ChangeDisplaySettingsEx(string device, ref DevMode devMode, IntPtr hwnd, int flags, IntPtr param);

    [StructLayout(LayoutKind.Sequential)]
    struct Luid
    {
        public uint Low;
        public int High;
    }

    [DllImport("mscms.dll")]
    static extern int ColorProfileGetDisplayList(int scope, Luid adapter, uint source, out IntPtr list, out uint count);
    [DllImport("mscms.dll")]
    static extern int ColorProfileGetDisplayDefault(int scope, Luid adapter, uint source, int type, int subtype, out IntPtr name);
    [DllImport("mscms.dll", CharSet = CharSet.Unicode)]
    static extern int ColorProfileSetDisplayDefaultAssociation(int scope, string name, int type, int subtype, Luid adapter, uint source);
    [DllImport("mscms.dll", CharSet = CharSet.Unicode)]
    static extern int ColorProfileRemoveDisplayAssociation(int scope, string name, Luid adapter, uint source, [MarshalAs(UnmanagedType.Bool)] bool advancedColor);
    [DllImport("mscms.dll", CharSet = CharSet.Unicode)]
    static extern int ColorProfileAddDisplayAssociation(int scope, string name, Luid adapter, uint source, [MarshalAs(UnmanagedType.Bool)] bool setAsDefault, [MarshalAs(UnmanagedType.Bool)] bool advancedColor);
    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr memory);

    public sealed class Monitor
    {
        public string Name, GdiName;
        public uint TargetId, SourceId;
        internal byte[] TargetAdapter, SourceAdapter;
    }

    // Displays currently showing a picture, since only those have settings to change.
    public static List<Monitor> Active()
    {
        const int PathSourceAdapter = 0, PathSourceId = 8, PathTargetAdapter = 20, PathTargetId = 28, PathInfoSize = 72, ModeInfoSize = 64;
        List<Monitor> monitors = new List<Monitor>();
        uint numPaths, numModes;
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out numPaths, out numModes) != 0) return monitors;
        byte[] paths = new byte[numPaths * PathInfoSize];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPaths, paths, ref numModes, new byte[numModes * ModeInfoSize], IntPtr.Zero) != 0) return monitors;

        for (int i = 0; i < numPaths; i++)
        {
            int at = i * PathInfoSize;
            Monitor m = new Monitor
            {
                TargetId = BitConverter.ToUInt32(paths, at + PathTargetId),
                SourceId = BitConverter.ToUInt32(paths, at + PathSourceId),
                TargetAdapter = new byte[8],
                SourceAdapter = new byte[8]
            };
            Array.Copy(paths, at + PathTargetAdapter, m.TargetAdapter, 0, 8);
            Array.Copy(paths, at + PathSourceAdapter, m.SourceAdapter, 0, 8);

            byte[] name = TargetPacket(GetTargetName, 420, m);
            m.Name = DisplayConfigGetDeviceInfo(name) == 0 ? Text(name, 36, 128) : "";
            if (m.Name.Length == 0) m.Name = "Unnamed display";
            byte[] source = SourcePacket(GetSourceName, 84, m);
            m.GdiName = DisplayConfigGetDeviceInfo(source) == 0 ? Text(source, 20, 64) : null;
            monitors.Add(m);
        }
        monitors.Sort(delegate(Monitor a, Monitor b) { return a.TargetId.CompareTo(b.TargetId); });
        return monitors;
    }

    static string Text(byte[] bytes, int at, int length)
    {
        string text = Encoding.Unicode.GetString(bytes, at, length);
        int end = text.IndexOf('\0');
        return end >= 0 ? text.Substring(0, end) : text;
    }

    static byte[] Packet(int type, int size, byte[] adapter, uint id)
    {
        byte[] packet = new byte[size];
        BitConverter.GetBytes(type).CopyTo(packet, 0);
        BitConverter.GetBytes(size).CopyTo(packet, 4);
        adapter.CopyTo(packet, 8);
        BitConverter.GetBytes(id).CopyTo(packet, 16);
        return packet;
    }

    static byte[] TargetPacket(int type, int size, Monitor m) { return Packet(type, size, m.TargetAdapter, m.TargetId); }
    static byte[] SourcePacket(int type, int size, Monitor m) { return Packet(type, size, m.SourceAdapter, m.SourceId); }

    // ---- HDR ----

    public static void GetHdr(Monitor m, out bool supported, out bool enabled)
    {
        byte[] packet = TargetPacket(GetAdvancedColor, 32, m);
        supported = enabled = false;
        if (DisplayConfigGetDeviceInfo(packet) != 0) return;
        uint flags = BitConverter.ToUInt32(packet, 20);
        bool forcedOff = (flags & 8) != 0;
        supported = (flags & 1) != 0 && !forcedOff;
        enabled = (flags & 2) != 0;
    }

    public static int SetHdr(Monitor m, bool on)
    {
        byte[] packet = TargetPacket(SetAdvancedColor, 24, m);
        BitConverter.GetBytes(on ? 1 : 0).CopyTo(packet, 20);
        return DisplayConfigSetDeviceInfo(packet);
    }

    // ---- SDR brightness: the Settings slider's 0-100 is 80-480 nits, and Windows counts 1000 as 80 nits ----

    public static int GetSdrBrightness(Monitor m)
    {
        byte[] packet = TargetPacket(GetSdrWhite, 24, m);
        if (DisplayConfigGetDeviceInfo(packet) != 0) return -1;
        int nits = (int)(BitConverter.ToUInt32(packet, 20) * 80L / 1000);
        return Math.Max(0, Math.Min(100, (nits - 80 + 2) / 4));
    }

    public static int SetSdrBrightness(Monitor m, int percent)
    {
        int nits = 80 + percent * 4;
        byte[] packet = TargetPacket(SetSdrWhite, 28, m);
        BitConverter.GetBytes(nits * 1000 / 80).CopyTo(packet, 20);
        packet[24] = 1; // Final value, not a live slider preview.
        return DisplayConfigSetDeviceInfo(packet);
    }

    // ---- Auto HDR is one setting for the whole PC ----

    const string GpuPrefsKey = @"Software\Microsoft\DirectX\UserGpuPreferences", GpuPrefsValue = "DirectXUserGlobalSettings";

    public static string ReadGpuPrefs()
    {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(GpuPrefsKey))
            return key == null ? null : key.GetValue(GpuPrefsValue) as string;
    }

    public static bool GetAutoHdr()
    {
        Match match = Regex.Match(ReadGpuPrefs() ?? "", "AutoHDREnable=(\\d+)");
        return match.Success && match.Groups[1].Value != "0";
    }

    public static int WriteGpuPrefs(string value)
    {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(GpuPrefsKey))
        {
            if (value == null) key.DeleteValue(GpuPrefsValue, false);
            else key.SetValue(GpuPrefsValue, value);
        }
        return 0;
    }

    public static int SetAutoHdr(bool on)
    {
        string token = "AutoHDREnable=" + (on ? 1 : 0);
        string current = ReadGpuPrefs() ?? "";
        return WriteGpuPrefs(Regex.IsMatch(current, "AutoHDREnable=\\d+")
            ? Regex.Replace(current, "AutoHDREnable=\\d+", token)
            : token + ";" + current);
    }

    // ---- Colour profile ----

    static Luid AdapterOf(Monitor m)
    {
        return new Luid { Low = BitConverter.ToUInt32(m.SourceAdapter, 0), High = BitConverter.ToInt32(m.SourceAdapter, 4) };
    }

    public static List<string> GetProfiles(Monitor m)
    {
        List<string> profiles = new List<string>();
        foreach (int scope in new[] { ScopeCurrentUser, ScopeSystemWide })
        {
            IntPtr list;
            uint count;
            if (ColorProfileGetDisplayList(scope, AdapterOf(m), m.SourceId, out list, out count) != 0 || list == IntPtr.Zero) continue;
            for (int i = 0; i < count; i++)
            {
                IntPtr item = Marshal.ReadIntPtr(list, i * IntPtr.Size);
                string name = Marshal.PtrToStringUni(item);
                if (!profiles.Contains(name)) profiles.Add(name);
                LocalFree(item);
            }
            LocalFree(list);
        }
        return profiles;
    }

    // HDR and SDR each keep their own default profile, as they do in Settings.
    public static string GetDefaultProfile(Monitor m, bool hdr)
    {
        IntPtr name;
        if (ColorProfileGetDisplayDefault(ScopeCurrentUser, AdapterOf(m), m.SourceId, ProfileIcc, hdr ? SubtypeExtended : SubtypeStandard, out name) != 0 || name == IntPtr.Zero)
            return null;
        string text = Marshal.PtrToStringUni(name);
        LocalFree(name);
        return text;
    }

    public static int SetDefaultProfile(Monitor m, string name, bool hdr)
    {
        return ColorProfileSetDisplayDefaultAssociation(ScopeCurrentUser, name, ProfileIcc, hdr ? SubtypeExtended : SubtypeStandard, AdapterOf(m), m.SourceId);
    }

    // Puts things back when there was no default before: keeps the profile listed, but no longer in use.
    public static int ClearDefaultProfile(Monitor m, string name, bool hdr)
    {
        int error = ColorProfileRemoveDisplayAssociation(ScopeCurrentUser, name, AdapterOf(m), m.SourceId, hdr);
        return error != 0 ? error : ColorProfileAddDisplayAssociation(ScopeCurrentUser, name, AdapterOf(m), m.SourceId, false, hdr);
    }

    // ---- Scale ----

    public static List<int> GetScales(Monitor m, out int current, out int recommended)
    {
        List<int> scales = new List<int>();
        current = recommended = 0;
        byte[] packet = SourcePacket(GetDpiScale, 32, m);
        if (DisplayConfigGetDeviceInfo(packet) != 0) return scales;
        int min = BitConverter.ToInt32(packet, 20), now = BitConverter.ToInt32(packet, 24), max = BitConverter.ToInt32(packet, 28);
        int recommendedStep = Math.Abs(min);
        for (int step = recommendedStep + min; step <= recommendedStep + max && step < ScaleSteps.Length; step++)
            if (step >= 0) scales.Add(ScaleSteps[step]);
        if (recommendedStep < ScaleSteps.Length) recommended = ScaleSteps[recommendedStep];
        if (recommendedStep + now >= 0 && recommendedStep + now < ScaleSteps.Length) current = ScaleSteps[recommendedStep + now];
        return scales;
    }

    public static int SetScale(Monitor m, int percent)
    {
        byte[] read = SourcePacket(GetDpiScale, 32, m);
        int error = DisplayConfigGetDeviceInfo(read);
        if (error != 0) return error;
        int recommendedStep = Math.Abs(BitConverter.ToInt32(read, 20));
        byte[] packet = SourcePacket(SetDpiScale, 24, m);
        BitConverter.GetBytes(Array.IndexOf(ScaleSteps, percent) - recommendedStep).CopyTo(packet, 20);
        return DisplayConfigSetDeviceInfo(packet);
    }

    // ---- Resolution and orientation ----

    static bool CurrentMode(Monitor m, out DevMode mode)
    {
        mode = new DevMode();
        mode.Size = (short)Marshal.SizeOf(typeof(DevMode));
        return m.GdiName != null && EnumDisplaySettings(m.GdiName, ENUM_CURRENT_SETTINGS, ref mode);
    }

    public static int GetOrientation(Monitor m)
    {
        DevMode mode;
        return CurrentMode(m, out mode) ? mode.Orientation : 0;
    }

    public static Size GetResolution(Monitor m)
    {
        DevMode mode;
        return CurrentMode(m, out mode) ? new Size(mode.Width, mode.Height) : Size.Empty;
    }

    // Windows lists modes in the current orientation, so these already match what's on screen.
    public static List<Size> GetResolutions(Monitor m)
    {
        const int MinColorBits = 32;
        List<Size> sizes = new List<Size>();
        if (m.GdiName == null) return sizes;
        DevMode mode = new DevMode();
        mode.Size = (short)Marshal.SizeOf(typeof(DevMode));
        for (int i = 0; EnumDisplaySettings(m.GdiName, i, ref mode); i++)
        {
            Size size = new Size(mode.Width, mode.Height);
            if (mode.BitsPerPel >= MinColorBits && !sizes.Contains(size)) sizes.Add(size);
        }
        sizes.Sort(delegate(Size a, Size b) { return a.Width != b.Width ? b.Width.CompareTo(a.Width) : b.Height.CompareTo(a.Height); });
        return sizes;
    }

    // The mode the monitor itself says it's best at, turned to match the current orientation.
    public static Size GetRecommendedResolution(Monitor m)
    {
        byte[] packet = TargetPacket(GetPreferredMode, 80, m);
        if (DisplayConfigGetDeviceInfo(packet) != 0) return Size.Empty;
        Size size = new Size(BitConverter.ToInt32(packet, 20), BitConverter.ToInt32(packet, 24));
        return GetOrientation(m) % 2 == 1 ? new Size(size.Height, size.Width) : size;
    }

    public static int SetResolution(Monitor m, Size size)
    {
        DevMode mode;
        if (!CurrentMode(m, out mode)) return -1;
        mode.Width = size.Width;
        mode.Height = size.Height;
        mode.Fields = DM_PELSWIDTH | DM_PELSHEIGHT;
        return ChangeMode(m, ref mode);
    }

    public static int SetOrientation(Monitor m, int orientation)
    {
        DevMode mode;
        if (!CurrentMode(m, out mode)) return -1;
        if (mode.Orientation % 2 != orientation % 2)
        {
            int width = mode.Width;
            mode.Width = mode.Height;
            mode.Height = width;
        }
        mode.Orientation = orientation;
        mode.Fields = DM_DISPLAYORIENTATION | DM_PELSWIDTH | DM_PELSHEIGHT;
        return ChangeMode(m, ref mode);
    }

    static int ChangeMode(Monitor m, ref DevMode mode)
    {
        int result = ChangeDisplaySettingsEx(m.GdiName, ref mode, IntPtr.Zero, CDS_TEST, IntPtr.Zero);
        return result != 0 ? result : ChangeDisplaySettingsEx(m.GdiName, ref mode, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
    }
}

// A field that opens a list of choices. It only ever reports a choice the person actually clicked.
sealed class OptionPicker : DeviceButton
{
    readonly ContextMenuStrip menu = new ContextMenuStrip();
    readonly float scale;
    List<string> options = new List<string>();
    string current;

    public event Action<int> Chosen;

    public OptionPicker(float scale) : base(scale)
    {
        this.scale = scale;
        menu.Renderer = new ToolStripProfessionalRenderer(new MenuColors()) { RoundedEdges = false };
        menu.Font = Ui.Font(14, false, scale);
        menu.BackColor = Ui.Card;
        menu.ForeColor = Ui.Text;
        Click += delegate { Open(); };
    }

    public void SetOptions(List<string> options, string current, string whenEmpty)
    {
        this.options = options;
        this.current = current;
        Enabled = options.Count > 0;
        Text = options.Count == 0 ? whenEmpty : current ?? "Choose";
        Invalidate();
    }

    void Open()
    {
        menu.Items.Clear();
        for (int i = 0; i < options.Count; i++)
        {
            int index = i;
            ToolStripMenuItem item = new ToolStripMenuItem(options[i].Replace("&", "&&")) { Checked = options[i] == current, ForeColor = Ui.Text };
            item.Click += delegate { if (Chosen != null) Chosen(index); };
            menu.Items.Add(item);
        }
        menu.MinimumSize = new Size(Width, 0);
        menu.Show(this, 0, Height + (int)(2 * scale));
    }
}

// "Brightness & colour" and "Scale and layout". Every change goes through Keep or Cancel, and the
// controls are re-read from Windows afterwards, so they always show what's really set.
sealed class SettingsPage : Panel
{
    readonly float scale;
    readonly Action<string> report;
    readonly IWin32Window owner;
    readonly OptionPicker monitorPicker, profilePicker, scalePicker, resolutionPicker, orientationPicker;
    readonly ToggleSwitch hdr, autoHdr;
    readonly ThinSlider sdr;
    readonly Label sdrValue, hdrNote, autoHdrNote;
    List<DisplaySettings.Monitor> monitors = new List<DisplaySettings.Monitor>();
    DisplaySettings.Monitor selected;
    List<int> scales = new List<int>();
    List<Size> resolutions = new List<Size>();
    List<string> profiles = new List<string>();

    public SettingsPage(float scale, Action<string> report, IWin32Window owner)
    {
        this.scale = scale;
        this.report = report;
        this.owner = owner;

        Controls.Add(MakeLabel("Display", Ui.Background, Ui.Font(15, true, scale), Ui.Text, 22, 4, 80, 34));
        monitorPicker = new OptionPicker(scale) { Bounds = new Rectangle(S(104), S(4), S(440), S(34)), Backdrop = Ui.Background, AccessibleName = "Display to change" };
        monitorPicker.Chosen += delegate(int i) { selected = monitors[i]; RefreshValues(); };
        Controls.Add(monitorPicker);

        Card colour = new Card(scale) { Bounds = new Rectangle(S(20), S(46), S(524), S(232)) };
        Controls.Add(colour);
        colour.Controls.Add(MakeLabel("Brightness & colour", Ui.Card, Ui.Font(15, true, scale), Ui.Text, 16, 6, 300, 26));

        colour.Controls.Add(RowLabel("Night light", 36));
        colour.Controls.Add(MakeLabel("Changed in Windows for now", Ui.Card, Ui.Font(13, false, scale), Ui.SubText, 190, 36, 180, 34));
        PillButton nightLight = new PillButton("Open in Windows", false, scale) { Bounds = new Rectangle(S(372), S(37), S(136), S(32)), Backdrop = Ui.Card };
        nightLight.Font = Ui.Font(13, true, scale);
        nightLight.Click += delegate { Process.Start("ms-settings:nightlight"); };
        colour.Controls.Add(nightLight);

        colour.Controls.Add(RowLabel("Colour profile", 74));
        profilePicker = new OptionPicker(scale) { Bounds = Field(74), AccessibleName = "Colour profile" };
        profilePicker.Chosen += delegate(int i) { ChooseProfile(profiles[i]); };
        colour.Controls.Add(profilePicker);

        colour.Controls.Add(RowLabel("HDR", 112));
        hdrNote = MakeLabel("", Ui.Card, Ui.Font(13, false, scale), Ui.SubText, 190, 112, 260, 34);
        colour.Controls.Add(hdrNote);
        hdr = MakeToggle("HDR", 113);
        hdr.Click += delegate { ChangeHdr(); };
        colour.Controls.Add(hdr);

        colour.Controls.Add(RowLabel("Auto HDR", 150));
        autoHdrNote = MakeLabel("", Ui.Card, Ui.Font(13, false, scale), Ui.SubText, 190, 150, 260, 34);
        colour.Controls.Add(autoHdrNote);
        autoHdr = MakeToggle("Auto HDR", 151);
        autoHdr.Click += delegate { ChangeAutoHdr(); };
        colour.Controls.Add(autoHdr);

        colour.Controls.Add(RowLabel("SDR brightness", 188));
        sdr = new ThinSlider(scale) { Bounds = new Rectangle(S(184), S(191), S(278), S(28)), AccessibleName = "SDR content brightness" };
        sdr.UserChanged += delegate { ShowSdrValue(); };
        sdr.Committed += delegate { ChangeSdr(); };
        colour.Controls.Add(sdr);
        sdrValue = MakeLabel("", Ui.Card, Ui.Font(13, false, scale), Ui.SubText, 466, 188, 44, 34);
        colour.Controls.Add(sdrValue);

        Card layout = new Card(scale) { Bounds = new Rectangle(S(20), S(286), S(524), S(150)) };
        Controls.Add(layout);
        layout.Controls.Add(MakeLabel("Scale and layout", Ui.Card, Ui.Font(15, true, scale), Ui.Text, 16, 6, 300, 26));

        layout.Controls.Add(RowLabel("Scale", 34));
        scalePicker = new OptionPicker(scale) { Bounds = Field(34), AccessibleName = "Scale" };
        scalePicker.Chosen += delegate(int i) { ChooseScale(scales[i]); };
        layout.Controls.Add(scalePicker);

        layout.Controls.Add(RowLabel("Display resolution", 72));
        resolutionPicker = new OptionPicker(scale) { Bounds = Field(72), AccessibleName = "Display resolution" };
        resolutionPicker.Chosen += delegate(int i) { ChooseResolution(resolutions[i]); };
        layout.Controls.Add(resolutionPicker);

        layout.Controls.Add(RowLabel("Display orientation", 110));
        orientationPicker = new OptionPicker(scale) { Bounds = Field(110), AccessibleName = "Display orientation" };
        orientationPicker.Chosen += delegate(int i) { ChooseOrientation(i); };
        layout.Controls.Add(orientationPicker);

        RefreshMonitors();
    }

    int S(int v) { return (int)Math.Round(v * scale); }

    Rectangle Field(int y) { return new Rectangle(S(190), S(y), S(318), S(34)); }

    Label RowLabel(string text, int y)
    {
        return MakeLabel(text, Ui.Card, Ui.Font(14, false, scale), Ui.Text, 16, y, 170, 34);
    }

    Label MakeLabel(string text, Color back, Font font, Color color, int x, int y, int w, int h)
    {
        return new Label { Text = text, UseMnemonic = false, BackColor = back, Font = font, ForeColor = color, TextAlign = ContentAlignment.MiddleLeft, Bounds = new Rectangle(S(x), S(y), S(w), S(h)) };
    }

    ToggleSwitch MakeToggle(string name, int y)
    {
        return new ToggleSwitch(scale) { Bounds = new Rectangle(S(454), S(y), S(54), S(32)), AccessibleName = name, Backdrop = Ui.Card };
    }

    // Keeps the same display selected if it's still there, otherwise falls back to the first one.
    public void RefreshMonitors()
    {
        monitors = DisplaySettings.Active();
        uint keep = selected != null ? selected.TargetId : 0;
        selected = monitors.Find(delegate(DisplaySettings.Monitor m) { return m.TargetId == keep; });
        if (selected == null && monitors.Count > 0) selected = monitors[0];
        RefreshValues();
    }

    void RefreshValues()
    {
        monitorPicker.SetOptions(monitors.ConvertAll(m => m.Name), selected != null ? selected.Name : null, "No display found");
        if (selected == null) return;

        bool supported, enabled;
        DisplaySettings.GetHdr(selected, out supported, out enabled);
        hdr.Enabled = supported;
        hdr.Checked = enabled;
        hdrNote.Text = supported ? "" : "This display doesn't support HDR";

        autoHdr.Enabled = enabled;
        autoHdr.Checked = DisplaySettings.GetAutoHdr();
        autoHdrNote.Text = enabled ? "" : "Turn on HDR to use this";

        int brightness = enabled ? DisplaySettings.GetSdrBrightness(selected) : -1;
        sdr.Enabled = brightness >= 0;
        if (!sdr.Dragging) sdr.Value = Math.Max(brightness, 0) / 100f;
        ShowSdrValue();

        profiles = DisplaySettings.GetProfiles(selected);
        profilePicker.SetOptions(profiles, DisplaySettings.GetDefaultProfile(selected, enabled), "None added for this display");

        int current, recommended;
        scales = DisplaySettings.GetScales(selected, out current, out recommended);
        scalePicker.SetOptions(scales.ConvertAll(s => ScaleName(s, recommended)), ScaleName(current, recommended), "Not available");

        Size best = DisplaySettings.GetRecommendedResolution(selected);
        resolutions = DisplaySettings.GetResolutions(selected);
        resolutionPicker.SetOptions(resolutions.ConvertAll(r => ResolutionName(r, best)), ResolutionName(DisplaySettings.GetResolution(selected), best), "Not available");

        orientationPicker.SetOptions(new List<string>(DisplaySettings.OrientationNames), DisplaySettings.OrientationNames[DisplaySettings.GetOrientation(selected)], "Not available");
    }

    static string ScaleName(int percent, int recommended)
    {
        return percent + "%" + (percent == recommended ? " (Recommended)" : "");
    }

    static string ResolutionName(Size size, Size best)
    {
        return size.Width + " × " + size.Height + (size == best ? " (Recommended)" : "");
    }

    void ShowSdrValue()
    {
        sdrValue.Text = sdr.Enabled ? (int)Math.Round(sdr.Value * 100) + "%" : "";
    }

    void Run(string name, Func<int> apply, Func<int> restore)
    {
        report(Program.Confirm(name, apply, restore, owner));
        RefreshValues();
    }

    void ChangeHdr()
    {
        DisplaySettings.Monitor m = selected;
        bool on = hdr.Checked;
        Run(on ? "HDR on" : "HDR off", () => DisplaySettings.SetHdr(m, on), () => DisplaySettings.SetHdr(m, !on));
    }

    void ChangeAutoHdr()
    {
        bool on = autoHdr.Checked;
        string before = DisplaySettings.ReadGpuPrefs();
        Run(on ? "Auto HDR on" : "Auto HDR off", () => DisplaySettings.SetAutoHdr(on), () => DisplaySettings.WriteGpuPrefs(before));
    }

    void ChangeSdr()
    {
        DisplaySettings.Monitor m = selected;
        int before = DisplaySettings.GetSdrBrightness(m), wanted = (int)Math.Round(sdr.Value * 100);
        if (before < 0 || before == wanted) return;
        Run("SDR brightness " + wanted + "%", () => DisplaySettings.SetSdrBrightness(m, wanted), () => DisplaySettings.SetSdrBrightness(m, before));
    }

    void ChooseProfile(string name)
    {
        DisplaySettings.Monitor m = selected;
        bool supported, on;
        DisplaySettings.GetHdr(m, out supported, out on);
        string before = DisplaySettings.GetDefaultProfile(m, on);
        Run(name, () => DisplaySettings.SetDefaultProfile(m, name, on),
            () => before != null ? DisplaySettings.SetDefaultProfile(m, before, on) : DisplaySettings.ClearDefaultProfile(m, name, on));
    }

    void ChooseScale(int percent)
    {
        DisplaySettings.Monitor m = selected;
        int before, recommended;
        DisplaySettings.GetScales(m, out before, out recommended);
        Run("Scale " + percent + "%", () => DisplaySettings.SetScale(m, percent), () => DisplaySettings.SetScale(m, before));
    }

    // The saved layout holds every display's resolution and orientation, so it can put both back exactly.
    void ChooseResolution(Size size)
    {
        DisplaySettings.Monitor m = selected;
        Display.Snapshot before;
        int error = Display.Capture(out before);
        if (error != 0) { report("Couldn't read your current setup, so nothing was changed."); return; }
        Run(size.Width + " × " + size.Height, () => DisplaySettings.SetResolution(m, size), () => Display.Restore(before));
    }

    void ChooseOrientation(int orientation)
    {
        DisplaySettings.Monitor m = selected;
        Display.Snapshot before;
        int error = Display.Capture(out before);
        if (error != 0) { report("Couldn't read your current setup, so nothing was changed."); return; }
        Run(DisplaySettings.OrientationNames[orientation], () => DisplaySettings.SetOrientation(m, orientation), () => Display.Restore(before));
    }
}
