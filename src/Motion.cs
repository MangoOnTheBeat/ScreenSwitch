using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// One timer drives every animation, and it only runs while something is moving.
static class Motion
{
    const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

    [DllImport("user32.dll")]
    static extern bool SystemParametersInfo(uint action, uint param, out bool value, uint flags);

    // Follows "Animation effects" in Windows settings, so turning those off here means everything just jumps.
    public static readonly bool Enabled = ReadEnabled();

    // True when everything is drawn large, which leaves out the effects that get slow at that size.
    public static bool Light;

    static readonly Timer timer = new Timer { Interval = 15 };
    static readonly Stopwatch clock = new Stopwatch();
    static readonly List<Func<float, bool>> running = new List<Func<float, bool>>();

    static bool ReadEnabled()
    {
        bool on;
        return !SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, out on, 0) || on;
    }

    static Motion()
    {
        timer.Tick += delegate { Step(); };
    }

    // Each entry gets the seconds since the last frame and returns true when it's finished.
    public static void Add(Func<float, bool> runner)
    {
        running.Add(runner);
        if (!timer.Enabled)
        {
            clock.Restart();
            timer.Start();
        }
    }

    static void Step()
    {
        float seconds = (float)clock.Elapsed.TotalSeconds;
        clock.Restart();
        for (int i = running.Count - 1; i >= 0; i--)
            if (running[i](seconds)) running.RemoveAt(i);
        if (running.Count == 0) timer.Stop();
    }

    // Starts quick and settles gently.
    public static float EaseOut(float t)
    {
        float left = 1 - Math.Max(0, Math.Min(1, t));
        return 1 - left * left * left;
    }

    public static Tween Run(Control owner, int milliseconds, Action<float> frame)
    {
        Tween tween = new Tween(owner, milliseconds / 1000f, frame);
        if (Enabled) Add(tween.Advance);
        else frame(1);
        return tween;
    }
}

// Plays once from 0 to 1, then stops by itself.
sealed class Tween
{
    readonly Control owner;
    readonly float duration;
    readonly Action<float> frame;
    float elapsed;
    bool stopped;

    public Tween(Control owner, float duration, Action<float> frame)
    {
        this.owner = owner;
        this.duration = duration;
        this.frame = frame;
    }

    public void Stop() { stopped = true; }

    public bool Advance(float seconds)
    {
        if (stopped || owner.IsDisposed) return true;
        elapsed += seconds;
        float t = duration > 0 ? elapsed / duration : 1;
        frame(Motion.EaseOut(t));
        return t >= 1;
    }
}

// A number that glides towards wherever it was last told to go, so it can change direction midway.
sealed class Smooth
{
    readonly Control owner;
    readonly float settle;
    readonly Action changed;
    float target;
    bool active;

    public Smooth(Control owner, float settle, Action changed = null)
    {
        this.owner = owner;
        this.settle = settle;
        this.changed = changed ?? owner.Invalidate;
    }

    public float Value { get; private set; }

    public float Target
    {
        get { return target; }
        set
        {
            target = value;
            if (!Motion.Enabled) { Snap(value); return; }
            if (active || value == Value) return;
            active = true;
            Motion.Add(Advance);
        }
    }

    public void Snap(float value)
    {
        target = value;
        Value = value;
        changed();
    }

    bool Advance(float seconds)
    {
        if (owner.IsDisposed) { active = false; return true; }
        Value = Next(Value, target, seconds, settle);
        changed();
        active = Value != target;
        return !active;
    }

    // Covers the same share of the remaining distance each moment, whatever the frame rate, then lands exactly.
    public static float Next(float value, float target, float seconds, float settle)
    {
        float next = value + (target - value) * (1 - (float)Math.Exp(-seconds / settle));
        return Math.Abs(target - next) < 0.002f * Math.Max(1, Math.Abs(target)) ? target : next;
    }
}
