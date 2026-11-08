using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Xml.Serialization;

// A saved combination of screens, sound and a shortcut. Device ids are whatever Windows reported when it was saved.
public sealed class SwitchProfile
{
    public const string KeepScreens = "Keep";
    public string Name, Screens = KeepScreens, OutputId, InputId;
    public int Shortcut;
}

public sealed class ShortcutConfig
{
    public bool SetupDone;
    public List<int> ScreenKeys;
    public List<SwitchProfile> Profiles;
    public int MuteMicKey, PushToTalkKey, MuteOutputKey;
}

// The three sound shortcuts on the Audio tab.
static class AudioShortcuts
{
    public const string MuteMic = "mute-mic", PushToTalk = "push-to-talk", MuteOutput = "mute-output";
    public static readonly string[] Slots = { MuteMic, PushToTalk, MuteOutput };
    public static readonly string[] Names = { "Mute microphone", "Push to talk", "Mute sound" };

    public static int Get(ShortcutConfig config, string slot)
    {
        return slot == MuteMic ? config.MuteMicKey : slot == PushToTalk ? config.PushToTalkKey : config.MuteOutputKey;
    }

    public static void Set(ShortcutConfig config, string slot, int keys)
    {
        if (slot == MuteMic) config.MuteMicKey = keys;
        else if (slot == PushToTalk) config.PushToTalkKey = keys;
        else config.MuteOutputKey = keys;
    }
}

// Saved for this Windows account only, next to the reset points.
sealed class ConfigStore
{
    static readonly XmlSerializer Serializer = new XmlSerializer(typeof(ShortcutConfig));
    readonly string file;

    public ConfigStore() : this(Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenSwitch"), "profiles.xml"))
    {
    }

    public ConfigStore(string file) { this.file = file; }

    public ShortcutConfig Load()
    {
        ShortcutConfig config = null;
        try
        {
            using (FileStream stream = File.OpenRead(file))
                config = (ShortcutConfig)Serializer.Deserialize(stream);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (InvalidOperationException) { }
        return Tidy(config ?? new ShortcutConfig());
    }

    static ShortcutConfig Tidy(ShortcutConfig config)
    {
        if (config.ScreenKeys == null || config.ScreenKeys.Count != Hotkey.ScreenNames.Length)
            config.ScreenKeys = new List<int>(Hotkey.DefaultScreenKeys());
        if (config.Profiles == null) config.Profiles = new List<SwitchProfile>();
        return config;
    }

    // Written alongside first, so a crash midway can't lose what's already saved.
    public void Save(ShortcutConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        string partial = file + ".new";
        using (FileStream stream = File.Create(partial))
            Serializer.Serialize(stream, config);
        if (File.Exists(file)) File.Replace(partial, file, null);
        else File.Move(partial, file);
    }
}

static class Hotkey
{
    public static readonly string[] ScreenNames = { "Screen 1", "Screen 2", "Extend", "Duplicate" };
    public static readonly Mode[] ScreenModes = { Mode.Screen1, Mode.Screen2, Mode.Extend, Mode.Duplicate };

    // All three modifiers, since Ctrl+Alt on its own is how many keyboards type symbols.
    const Keys DefaultModifiers = Keys.Control | Keys.Alt | Keys.Shift;

    public static int[] DefaultScreenKeys()
    {
        return new[] { (int)(DefaultModifiers | Keys.D1), (int)(DefaultModifiers | Keys.D2), (int)(DefaultModifiers | Keys.D3), (int)(DefaultModifiers | Keys.D4) };
    }

    public static string Describe(Keys keys)
    {
        if (keys == Keys.None) return "Not set";
        List<string> parts = new List<string>();
        if ((keys & Keys.Control) != 0) parts.Add("Ctrl");
        if ((keys & Keys.Alt) != 0) parts.Add("Alt");
        if ((keys & Keys.Shift) != 0) parts.Add("Shift");
        Keys code = keys & Keys.KeyCode;
        parts.Add(code >= Keys.D0 && code <= Keys.D9 ? ((char)('0' + (code - Keys.D0))).ToString()
            : code >= Keys.NumPad0 && code <= Keys.NumPad9 ? "Num " + (code - Keys.NumPad0)
            : code == Keys.Next ? "PageDown"
            : code == Keys.Prior ? "PageUp"
            : code.ToString());
        return string.Join("+", parts);
    }

    public static bool IsModifierOnly(Keys keys)
    {
        Keys code = keys & Keys.KeyCode;
        return code == Keys.ShiftKey || code == Keys.ControlKey || code == Keys.Menu || code == Keys.LWin || code == Keys.RWin || code == Keys.None;
    }

    // Plain typing keys would stop working everywhere, so they need Ctrl or Alt. Keys nobody types with are
    // fine alone, which matters for push to talk, since a single key is easy to hold.
    public static string Problem(Keys keys)
    {
        Keys code = keys & Keys.KeyCode;
        bool alone = (code >= Keys.F1 && code <= Keys.F24) || (code >= Keys.NumPad0 && code <= Keys.NumPad9) || code == Keys.Pause || code == Keys.Scroll;
        if (!alone && (keys & (Keys.Control | Keys.Alt)) == 0) return "Use Ctrl or Alt in the shortcut, or a function key, number pad key, Pause or Scroll Lock.";
        return null;
    }

    // Who else in the app already uses these keys. Null if nobody does. self is left out of the check.
    public static string Holder(ShortcutConfig config, Keys keys, object self)
    {
        for (int i = 0; i < config.ScreenKeys.Count; i++)
            if (config.ScreenKeys[i] == (int)keys && !(self is int && (int)self == i)) return ScreenNames[i];
        foreach (SwitchProfile profile in config.Profiles)
            if (profile.Shortcut == (int)keys && !ReferenceEquals(profile, self)) return profile.Name;
        for (int i = 0; i < AudioShortcuts.Slots.Length; i++)
            if (AudioShortcuts.Get(config, AudioShortcuts.Slots[i]) == (int)keys && !Equals(AudioShortcuts.Slots[i], self)) return AudioShortcuts.Names[i];
        return null;
    }
}

// Owns the system-wide shortcuts for one window.
sealed class HotkeyManager
{
    const int ModAlt = 1, ModControl = 2, ModShift = 4, ModNoRepeat = 0x4000, TestId = 0xBFFF;
    public const int WM_HOTKEY = 0x312;

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr window, int id, int modifiers, int key);
    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr window, int id);

    readonly IWin32Window owner;
    readonly List<KeyValuePair<Keys, Action>> wanted = new List<KeyValuePair<Keys, Action>>();
    int registered, pauses;

    public HotkeyManager(IWin32Window owner) { this.owner = owner; }

    public List<string> Failed { get; private set; }

    static int ModifiersOf(Keys keys)
    {
        return ((keys & Keys.Alt) != 0 ? ModAlt : 0) | ((keys & Keys.Control) != 0 ? ModControl : 0) | ((keys & Keys.Shift) != 0 ? ModShift : 0) | ModNoRepeat;
    }

    public void Set(List<KeyValuePair<Keys, Action>> shortcuts)
    {
        wanted.Clear();
        wanted.AddRange(shortcuts);
        Apply();
    }

    void Clear()
    {
        for (int i = 0; i < registered; i++) UnregisterHotKey(owner.Handle, i);
        registered = 0;
    }

    void Apply()
    {
        Clear();
        Failed = new List<string>();
        if (pauses > 0) return;
        for (int i = 0; i < wanted.Count; i++)
        {
            Keys keys = wanted[i].Key;
            if (!RegisterHotKey(owner.Handle, i, ModifiersOf(keys), (int)(keys & Keys.KeyCode))) Failed.Add(Hotkey.Describe(keys));
        }
        registered = wanted.Count;
    }

    // Shortcuts must be off while new keys are being pressed, or pressing one would trigger it.
    public void Pause() { pauses++; Clear(); }
    public void Resume() { pauses--; if (pauses == 0) Apply(); }

    public bool IsFree(Keys keys)
    {
        if (!RegisterHotKey(owner.Handle, TestId, ModifiersOf(keys), (int)(keys & Keys.KeyCode))) return false;
        UnregisterHotKey(owner.Handle, TestId);
        return true;
    }

    public void Fire(int id)
    {
        if (id >= 0 && id < wanted.Count) wanted[id].Value();
    }
}

static class ProfileRunner
{
    // Applies a profile. A screen change goes through the usual Keep or Cancel, and Cancel puts the sound back too.
    public static string Use(SwitchProfile profile, IWin32Window owner)
    {
        if (Program.Busy) return "Finish the current change first.";
        string oldOutput = Audio.DefaultId(AudioFlow.Output), oldInput = Audio.DefaultId(AudioFlow.Input);

        Mode mode;
        bool changeScreens = Enum.TryParse(profile.Screens, out mode) && !Display.IsCurrent(mode);
        if (!changeScreens)
        {
            string problem = SetSound(profile);
            return problem ?? "Now using: " + profile.Name;
        }

        Display.Snapshot before;
        int error = Display.Capture(out before);
        if (error != 0)
            return Program.Fail("Couldn't read your current display setup, so nothing was changed.", error, owner);

        string soundProblem = null;
        string result = Program.Confirm(profile.Name,
            delegate
            {
                int applied = Display.Apply(mode);
                if (applied == 0) soundProblem = SetSound(profile);
                return applied;
            },
            delegate
            {
                RestoreSound(profile, oldOutput, oldInput);
                return Display.Restore(before);
            },
            owner);
        return soundProblem ?? result;
    }

    // Returns what went wrong, or null. A missing device doesn't stop the rest of the profile.
    static string SetSound(SwitchProfile profile)
    {
        string problem = null;
        foreach (string id in new[] { profile.OutputId, profile.InputId })
        {
            if (id == null) continue;
            try { Audio.SetDefault(id); }
            catch (Exception e)
            {
                if (!(e is COMException || e is InvalidCastException)) throw;
                problem = "A sound device in " + profile.Name + " isn't connected, so it wasn't changed.";
            }
        }
        return problem;
    }

    static void RestoreSound(SwitchProfile profile, string oldOutput, string oldInput)
    {
        try
        {
            if (profile.OutputId != null && oldOutput != null) Audio.SetDefault(oldOutput);
            if (profile.InputId != null && oldInput != null) Audio.SetDefault(oldInput);
        }
        catch (COMException) { } // Going back to the old screens matters more.
    }

    public static string Summary(SwitchProfile profile, List<AudioDevice> outputs, List<AudioDevice> inputs)
    {
        Mode mode;
        int screen = Enum.TryParse(profile.Screens, out mode) ? Array.IndexOf(Hotkey.ScreenModes, mode) : -1;
        // The shortcut goes first, since long device names get cut off.
        List<string> parts = new List<string>();
        if (profile.Shortcut != 0) parts.Add(Hotkey.Describe((Keys)profile.Shortcut));
        parts.Add(screen >= 0 ? Hotkey.ScreenNames[screen] : "Screens unchanged");
        if (profile.OutputId != null) parts.Add(DeviceName(profile.OutputId, outputs));
        if (profile.InputId != null) parts.Add(DeviceName(profile.InputId, inputs));
        return string.Join("  ·  ", parts);
    }

    static string DeviceName(string id, List<AudioDevice> devices)
    {
        AudioDevice device = devices.Find(delegate(AudioDevice d) { return d.Id == id; });
        return device != null ? device.Name : "Device not connected";
    }
}

// A field that records the next keys pressed. Click, press the keys, done.
sealed class KeyButton : SoftButton
{
    readonly float scale;
    readonly HotkeyManager hotkeys;
    readonly Func<Keys, string> holder;
    readonly Action<string> report;
    Keys value;
    bool capturing;

    public event EventHandler Changed;

    public KeyButton(float scale, HotkeyManager hotkeys, Func<Keys, string> holder, Action<string> report)
    {
        this.scale = scale;
        this.hotkeys = hotkeys;
        this.holder = holder;
        this.report = report;
        Font = Ui.Font(13, true, scale);
        Backdrop = Ui.Card;
        Text = Hotkey.Describe(Keys.None);
        Click += delegate { Begin(); };
    }

    public Keys Value
    {
        get { return value; }
        set { this.value = value; Text = Hotkey.Describe(value); Invalidate(); }
    }

    void Begin()
    {
        if (capturing) return;
        capturing = true;
        hotkeys.Pause();
        Text = "Press keys...";
        Invalidate();
        report("Press the new keys. Backspace clears it, Esc cancels.");
    }

    void End()
    {
        if (!capturing) return;
        capturing = false;
        hotkeys.Resume();
        Text = Hotkey.Describe(value);
        Invalidate();
    }

    void Accept(Keys keys)
    {
        value = keys;
        End();
        if (Changed != null) Changed(this, EventArgs.Empty);
        report(keys == Keys.None ? "Shortcut cleared." : "Shortcut set to " + Hotkey.Describe(keys) + ".");
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!capturing) return base.ProcessCmdKey(ref msg, keyData);
        if (Hotkey.IsModifierOnly(keyData)) return true;
        if (keyData == Keys.Escape) { End(); report("Shortcut not changed."); return true; }
        if (keyData == Keys.Back || keyData == Keys.Delete) { Accept(Keys.None); return true; }

        string problem = Hotkey.Problem(keyData);
        string used = problem == null ? holder(keyData) : null;
        if (used != null) problem = Hotkey.Describe(keyData) + " is already used by " + used + ".";
        else if (problem == null && !hotkeys.IsFree(keyData)) problem = Hotkey.Describe(keyData) + " is taken by Windows or another app.";
        if (problem != null) { report(problem + " Try other keys."); return true; }

        Accept(keyData);
        return true;
    }

    protected override void OnLostFocus(EventArgs e)
    {
        End();
        base.OnLostFocus(e);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        End();
        base.OnHandleDestroyed(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Backdrop);
        Ui.Prepare(g);
        using (GraphicsPath field = Logo.Rounded(0.5f, 0.5f, Width - 2, Height - 2, 8 * scale))
        using (SolidBrush brush = new SolidBrush(Blend(Ui.Neutral, Ui.NeutralHover, Ui.NeutralPressed)))
        {
            g.FillPath(brush, field);
            if (capturing || ShowRing)
                using (Pen ring = new Pen(capturing ? Ui.Accent : Ui.Honey, 2 * scale)) g.DrawPath(ring, field);
        }
        using (StringFormat center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter })
        using (SolidBrush text = new SolidBrush(value == Keys.None && !capturing ? Ui.SubText : Ui.Text))
            g.DrawString(Text, Font, text, new RectangleF(4 * scale, 0, Width - 8 * scale, Height), center);
    }
}

// A drop-down list of named choices.
sealed class ChoiceButton : DeviceButton
{
    readonly ContextMenuStrip menu = new ContextMenuStrip();
    readonly float scale;
    readonly List<KeyValuePair<string, string>> choices = new List<KeyValuePair<string, string>>();
    string value;

    public ChoiceButton(float scale) : base(scale)
    {
        this.scale = scale;
        menu.Renderer = new ToolStripProfessionalRenderer(new MenuColors()) { RoundedEdges = false };
        menu.Font = Ui.Font(14, false, scale);
        menu.BackColor = Ui.Card;
        menu.ForeColor = Ui.Text;
        Click += delegate { Open(); };
    }

    public void Add(string label, string choice) { choices.Add(new KeyValuePair<string, string>(label, choice)); }

    public string Value
    {
        get { return value; }
        set
        {
            this.value = value;
            KeyValuePair<string, string> match = choices.Find(delegate(KeyValuePair<string, string> c) { return c.Value == value; });
            Text = match.Key ?? "Device not connected";
            Invalidate();
        }
    }

    void Open()
    {
        menu.Items.Clear();
        foreach (KeyValuePair<string, string> choice in choices)
        {
            KeyValuePair<string, string> picked = choice;
            ToolStripMenuItem item = new ToolStripMenuItem(choice.Key.Replace("&", "&&")) { Checked = choice.Value == value, ForeColor = Ui.Text };
            item.Click += delegate { Value = picked.Value; };
            menu.Items.Add(item);
        }
        menu.MinimumSize = new Size(Width, 0);
        menu.Show(this, 0, Height + (int)Math.Round(2 * scale));
    }
}

// The fields for one profile, shared by the editor and the setup menu.
sealed class ProfileFields : Panel
{
    readonly float scale;
    readonly SwitchProfile profile;
    readonly TextBox nameBox = new TextBox();
    readonly ChoiceButton screens, output, input;
    readonly KeyButton shortcut;

    int S(int v) { return (int)Math.Round(v * scale); }

    public ProfileFields(float scale, ShortcutConfig config, SwitchProfile profile, HotkeyManager hotkeys, Action<string> report)
    {
        this.scale = scale;
        this.profile = profile;
        BackColor = Ui.Card;

        AddRowLabel("Name", 0);
        Panel nameField = new Panel { BackColor = Ui.Neutral, Bounds = new Rectangle(S(84), S(0), S(392), S(34)) };
        nameBox.Font = Ui.Font(14, false, scale);
        nameBox.BackColor = Ui.Neutral;
        nameBox.ForeColor = Ui.Text;
        nameBox.BorderStyle = BorderStyle.None;
        nameBox.Bounds = new Rectangle(S(10), S(8), S(372), S(20));
        nameBox.AccessibleName = "Profile name";
        nameBox.Text = profile.Name ?? "";
        nameField.Controls.Add(nameBox);
        Controls.Add(nameField);

        AddRowLabel("Screens", 44);
        screens = AddChoice(44, "Screens");
        screens.Add("Don't change", SwitchProfile.KeepScreens);
        for (int i = 0; i < Hotkey.ScreenNames.Length; i++) screens.Add(Hotkey.ScreenNames[i], Hotkey.ScreenModes[i].ToString());
        screens.Value = profile.Screens;

        AddRowLabel("Output", 88);
        output = AddChoice(88, "Sound output");
        AddRowLabel("Input", 132);
        input = AddChoice(132, "Sound input");
        output.Add("Don't change", null);
        input.Add("Don't change", null);
        try
        {
            foreach (AudioDevice device in Audio.List(AudioFlow.Output)) output.Add(device.Name, device.Id);
            foreach (AudioDevice device in Audio.List(AudioFlow.Input)) input.Add(device.Name, device.Id);
        }
        catch (COMException) { } // A profile can still be saved without sound.
        output.Value = profile.OutputId;
        input.Value = profile.InputId;

        AddRowLabel("Shortcut", 176);
        shortcut = new KeyButton(scale, hotkeys, delegate(Keys keys) { return Hotkey.Holder(config, keys, profile); }, report)
        {
            Bounds = new Rectangle(S(84), S(176), S(392), S(34)),
            AccessibleName = "Profile shortcut",
            Value = (Keys)profile.Shortcut
        };
        Controls.Add(shortcut);
    }

    void AddRowLabel(string text, int y)
    {
        Controls.Add(new Label { Text = text, Font = Ui.Font(13, false, scale), ForeColor = Ui.SubText, BackColor = Ui.Card, TextAlign = ContentAlignment.MiddleLeft, Bounds = new Rectangle(0, S(y), S(80), S(34)) });
    }

    ChoiceButton AddChoice(int y, string name)
    {
        ChoiceButton choice = new ChoiceButton(scale) { Bounds = new Rectangle(S(84), S(y), S(392), S(34)), AccessibleName = name };
        Controls.Add(choice);
        return choice;
    }

    public string NameText { get { return nameBox.Text.Trim(); } }

    public bool ChangesSomething
    {
        get { return screens.Value != SwitchProfile.KeepScreens || output.Value != null || input.Value != null; }
    }

    public void Store()
    {
        profile.Name = NameText;
        profile.Screens = screens.Value;
        profile.OutputId = output.Value;
        profile.InputId = input.Value;
        profile.Shortcut = (int)shortcut.Value;
    }
}

sealed class ProfileForm : Form
{
    readonly ProfileFields fields;
    readonly Label message = new Label();
    readonly float scale;

    int S(int v) { return (int)Math.Round(v * scale); }

    public ProfileForm(string title, float scale, ShortcutConfig config, SwitchProfile profile, HotkeyManager hotkeys)
    {
        this.scale = scale;
        Text = title;
        Icon = MainForm.AppIcon();
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Ui.Card;
        ClientSize = new Size(S(520), S(330));

        fields = new ProfileFields(scale, config, profile, hotkeys, delegate(string m) { message.Text = m; }) { Bounds = new Rectangle(S(22), S(16), S(476), S(212)) };
        Controls.Add(fields);

        message.Font = Ui.Font(13, false, scale);
        message.ForeColor = Ui.SubText;
        message.BackColor = Ui.Card;
        message.UseMnemonic = false;
        message.TextAlign = ContentAlignment.MiddleLeft;
        message.Bounds = new Rectangle(S(22), S(232), S(476), S(32));
        message.Text = "A shortcut works anywhere while Screen Switch is running.";
        Controls.Add(message);

        PillButton cancel = new PillButton("Cancel", false, scale) { Bounds = new Rectangle(S(286), S(272), S(100), S(40)), Backdrop = Ui.Card, DialogResult = DialogResult.Cancel };
        cancel.Font = Ui.Font(13, true, scale);
        Controls.Add(cancel);
        PillButton save = new PillButton("Save", true, scale) { Bounds = new Rectangle(S(398), S(272), S(100), S(40)), Backdrop = Ui.Card };
        save.Font = Ui.Font(13, true, scale);
        save.Click += delegate { TrySave(); };
        Controls.Add(save);
        CancelButton = cancel;
    }

    void TrySave()
    {
        if (fields.NameText.Length == 0) { message.Text = "Give this profile a name."; return; }
        if (!fields.ChangesSomething) { message.Text = "Choose screens or a sound device for it to change."; return; }
        fields.Store();
        DialogResult = DialogResult.OK;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Ui.StyleWindow(this);
    }
}

sealed class ProfilesPage : Panel
{
    readonly float scale;
    readonly ShortcutConfig config;
    readonly ConfigStore store;
    readonly HotkeyManager hotkeys;
    readonly Action<string> report;
    readonly Action changed, runSetup;
    readonly Action<SwitchProfile> use;
    readonly KeyButton[] screenKeys = new KeyButton[Hotkey.ScreenNames.Length];
    readonly Panel list = new Panel();

    int S(int v) { return (int)Math.Round(v * scale); }

    public ProfilesPage(float scale, ShortcutConfig config, ConfigStore store, HotkeyManager hotkeys, Action<string> report, Action changed, Action runSetup, Action<SwitchProfile> use)
    {
        this.scale = scale;
        this.config = config;
        this.store = store;
        this.hotkeys = hotkeys;
        this.report = report;
        this.changed = changed;
        this.runSetup = runSetup;
        this.use = use;

        Card keysCard = new Card(scale) { Bounds = new Rectangle(S(20), S(6), S(524), S(176)) };
        Controls.Add(keysCard);
        keysCard.Controls.Add(MakeLabel("Keyboard shortcuts", Ui.Font(15, true, scale), Ui.Text, 16, 10, 300, 24));
        keysCard.Controls.Add(MakeLabel("They work anywhere while Screen Switch is running.", Ui.Font(13, false, scale), Ui.SubText, 16, 34, 340, 20));

        PillButton setup = new PillButton("Run setup again", false, scale) { Bounds = new Rectangle(S(368), S(12), S(140), S(36)), Backdrop = Ui.Card };
        setup.Font = Ui.Font(13, true, scale);
        setup.Click += delegate { runSetup(); };
        keysCard.Controls.Add(setup);

        for (int i = 0; i < screenKeys.Length; i++)
        {
            int slot = i;
            int x = i % 2 == 0 ? 16 : 268, y = i < 2 ? 62 : 100;
            keysCard.Controls.Add(MakeLabel(Hotkey.ScreenNames[i], Ui.Font(13, false, scale), Ui.SubText, x, y, 70, 32));
            screenKeys[i] = new KeyButton(scale, hotkeys, delegate(Keys keys) { return Hotkey.Holder(config, keys, slot); }, report)
            {
                Bounds = new Rectangle(S(x + 72), S(y), S(168), S(32)),
                AccessibleName = Hotkey.ScreenNames[i] + " shortcut"
            };
            screenKeys[i].Changed += delegate { config.ScreenKeys[slot] = (int)screenKeys[slot].Value; Save(); };
            keysCard.Controls.Add(screenKeys[i]);
        }
        keysCard.Controls.Add(MakeLabel("Click one, then press the new keys. Change them any time.", Ui.Font(13, false, scale), Ui.SubText, 16, 140, 492, 24));

        Card profilesCard = new Card(scale) { Bounds = new Rectangle(S(20), S(188), S(524), S(248)) };
        Controls.Add(profilesCard);
        profilesCard.Controls.Add(MakeLabel("Profiles", Ui.Font(15, true, scale), Ui.Text, 16, 10, 300, 24));
        profilesCard.Controls.Add(MakeLabel("Switch screens and sound together.", Ui.Font(13, false, scale), Ui.SubText, 16, 34, 340, 20));
        PillButton add = new PillButton("New profile", true, scale) { Bounds = new Rectangle(S(392), S(12), S(116), S(36)), Backdrop = Ui.Card };
        add.Font = Ui.Font(13, true, scale);
        add.Click += delegate { NewProfile(); };
        profilesCard.Controls.Add(add);

        list.BackColor = Ui.Card;
        list.AutoScroll = true;
        list.Bounds = new Rectangle(S(8), S(60), S(508), S(180));
        profilesCard.Controls.Add(list);

        Reload();
    }

    Label MakeLabel(string text, Font font, Color color, int x, int y, int w, int h)
    {
        return new Label { Text = text, UseMnemonic = false, AutoEllipsis = true, BackColor = Ui.Card, Font = font, ForeColor = color, TextAlign = ContentAlignment.MiddleLeft, Bounds = new Rectangle(S(x), S(y), S(w), S(h)) };
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) Reload();
    }

    public void Reload()
    {
        for (int i = 0; i < screenKeys.Length; i++) screenKeys[i].Value = (Keys)config.ScreenKeys[i];

        List<AudioDevice> outputs = new List<AudioDevice>(), inputs = new List<AudioDevice>();
        try
        {
            outputs = Audio.List(AudioFlow.Output);
            inputs = Audio.List(AudioFlow.Input);
        }
        catch (COMException) { } // Names just show as not connected until the next look.

        list.Controls.Clear();
        if (config.Profiles.Count == 0)
            list.Controls.Add(MakeLabel("No profiles yet. Press New profile to make one.", Ui.Font(13, false, scale), Ui.SubText, 8, 6, 480, 28));
        for (int i = 0; i < config.Profiles.Count; i++) AddRow(config.Profiles[i], i, outputs, inputs);
    }

    void AddRow(SwitchProfile profile, int index, List<AudioDevice> outputs, List<AudioDevice> inputs)
    {
        int y = 2 + index * 48;
        list.Controls.Add(MakeLabel(profile.Name, Ui.Font(14, false, scale), Ui.Text, 8, y, 244, 24));
        list.Controls.Add(MakeLabel(ProfileRunner.Summary(profile, outputs, inputs), Ui.Font(12, false, scale), Ui.SubText, 8, y + 22, 244, 20));

        AddRowButton("Use", "Use " + profile.Name, true, 256, 64, y, delegate { use(profile); });
        AddRowButton("Edit", "Edit " + profile.Name, false, 324, 64, y, delegate { Edit(profile); });
        AddRowButton("Delete", "Delete " + profile.Name, false, 392, 84, y, delegate { Delete(profile); });
    }

    void AddRowButton(string text, string name, bool primary, int x, int w, int y, Action click)
    {
        PillButton button = new PillButton(text, primary, scale) { Bounds = new Rectangle(S(x), S(y + 4), S(w), S(34)), Backdrop = Ui.Card, AccessibleName = name };
        button.Font = Ui.Font(13, true, scale);
        button.Click += delegate { click(); };
        list.Controls.Add(button);
    }

    void NewProfile()
    {
        SwitchProfile profile = new SwitchProfile();
        using (ProfileForm form = new ProfileForm("New profile", scale, config, profile, hotkeys))
        {
            if (form.ShowDialog(this) != DialogResult.OK) return;
        }
        config.Profiles.Add(profile);
        Save();
        report("Saved: " + profile.Name + ". You can edit or delete it any time.");
    }

    void Edit(SwitchProfile profile)
    {
        using (ProfileForm form = new ProfileForm("Edit profile", scale, config, profile, hotkeys))
        {
            if (form.ShowDialog(this) != DialogResult.OK) return;
        }
        Save();
        report("Saved: " + profile.Name);
    }

    void Delete(SwitchProfile profile)
    {
        config.Profiles.Remove(profile);
        Save();
        report("Deleted: " + profile.Name);
    }

    void Save()
    {
        try { store.Save(config); }
        catch (Exception e)
        {
            if (!(e is IOException || e is UnauthorizedAccessException)) throw;
            report("Couldn't save that: " + e.Message);
        }
        changed();
        Reload();
    }
}

// Shown the first time the app runs. Every step says plainly that none of this is final.
sealed class SetupForm : Form
{
    const string StillChangeable = "Nothing here is final. You can change all of this any time in the Profiles tab.";
    readonly float scale;
    readonly ShortcutConfig config;
    readonly SwitchProfile firstProfile = new SwitchProfile();
    readonly ProfileFields profileFields;
    readonly Panel[] steps = new Panel[4];
    readonly Label heading = new Label(), stepCount = new Label(), message = new Label();
    readonly PillButton back, next;
    readonly KeyButton[] screenKeys = new KeyButton[Hotkey.ScreenNames.Length];
    readonly ToggleSwitch startup;
    int step;

    int S(int v) { return (int)Math.Round(v * scale); }

    public SetupForm(float scale, ShortcutConfig config, HotkeyManager hotkeys)
    {
        this.scale = scale;
        this.config = config;
        Text = "Screen Switch setup";
        Icon = MainForm.AppIcon();
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Ui.Card;
        ClientSize = new Size(S(540), S(440));

        heading.Font = Ui.Display(24, scale);
        heading.ForeColor = Ui.Text;
        heading.BackColor = Ui.Card;
        heading.Bounds = new Rectangle(S(32), S(20), S(390), S(38));
        heading.TextAlign = ContentAlignment.MiddleLeft;
        Controls.Add(heading);

        stepCount.Font = Ui.Font(13, false, scale);
        stepCount.ForeColor = Ui.SubText;
        stepCount.BackColor = Ui.Card;
        stepCount.Bounds = new Rectangle(S(420), S(24), S(88), S(30));
        stepCount.TextAlign = ContentAlignment.MiddleRight;
        Controls.Add(stepCount);

        for (int i = 0; i < steps.Length; i++)
        {
            steps[i] = new Panel { BackColor = Ui.Card, Bounds = new Rectangle(S(32), S(66), S(476), S(226)), Visible = false };
            Controls.Add(steps[i]);
        }

        AddText(steps[0], "Screen Switch changes your screens and sound in one place.\n\nThis quick setup does two things:\n\n  •  Picks keyboard shortcuts for switching screens\n  •  Optionally saves your first profile, which changes screens and sound together\n\nYou can skip any step, or skip setup completely.", 0, 210, 15, Ui.Text);

        AddText(steps[1], "Click a shortcut, then press the keys you want. Backspace clears it, Esc cancels.", 0, 46, 14, Ui.SubText);
        for (int i = 0; i < screenKeys.Length; i++)
        {
            int y = 52 + i * 42;
            AddText(steps[1], Hotkey.ScreenNames[i], y, 34, 14, Ui.Text, 100);
            int slot = i;
            screenKeys[i] = new KeyButton(scale, hotkeys, delegate(Keys keys) { return Hotkey.Holder(config, keys, slot); }, delegate(string m) { message.Text = m; })
            {
                Bounds = new Rectangle(S(110), S(y), S(366), S(34)),
                AccessibleName = Hotkey.ScreenNames[i] + " shortcut",
                Value = (Keys)config.ScreenKeys[i]
            };
            screenKeys[i].Changed += delegate { config.ScreenKeys[slot] = (int)screenKeys[slot].Value; };
            steps[1].Controls.Add(screenKeys[i]);
        }

        profileFields = new ProfileFields(scale, config, firstProfile, hotkeys, delegate(string m) { message.Text = m; }) { Bounds = new Rectangle(0, 0, S(476), S(212)) };
        steps[2].Controls.Add(profileFields);

        AddText(steps[3], "Shortcuts only work while Screen Switch is running. Closing the window keeps it running in the tray.", 0, 52, 15, Ui.Text);
        AddText(steps[3], "Open when Windows starts", 66, 34, 15, Ui.Text, 400);
        startup = new ToggleSwitch(scale) { Bounds = new Rectangle(S(422), S(67), S(54), S(32)), Backdrop = Ui.Card, Checked = Startup.IsEnabled(), AccessibleName = "Open when Windows starts" };
        steps[3].Controls.Add(startup);
        AddText(steps[3], "Your settings stay on this Windows account. Screen Switch doesn't send them anywhere.", 116, 52, 14, Ui.SubText);

        message.Font = Ui.Font(13, false, scale);
        message.ForeColor = Ui.SubText;
        message.BackColor = Ui.Card;
        message.UseMnemonic = false;
        message.Bounds = new Rectangle(S(32), S(292), S(476), S(26));
        message.TextAlign = ContentAlignment.MiddleLeft;
        Controls.Add(message);

        Panel notice = new Panel { BackColor = Ui.Neutral, Bounds = new Rectangle(S(32), S(322), S(476), S(40)) };
        notice.Controls.Add(new Label { Text = StillChangeable, Font = Ui.Font(13, true, scale), ForeColor = Ui.Text, BackColor = Ui.Neutral, TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(S(6), 0, S(464), S(40)) });
        Controls.Add(notice);

        PillButton skip = new PillButton("Skip setup", false, scale) { Bounds = new Rectangle(S(32), S(378), S(110), S(44)), Backdrop = Ui.Card, DialogResult = DialogResult.Cancel };
        skip.Font = Ui.Font(14, true, scale);
        Controls.Add(skip);
        back = new PillButton("Back", false, scale) { Bounds = new Rectangle(S(280), S(378), S(110), S(44)), Backdrop = Ui.Card };
        back.Font = Ui.Font(14, true, scale);
        back.Click += delegate { Show(step - 1); };
        Controls.Add(back);
        next = new PillButton("Next", true, scale) { Bounds = new Rectangle(S(398), S(378), S(110), S(44)), Backdrop = Ui.Card };
        next.Font = Ui.Font(14, true, scale);
        next.Click += delegate { Forward(); };
        Controls.Add(next);
        CancelButton = skip;

        Show(0);
    }

    void AddText(Panel parent, string text, int y, int height, float size, Color color, int width = 476)
    {
        parent.Controls.Add(new Label { Text = text, UseMnemonic = false, Font = Ui.Font(size, false, scale), ForeColor = color, BackColor = Ui.Card, TextAlign = height <= 34 ? ContentAlignment.MiddleLeft : ContentAlignment.TopLeft, Bounds = new Rectangle(0, S(y), S(width), S(height)) });
    }

    void Show(int number)
    {
        string[] headings = { "Welcome", "Keyboard shortcuts", "Your first profile (optional)", "Almost done" };
        step = number;
        for (int i = 0; i < steps.Length; i++) steps[i].Visible = i == number;
        heading.Text = headings[number];
        stepCount.Text = "Step " + (number + 1) + " of " + steps.Length;
        back.Visible = number > 0;
        next.Text = number == steps.Length - 1 ? "Finish" : "Next";
        message.Text = number == 2 ? "Leave the name empty to skip this." : "";
    }

    void Forward()
    {
        if (step == 2 && profileFields.NameText.Length > 0 && !profileFields.ChangesSomething)
        {
            message.Text = "Choose screens or a sound device, or clear the name to skip.";
            return;
        }
        if (step < steps.Length - 1) { Show(step + 1); return; }
        DialogResult = DialogResult.OK;
    }

    // Skipping keeps the current shortcuts and makes no profile.
    public void Finish(Action<string> report)
    {
        if (DialogResult != DialogResult.OK) return;
        if (profileFields.NameText.Length > 0)
        {
            profileFields.Store();
            config.Profiles.Add(firstProfile);
        }
        try { Startup.Set(startup.Checked); }
        catch (Exception e) { report("Couldn't change the startup setting: " + e.Message); }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Ui.StyleWindow(this);
    }
}
