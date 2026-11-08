using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

// One part of the tab switcher at the top of the window. The track paints a pill that slides between them.
sealed class SegmentButton : SoftButton
{
    readonly float scale;

    public SegmentButton(string text, float scale)
    {
        this.scale = scale;
        Text = text;
        Font = Ui.Font(13, true, scale);
        Backdrop = Ui.Neutral;
    }

    public bool Selected
    {
        set
        {
            SegmentTrack track = Parent as SegmentTrack;
            if (value && track != null) track.SlideTo(Left, Width);
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Backdrop);
        Ui.Prepare(g);

        // How much of the pill is over this button, so the text brightens as it arrives.
        SegmentTrack track = Parent as SegmentTrack;
        float pillX = track != null ? track.PillX - Left : 0, covered = 0;
        if (track != null)
        {
            covered = Math.Max(0, 1 - Math.Abs(pillX) / Width);
            using (GraphicsPath pill = Logo.Rounded(pillX + 0.5f, 0.5f, track.PillWidth - 2, Height - 2, 7 * scale))
            using (SolidBrush brush = new SolidBrush(Ui.Accent))
                g.FillPath(brush, pill);
        }
        if (ShowRing)
            using (GraphicsPath ring = Logo.Rounded(1, 1, Width - 3, Height - 3, 6 * scale))
            using (Pen pen = new Pen(Ui.Honey, 2 * scale))
                g.DrawPath(pen, ring);
        Color rest = Ui.Mix(Ui.SubText, Ui.Text, Hover.Value);
        using (StringFormat center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
        using (SolidBrush text = new SolidBrush(Ui.Mix(rest, Ui.OnAccent, covered)))
            g.DrawString(Text, Font, text, new RectangleF(0, 0, Width, Height), center);
    }
}

// The rounded strip behind the tab buttons.
sealed class SegmentTrack : Panel
{
    readonly float scale;
    readonly Smooth pill;
    bool placed;

    public SegmentTrack(float scale)
    {
        this.scale = scale;
        BackColor = Ui.Neutral;
        DoubleBuffered = true;
        pill = new Smooth(this, 0.055f, delegate { Invalidate(true); });
    }

    public float PillX { get { return pill.Value; } }
    public int PillWidth { get; private set; }

    public void SlideTo(int x, int width)
    {
        PillWidth = width;
        if (placed) pill.Target = x;
        else pill.Snap(x);
        placed = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Ui.Background);
        Ui.Prepare(g);
        using (GraphicsPath track = Logo.Rounded(0, 0, Width - 1, Height - 1, 9 * scale))
        using (SolidBrush brush = new SolidBrush(Ui.Neutral))
            g.FillPath(brush, track);
    }
}

// A white rounded card that holds other controls.
class Card : Panel
{
    readonly float scale;

    public Card(float scale)
    {
        this.scale = scale;
        DoubleBuffered = true;
        BackColor = Ui.Card;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Ui.Background);
        Ui.Prepare(g);
        using (GraphicsPath card = Logo.Rounded(0.5f, 0.5f, Width - 2, Height - 2, 14 * scale))
        using (SolidBrush brush = new SolidBrush(Ui.Card))
        using (Pen hairline = new Pen(Color.FromArgb(30, Ui.Text), scale))
        {
            g.FillPath(brush, card);
            g.DrawPath(hairline, card);
        }
    }
}

// A thin Apple-style slider with a round white knob. Only the person using it raises UserChanged.
sealed class ThinSlider : Control
{
    readonly float scale;
    readonly bool vertical, fromCenter;
    float value;

    public event EventHandler UserChanged;
    // Raised once the person lets go, for settings that shouldn't change on every small movement.
    public event EventHandler Committed;
    public bool Dragging { get; private set; }

    public ThinSlider(float scale, bool vertical = false, bool fromCenter = false)
    {
        this.scale = scale;
        this.vertical = vertical;
        this.fromCenter = fromCenter;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        BackColor = Ui.Card;
    }

    public float Value
    {
        get { return value; }
        set
        {
            float clamped = Math.Max(0, Math.Min(1, value));
            if (clamped == this.value) return;
            this.value = clamped;
            Invalidate();
        }
    }

    float Knob { get { return 18 * scale; } }

    void SetFromUser(float v)
    {
        Value = v;
        if (UserChanged != null) UserChanged(this, EventArgs.Empty);
    }

    void SetFromMouse(MouseEventArgs e)
    {
        float length = (vertical ? Height : Width) - Knob;
        SetFromUser(vertical ? 1 - (e.Y - Knob / 2) / length : (e.X - Knob / 2) / length);
    }

    protected override void OnMouseDown(MouseEventArgs e) { Focus(); Dragging = true; SetFromMouse(e); base.OnMouseDown(e); }
    protected override void OnMouseMove(MouseEventArgs e) { if (Dragging) SetFromMouse(e); base.OnMouseMove(e); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        bool wasDragging = Dragging;
        Dragging = false;
        base.OnMouseUp(e);
        if (wasDragging && Committed != null) Committed(this, EventArgs.Empty);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        bool arrow = e.KeyCode == Keys.Left || e.KeyCode == Keys.Right || e.KeyCode == Keys.Up || e.KeyCode == Keys.Down;
        if (arrow && Committed != null) Committed(this, EventArgs.Empty);
    }

    protected override bool IsInputKey(Keys key)
    {
        return key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down || base.IsInputKey(key);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Down) SetFromUser(value - 0.02f);
        if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Up) SetFromUser(value + 0.02f);
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(BackColor);
        Ui.Prepare(g);

        float thick = 4 * scale, knob = Knob, length = (vertical ? Height : Width) - knob;
        float at = knob / 2 + length * (vertical ? 1 - value : value);
        float anchor = fromCenter ? knob / 2 + length / 2 : (vertical ? knob / 2 + length : knob / 2);
        float cross = ((vertical ? Width : Height) - thick) / 2;

        using (SolidBrush track = new SolidBrush(Ui.Neutral))
        using (SolidBrush fill = new SolidBrush(Enabled ? Ui.Accent : Ui.SubText))
        {
            float start = Math.Min(anchor, at), span = Math.Abs(anchor - at);
            if (vertical)
            {
                g.FillRectangle(track, cross, knob / 2, thick, length);
                g.FillRectangle(fill, cross, start, thick, span);
            }
            else
            {
                g.FillRectangle(track, knob / 2, cross, length, thick);
                g.FillRectangle(fill, start, cross, span, thick);
            }
        }

        float x = vertical ? (Width - knob) / 2 : at - knob / 2, y = vertical ? at - knob / 2 : (Height - knob) / 2;
        if (Focused)
            using (Pen ring = new Pen(Color.FromArgb(120, Ui.Accent), 3 * scale)) g.DrawEllipse(ring, x, y, knob - 1, knob - 1);
        g.FillEllipse(Brushes.White, x, y, knob - 1, knob - 1);
        using (Pen edge = new Pen(Color.FromArgb(50, 0, 0, 0), scale)) g.DrawEllipse(edge, x, y, knob - 1, knob - 1);
    }
}

// Live sound level. Rises and falls smoothly between readings.
sealed class LevelBar : Control
{
    readonly Smooth level;

    public LevelBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Ui.Card;
        level = new Smooth(this, 0.06f);
    }

    public float Level
    {
        set { if (Math.Abs(value - level.Target) > 0.005f) level.Target = value; }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(BackColor);
        Ui.Prepare(g);
        float radius = Height / 2f, filled = Width * Math.Min(level.Value, 1);
        using (GraphicsPath track = Logo.Rounded(0, 0, Width - 1, Height - 1, radius))
        using (SolidBrush brush = new SolidBrush(Ui.Neutral))
            g.FillPath(brush, track);
        if (filled < Height) return;
        using (GraphicsPath bar = Logo.Rounded(0, 0, filled - 1, Height - 1, radius))
        using (LinearGradientBrush brush = new LinearGradientBrush(new RectangleF(0, 0, Width, Height), Ui.Honey, Ui.Accent, 0f))
            g.FillPath(brush, bar);
    }
}

// Colors for the drop-down menu of devices.
sealed class MenuColors : ProfessionalColorTable
{
    public override Color MenuItemSelected { get { return Ui.Neutral; } }
    public override Color MenuItemBorder { get { return Ui.Neutral; } }
    public override Color MenuBorder { get { return Ui.Neutral; } }
    public override Color ToolStripDropDownBackground { get { return Ui.Card; } }
    public override Color ImageMarginGradientBegin { get { return Ui.Card; } }
    public override Color ImageMarginGradientMiddle { get { return Ui.Card; } }
    public override Color ImageMarginGradientEnd { get { return Ui.Card; } }
    public override Color CheckBackground { get { return Ui.Card; } }
    public override Color CheckSelectedBackground { get { return Ui.Neutral; } }
    public override Color CheckPressedBackground { get { return Ui.Neutral; } }
}

// A rounded field showing the current device. Clicking opens the list of devices.
class DeviceButton : SoftButton
{
    readonly float scale;

    public DeviceButton(float scale)
    {
        this.scale = scale;
        Font = Ui.Font(14, false, scale);
        Backdrop = Ui.Card;
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
            if (ShowRing)
                using (Pen ring = new Pen(Ui.Honey, 2 * scale)) g.DrawPath(ring, field);
        }

        float arrow = 30 * scale;
        using (StringFormat left = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
        using (SolidBrush text = new SolidBrush(Ui.Text))
            g.DrawString(Text, Font, text, new RectangleF(12 * scale, 0, Width - 12 * scale - arrow, Height), left);

        float cx = Width - 17 * scale, cy = Height / 2f, size = 4 * scale;
        using (Pen chevron = new Pen(Ui.SubText, 1.8f * scale))
            g.DrawLines(chevron, new[] { new PointF(cx - size, cy - size / 2), new PointF(cx, cy + size / 2), new PointF(cx + size, cy - size / 2) });
    }
}

// Device picker, volume, mute and levels for either the output or the input side.
sealed class EndpointCard : Card
{
    readonly AudioFlow flow;
    readonly float scale;
    readonly Action<string> report;
    readonly DeviceButton picker;
    readonly ContextMenuStrip menu = new ContextMenuStrip();
    readonly ThinSlider volume, balance;
    readonly Label percent = new Label(), balanceValue = new Label();
    readonly PillButton mute;
    readonly LevelBar meter = new LevelBar();
    List<AudioDevice> devices = new List<AudioDevice>();
    AudioControl control;
    string currentId;

    public EndpointCard(AudioFlow flow, string title, bool withBalance, float scale, Action<string> report) : base(scale)
    {
        this.flow = flow;
        this.scale = scale;
        this.report = report;

        AddLabel(title, Ui.Font(15, true, scale), Ui.Text, 16, 12, 66, 34);
        picker = new DeviceButton(scale) { Bounds = new Rectangle(S(84), S(12), S(424), S(34)) };
        picker.AccessibleName = title + " device";
        picker.Click += delegate { OpenMenu(); };
        Controls.Add(picker);

        menu.Renderer = new ToolStripProfessionalRenderer(new MenuColors()) { RoundedEdges = false };
        menu.Font = Ui.Font(14, false, scale);
        menu.BackColor = Ui.Card;
        menu.ForeColor = Ui.Text;

        AddLabel("Volume", Ui.Font(13, false, scale), Ui.SubText, 16, 52, 66, 28);
        volume = new ThinSlider(scale) { Bounds = new Rectangle(S(80), S(52), S(306), S(28)), AccessibleName = title + " volume" };
        volume.UserChanged += delegate { Try(delegate { control.Volume = volume.Value; }); ShowPercent(); };
        Controls.Add(volume);
        Style(percent, 388, 52, 44);

        mute = new PillButton("Mute", false, scale) { Bounds = new Rectangle(S(436), S(50), S(72), S(32)), Backdrop = Ui.Card };
        mute.Font = Ui.Font(13, true, scale);
        mute.Click += delegate { Try(delegate { control.Muted = !control.Muted; }); };
        Controls.Add(mute);

        meter.Bounds = new Rectangle(S(89), S(82), S(288), S(4));
        Controls.Add(meter);

        if (withBalance)
        {
            AddLabel("Balance", Ui.Font(13, false, scale), Ui.SubText, 16, 92, 66, 28);
            balance = new ThinSlider(scale, false, true) { Bounds = new Rectangle(S(80), S(92), S(306), S(28)), AccessibleName = "Left and right balance" };
            balance.UserChanged += delegate { Try(delegate { control.Balance = balance.Value * 2 - 1; }); };
            Controls.Add(balance);
            Style(balanceValue, 388, 92, 66);
        }
    }

    int S(int v) { return (int)Math.Round(v * scale); }

    void AddLabel(string text, Font font, Color color, int x, int y, int w, int h)
    {
        Controls.Add(new Label
        {
            Text = text,
            Font = font,
            ForeColor = color,
            BackColor = Ui.Card,
            TextAlign = ContentAlignment.MiddleLeft,
            Bounds = new Rectangle(S(x), S(y), S(w), S(h))
        });
    }

    void Style(Label label, int x, int y, int w)
    {
        label.Font = Ui.Font(13, false, scale);
        label.ForeColor = Ui.SubText;
        label.BackColor = Ui.Card;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.Bounds = new Rectangle(S(x), S(y), S(w), S(28));
        Controls.Add(label);
    }

    void ShowPercent()
    {
        percent.Text = (int)Math.Round(volume.Value * 100) + "%";
    }

    // A device can vanish at any moment, which shows up as an error here. The next refresh sorts it out.
    void Try(Action action)
    {
        if (control == null) return;
        try { action(); }
        catch (Exception e)
        {
            if (!(e is System.Runtime.InteropServices.COMException || e is InvalidCastException)) throw;
            control = null;
        }
    }

    void OpenMenu()
    {
        menu.Items.Clear();
        foreach (AudioDevice device in devices)
        {
            AudioDevice picked = device;
            ToolStripMenuItem item = new ToolStripMenuItem(device.Name.Replace("&", "&&")) { Checked = device.Id == currentId, ForeColor = Ui.Text };
            item.Click += delegate { Choose(picked); };
            menu.Items.Add(item);
        }
        menu.MinimumSize = new Size(picker.Width, 0);
        menu.Show(picker, 0, picker.Height + S(2));
    }

    void Choose(AudioDevice device)
    {
        try
        {
            Audio.SetDefault(device.Id);
            report("Now using: " + device.Name);
        }
        catch (Exception e)
        {
            report("Couldn't switch to " + device.Name + ": " + e.Message);
        }
        RefreshDevices();
    }

    public List<AudioDevice> RefreshDevices()
    {
        devices = Audio.List(flow);
        string id = Audio.DefaultId(flow);
        if (id != currentId || control == null)
        {
            currentId = id;
            control = null;
            if (id != null)
            {
                try { control = Audio.Open(id); }
                catch (System.Runtime.InteropServices.COMException) { }
            }
        }

        AudioDevice current = devices.Find(delegate(AudioDevice d) { return d.Id == id; });
        picker.Text = current != null ? current.Name : "No device found";
        picker.Enabled = devices.Count > 0;
        picker.Invalidate();
        Tick();
        return devices;
    }


    public void Tick()
    {
        volume.Enabled = mute.Enabled = control != null;
        Try(delegate
        {
            if (!volume.Dragging) { volume.Value = control.Volume; ShowPercent(); }
            bool muted = control.Muted;
            if (mute.Text != (muted ? "Unmute" : "Mute")) { mute.Text = muted ? "Unmute" : "Mute"; mute.Primary = muted; }
            meter.Level = muted ? 0 : control.Peak;
            if (balance != null)
            {
                balance.Enabled = control.HasBalance;
                if (!balance.Dragging) balance.Value = (control.Balance + 1) / 2;
                float b = balance.Value * 2 - 1;
                balanceValue.Text = Math.Abs(b) < 0.02f ? "Center" : (b < 0 ? "Left " : "Right ") + (int)Math.Round(Math.Abs(b) * 100);
            }
        });
        if (control == null) meter.Level = 0;
    }
}

sealed class AudioPage : Panel
{
    readonly EndpointCard output, input;
    readonly System.Windows.Forms.Timer levels = new System.Windows.Forms.Timer { Interval = 80 };
    readonly HashSet<string> known = new HashSet<string>();
    readonly Action<string> newDevice;
    readonly PillButton more;
    bool seeded;

    public AudioPage(float scale, Action<string> report, Action<string> newDevice)
    {
        this.newDevice = newDevice;
        Func<int, int> S = delegate(int v) { return (int)Math.Round(v * scale); };

        output = new EndpointCard(AudioFlow.Output, "Output", true, scale, report) { Bounds = new Rectangle(S(20), S(6), S(524), S(130)) };
        input = new EndpointCard(AudioFlow.Input, "Input", false, scale, report) { Bounds = new Rectangle(S(20), S(146), S(524), S(98)) };
        Controls.Add(output);
        Controls.Add(input);

        more = new PillButton("More sound settings", false, scale) { Bounds = new Rectangle(S(182), S(256), S(200), S(38)) };
        more.Font = Ui.Font(13, true, scale);
        more.Click += delegate { Process.Start("ms-settings:sound"); };
        Controls.Add(more);

        levels.Tick += delegate { output.Tick(); input.Tick(); };
    }

    // Keys for muting from anywhere. Added once the shortcut settings have loaded.
    public void AddShortcuts(float scale, ShortcutConfig config, ConfigStore store, HotkeyManager hotkeys, Action<string> report, Action changed)
    {
        Func<int, int> S = delegate(int v) { return (int)Math.Round(v * scale); };
        string[] hints = { "Press to mute or unmute", "Hold to talk, let go to mute", "Press to mute or unmute" };

        Card card = new Card(scale) { Bounds = new Rectangle(S(20), S(254), S(524), S(140)) };
        Controls.Add(card);
        card.Controls.Add(new Label { Text = "Audio shortcuts", Font = Ui.Font(15, true, scale), ForeColor = Ui.Text, BackColor = Ui.Card, TextAlign = ContentAlignment.MiddleLeft, Bounds = new Rectangle(S(16), S(8), S(300), S(26)) });

        for (int i = 0; i < AudioShortcuts.Slots.Length; i++)
        {
            string slot = AudioShortcuts.Slots[i];
            int y = 38 + i * 32;
            card.Controls.Add(new Label { Text = AudioShortcuts.Names[i], Font = Ui.Font(13, false, scale), ForeColor = Ui.Text, BackColor = Ui.Card, TextAlign = ContentAlignment.MiddleLeft, Bounds = new Rectangle(S(16), S(y), S(130), S(30)) });
            card.Controls.Add(new Label { Text = hints[i], Font = Ui.Font(12, false, scale), ForeColor = Ui.SubText, BackColor = Ui.Card, TextAlign = ContentAlignment.MiddleLeft, Bounds = new Rectangle(S(146), S(y), S(186), S(30)) });
            KeyButton key = new KeyButton(scale, hotkeys, delegate(Keys keys) { return Hotkey.Holder(config, keys, slot); }, report)
            {
                Bounds = new Rectangle(S(336), S(y), S(172), S(30)),
                AccessibleName = AudioShortcuts.Names[i] + " shortcut",
                Value = (Keys)AudioShortcuts.Get(config, slot)
            };
            key.Changed += delegate
            {
                AudioShortcuts.Set(config, slot, (int)key.Value);
                try { store.Save(config); }
                catch (Exception e)
                {
                    if (!(e is IOException || e is UnauthorizedAccessException)) throw;
                    report("Couldn't save that: " + e.Message);
                }
                changed();
            };
            card.Controls.Add(key);
        }
        more.Top = S(400);
    }

    // Only reads levels while they can be seen.
    protected override void OnVisibleChanged(EventArgs e)
    {
        levels.Enabled = Visible;
        base.OnVisibleChanged(e);
    }

    public void RefreshDevices()
    {
        List<AudioDevice> all = output.RefreshDevices();
        all.AddRange(input.RefreshDevices());
        foreach (AudioDevice device in all)
            if (known.Add(device.Id) && seeded) newDevice(device.Name);
        seeded = true;
    }
}
