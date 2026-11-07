using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Xml.Serialization;
using Microsoft.Win32;

public class SavedMonitor
{
    public uint TargetId;
    public string Name, Profile;
    public bool Hdr;
    public int Sdr, Scale, Width, Height, Orientation;
}

// The settings on each screen plus audio, captured from Windows at one moment. It deliberately
// holds no screen mode: resetting never changes which screens are on or how they're arranged.
public class ResetPoint
{
    public string Name;
    public DateTime Created;
    public string GpuPrefs, OutputId, InputId;
    public float OutputVolume, InputVolume;
    public bool OutputMuted, InputMuted;
    public List<SavedMonitor> Monitors = new List<SavedMonitor>();

    public static ResetPoint Capture(string name)
    {
        ResetPoint point = new ResetPoint { Name = name, Created = DateTime.Now, GpuPrefs = DisplaySettings.ReadGpuPrefs() };

        foreach (DisplaySettings.Monitor m in DisplaySettings.Active())
        {
            bool supported, hdr;
            DisplaySettings.GetHdr(m, out supported, out hdr);
            int scale, recommended;
            DisplaySettings.GetScales(m, out scale, out recommended);
            Size size = DisplaySettings.GetResolution(m);
            point.Monitors.Add(new SavedMonitor
            {
                TargetId = m.TargetId,
                Name = m.Name,
                Hdr = hdr,
                Sdr = hdr ? DisplaySettings.GetSdrBrightness(m) : -1,
                Scale = scale,
                Width = size.Width,
                Height = size.Height,
                Orientation = DisplaySettings.GetOrientation(m),
                Profile = DisplaySettings.GetDefaultProfile(m, hdr)
            });
        }

        point.OutputId = Audio.DefaultId(AudioFlow.Output);
        point.InputId = Audio.DefaultId(AudioFlow.Input);
        ReadLevels(point.OutputId, out point.OutputVolume, out point.OutputMuted);
        ReadLevels(point.InputId, out point.InputVolume, out point.InputMuted);
        return point;
    }

    static void ReadLevels(string id, out float volume, out bool muted)
    {
        volume = -1;
        muted = false;
        if (id == null) return;
        try
        {
            AudioControl control = Audio.Open(id);
            volume = control.Volume;
            muted = control.Muted;
        }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    // Anything that can't go back (a monitor or audio device that's gone, say) is listed here.
    [XmlIgnore]
    public List<string> Problems = new List<string>();

    // Only touches what differs, and only on screens that are on right now. A screen that's off stays off.
    public int Apply()
    {
        Problems.Clear();
        List<DisplaySettings.Monitor> active = DisplaySettings.Active();
        foreach (SavedMonitor saved in Monitors)
        {
            DisplaySettings.Monitor m = active.Find(delegate(DisplaySettings.Monitor a) { return a.TargetId == saved.TargetId; });
            if (m == null) { Problems.Add(saved.Name + " (it's off or unplugged)"); continue; }
            ApplyMonitor(saved, m);
        }

        if (DisplaySettings.ReadGpuPrefs() != GpuPrefs) DisplaySettings.WriteGpuPrefs(GpuPrefs);
        ApplyAudio(AudioFlow.Output, OutputId, OutputVolume, OutputMuted);
        ApplyAudio(AudioFlow.Input, InputId, InputVolume, InputMuted);
        return 0;
    }

    void ApplyMonitor(SavedMonitor saved, DisplaySettings.Monitor m)
    {
        // Orientation first, because it swaps width and height and the saved size is for the saved orientation.
        if (DisplaySettings.GetOrientation(m) != saved.Orientation)
            Check(saved.Name + " orientation", DisplaySettings.SetOrientation(m, saved.Orientation));
        Size size = new Size(saved.Width, saved.Height);
        if (!size.IsEmpty && DisplaySettings.GetResolution(m) != size)
            Check(saved.Name + " resolution", DisplaySettings.SetResolution(m, size));

        bool supported, hdr;
        DisplaySettings.GetHdr(m, out supported, out hdr);
        if (hdr != saved.Hdr && supported)
        {
            Check(saved.Name + " HDR", DisplaySettings.SetHdr(m, saved.Hdr));
            hdr = saved.Hdr;
        }
        if (saved.Hdr && saved.Sdr >= 0 && DisplaySettings.GetSdrBrightness(m) != saved.Sdr)
            Check(saved.Name + " SDR brightness", DisplaySettings.SetSdrBrightness(m, saved.Sdr));

        int scale, recommended;
        DisplaySettings.GetScales(m, out scale, out recommended);
        if (saved.Scale > 0 && scale != saved.Scale)
            Check(saved.Name + " scale", DisplaySettings.SetScale(m, saved.Scale));

        if (saved.Profile != null && DisplaySettings.GetDefaultProfile(m, hdr) != saved.Profile)
            Check(saved.Name + " colour profile", DisplaySettings.SetDefaultProfile(m, saved.Profile, hdr));
    }

    void Check(string what, int error)
    {
        if (error != 0) Problems.Add(what);
    }

    void ApplyAudio(AudioFlow flow, string id, float volume, bool muted)
    {
        if (id == null) return;
        try
        {
            if (Audio.DefaultId(flow) != id) Audio.SetDefault(id);
            AudioControl control = Audio.Open(id);
            if (volume >= 0) control.Volume = volume;
            control.Muted = muted;
        }
        catch (Exception e)
        {
            if (!(e is System.Runtime.InteropServices.COMException || e is InvalidCastException)) throw;
            Problems.Add(flow + " audio device");
        }
    }
}

// Original settings is kept in a read-only file and again in the registry, and each copy repairs the other.
// There's deliberately no way to delete it here: extra points are stored separately and are the only ones removable.
sealed class ResetStore
{
    const string OriginalName = "Original settings";
    readonly string folder, registryPath;
    static readonly XmlSerializer OneSerializer = new XmlSerializer(typeof(ResetPoint));
    static readonly XmlSerializer ManySerializer = new XmlSerializer(typeof(List<ResetPoint>));

    public ResetStore() : this(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenSwitch"),
        @"Software\ScreenSwitch")
    {
    }

    public ResetStore(string folder, string registryPath)
    {
        this.folder = folder;
        this.registryPath = registryPath;
    }

    string OriginalFile { get { return Path.Combine(folder, "original.xml"); } }
    string ExtrasFile { get { return Path.Combine(folder, "reset-points.xml"); } }

    static string ToBase64(ResetPoint point)
    {
        using (MemoryStream stream = new MemoryStream())
        {
            OneSerializer.Serialize(stream, point);
            return Convert.ToBase64String(stream.ToArray());
        }
    }

    static ResetPoint FromBase64(string text)
    {
        try
        {
            using (MemoryStream stream = new MemoryStream(Convert.FromBase64String(text)))
                return (ResetPoint)OneSerializer.Deserialize(stream);
        }
        catch (Exception e)
        {
            if (e is FormatException || e is InvalidOperationException) return null;
            throw;
        }
    }

    string ReadFileCopy()
    {
        try { return Convert.ToBase64String(File.ReadAllBytes(OriginalFile)); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    string ReadRegistryCopy()
    {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(registryPath))
            return key == null ? null : key.GetValue("OriginalResetPoint") as string;
    }

    void WriteFileCopy(string base64)
    {
        Directory.CreateDirectory(folder);
        if (File.Exists(OriginalFile)) File.SetAttributes(OriginalFile, FileAttributes.Normal);
        File.WriteAllBytes(OriginalFile, Convert.FromBase64String(base64));
        File.SetAttributes(OriginalFile, FileAttributes.ReadOnly);
    }

    void WriteRegistryCopy(string base64)
    {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(registryPath))
            key.SetValue("OriginalResetPoint", base64);
    }

    // The saved point, with any missing or damaged copy repaired. Null only if both copies are gone.
    public ResetPoint Original()
    {
        string fromFile = ReadFileCopy(), fromRegistry = ReadRegistryCopy();
        ResetPoint fileGood = fromFile != null ? FromBase64(fromFile) : null;
        ResetPoint registryGood = fromRegistry != null ? FromBase64(fromRegistry) : null;

        if (fileGood != null && registryGood == null) WriteRegistryCopy(fromFile);
        if (registryGood != null && fileGood == null) WriteFileCopy(fromRegistry);
        return fileGood ?? registryGood;
    }

    // Runs at every launch, but only ever captures once: an existing Original is never replaced.
    public ResetPoint EnsureOriginal()
    {
        ResetPoint existing = Original();
        if (existing != null) return existing;
        ResetPoint point = ResetPoint.Capture(OriginalName);
        string base64 = ToBase64(point);
        WriteRegistryCopy(base64);
        WriteFileCopy(base64);
        return point;
    }

    public List<ResetPoint> Extras()
    {
        try
        {
            using (FileStream stream = File.OpenRead(ExtrasFile))
                return (List<ResetPoint>)ManySerializer.Deserialize(stream);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (InvalidOperationException) { }
        return new List<ResetPoint>();
    }

    void SaveExtras(List<ResetPoint> points)
    {
        Directory.CreateDirectory(folder);
        using (FileStream stream = File.Create(ExtrasFile))
            ManySerializer.Serialize(stream, points);
    }

    public void AddExtra(ResetPoint point)
    {
        List<ResetPoint> points = Extras();
        points.Add(point);
        SaveExtras(points);
    }

    public void DeleteExtra(ResetPoint point)
    {
        List<ResetPoint> points = Extras();
        points.RemoveAll(delegate(ResetPoint p) { return p.Created == point.Created && p.Name == point.Name; });
        SaveExtras(points);
    }
}

sealed class ResetPage : Panel
{
    readonly float scale;
    readonly ResetStore store;
    readonly Action<string> report;
    readonly Action changed;
    readonly IWin32Window owner;
    readonly Label originalInfo;
    readonly PillButton originalReset;
    readonly TextBox nameBox;
    readonly Panel list = new Panel();
    ResetPoint original;

    public ResetPage(float scale, ResetStore store, Action<string> report, Action changed, IWin32Window owner)
    {
        this.scale = scale;
        this.store = store;
        this.report = report;
        this.changed = changed;
        this.owner = owner;

        Card card = new Card(scale) { Bounds = new Rectangle(S(20), S(6), S(524), S(296)) };
        Controls.Add(card);

        card.Controls.Add(MakeLabel("Original settings", Ui.Font(15, true, scale), Ui.Text, 16, 10, 360, 24));
        originalInfo = MakeLabel("", Ui.Font(13, false, scale), Ui.SubText, 16, 34, 360, 22);
        card.Controls.Add(originalInfo);
        originalReset = new PillButton("Reset", true, scale) { Bounds = new Rectangle(S(420), S(15), S(88), S(36)), Backdrop = Ui.Card, AccessibleName = "Reset to original settings" };
        originalReset.Font = Ui.Font(13, true, scale);
        originalReset.Click += delegate { if (original != null) ResetTo(original); };
        card.Controls.Add(originalReset);

        Panel divider = new Panel { BackColor = Ui.Neutral, Bounds = new Rectangle(S(16), S(64), S(492), Math.Max(1, S(1))) };
        card.Controls.Add(divider);

        nameBox = new TextBox
        {
            Font = Ui.Font(14, false, scale),
            BackColor = Ui.Neutral,
            ForeColor = Ui.Text,
            BorderStyle = BorderStyle.None,
            Bounds = new Rectangle(S(28), S(82), S(250), S(22)),
            AccessibleName = "Name for a new reset point"
        };
        Panel nameField = new Panel { BackColor = Ui.Neutral, Bounds = new Rectangle(S(16), S(74), S(274), S(36)) };
        card.Controls.Add(nameBox);
        card.Controls.Add(nameField);
        nameBox.BringToFront();

        PillButton add = new PillButton("Save current settings", false, scale) { Bounds = new Rectangle(S(298), S(74), S(210), S(36)), Backdrop = Ui.Card };
        add.Font = Ui.Font(13, true, scale);
        add.Click += delegate { AddCurrent(); };
        card.Controls.Add(add);

        list.BackColor = Ui.Card;
        list.AutoScroll = true;
        list.Bounds = new Rectangle(S(8), S(116), S(508), S(172));
        card.Controls.Add(list);

        Reload();
    }

    int S(int v) { return (int)Math.Round(v * scale); }

    Label MakeLabel(string text, Font font, Color color, int x, int y, int w, int h)
    {
        return new Label { Text = text, UseMnemonic = false, BackColor = Ui.Card, Font = font, ForeColor = color, TextAlign = ContentAlignment.MiddleLeft, Bounds = new Rectangle(S(x), S(y), S(w), S(h)) };
    }

    static string WhenText(DateTime when)
    {
        return when.ToString("d MMM yyyy, HH:mm");
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) Reload();
    }

    public void Reload()
    {
        original = store.Original();
        originalReset.Enabled = original != null;
        originalInfo.Text = original != null ? "Saved " + WhenText(original.Created) + ". Can't be removed." : "Saved the first time the app runs.";

        list.Controls.Clear();
        List<ResetPoint> extras = store.Extras();
        if (extras.Count == 0)
            list.Controls.Add(MakeLabel("Name your current settings above to add another reset point.", Ui.Font(13, false, scale), Ui.SubText, 8, 6, 480, 28));
        for (int i = 0; i < extras.Count; i++) AddRow(extras[i], i);
    }

    void AddRow(ResetPoint point, int index)
    {
        int y = 4 + index * 48;
        list.Controls.Add(MakeLabel(point.Name, Ui.Font(14, false, scale), Ui.Text, 8, y, 280, 24));
        list.Controls.Add(MakeLabel(WhenText(point.Created), Ui.Font(12, false, scale), Ui.SubText, 8, y + 22, 280, 20));

        PillButton reset = new PillButton("Reset", false, scale) { Bounds = new Rectangle(S(304), S(y + 4), S(84), S(34)), Backdrop = Ui.Card, AccessibleName = "Reset to " + point.Name };
        reset.Font = Ui.Font(13, true, scale);
        reset.Click += delegate { ResetTo(point); };
        list.Controls.Add(reset);

        PillButton delete = new PillButton("Delete", false, scale) { Bounds = new Rectangle(S(394), S(y + 4), S(84), S(34)), Backdrop = Ui.Card, AccessibleName = "Delete " + point.Name };
        delete.Font = Ui.Font(13, true, scale);
        delete.Click += delegate { Delete(point); };
        list.Controls.Add(delete);
    }

    void AddCurrent()
    {
        string name = nameBox.Text.Trim();
        if (name.Length == 0) name = "Saved " + WhenText(DateTime.Now);
        try
        {
            store.AddExtra(ResetPoint.Capture(name));
            nameBox.Clear();
            report("Saved: " + name);
        }
        catch (Exception e)
        {
            if (!(e is IOException || e is UnauthorizedAccessException)) throw;
            report("Couldn't save that reset point: " + e.Message);
        }
        Reload();
    }

    void Delete(ResetPoint point)
    {
        DialogResult answer = MessageBox.Show(owner, "Delete \"" + point.Name + "\"? This can't be undone.", "Screen Switch", MessageBoxButtons.YesNo, MessageBoxIcon.None, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;
        store.DeleteExtra(point);
        report("Deleted: " + point.Name);
        Reload();
    }

    // Like every other change it asks Keep or Cancel, and Cancel returns to how things are right now.
    void ResetTo(ResetPoint point)
    {
        ResetPoint now = ResetPoint.Capture("Before reset");
        string result = Program.Confirm(point.Name, point.Apply, now.Apply, owner);
        if (point.Problems.Count > 0 && result.StartsWith("Now using", StringComparison.Ordinal))
            result += ". Couldn't restore: " + string.Join(", ", point.Problems);
        report(result);
        changed();
    }
}
