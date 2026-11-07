using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

enum AudioFlow { Output = 0, Input = 1 }

sealed class AudioDevice
{
    public string Id, Name;
}

// Windows Core Audio (MMDevice) interfaces. Only the methods in use are named; order must match the vtable.
[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class MMDeviceEnumeratorCom { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    void EnumAudioEndpoints(int flow, int stateMask, out IMMDeviceCollection devices);
    void GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    void RegisterEndpointNotificationCallback(IMMNotificationClient client);
    void UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollection
{
    void GetCount(out int count);
    void Item(int index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    void Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    void OpenPropertyStore(int access, out IPropertyStore store);
    void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetState(out int state);
}

[StructLayout(LayoutKind.Sequential)]
struct PropertyKey
{
    public Guid Format;
    public int Id;
}

[StructLayout(LayoutKind.Sequential)]
struct PropVariant
{
    public ushort Type, Reserved1, Reserved2, Reserved3;
    public IntPtr Value, Value2;
}

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPropertyStore
{
    void GetCount(out int count);
    void GetAt(int index, out PropertyKey key);
    void GetValue(ref PropertyKey key, out PropVariant value);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolume
{
    void RegisterControlChangeNotify(IntPtr notify);
    void UnregisterControlChangeNotify(IntPtr notify);
    void GetChannelCount(out int count);
    void SetMasterVolumeLevel(float db, ref Guid context);
    void SetMasterVolumeLevelScalar(float level, ref Guid context);
    void GetMasterVolumeLevel(out float db);
    void GetMasterVolumeLevelScalar(out float level);
    void SetChannelVolumeLevel(int channel, float db, ref Guid context);
    void SetChannelVolumeLevelScalar(int channel, float level, ref Guid context);
    void GetChannelVolumeLevel(int channel, out float db);
    void GetChannelVolumeLevelScalar(int channel, out float level);
    void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
    void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

[ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioMeterInformation
{
    void GetPeakValue(out float peak);
}

[ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMNotificationClient
{
    void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, int state);
    void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
    void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
    void OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string id);
    void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
}

// IPolicyConfig is undocumented, but it's the only way to set the default device and it's what the Sound settings use.
[ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
class PolicyConfigCom { }

[ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPolicyConfig
{
    void GetMixFormat();
    void GetDeviceFormat();
    void ResetDeviceFormat();
    void SetDeviceFormat();
    void GetProcessingPeriod();
    void SetProcessingPeriod();
    void GetShareMode();
    void SetShareMode();
    void GetPropertyValue();
    void SetPropertyValue();
    void SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
}

static class Audio
{
    const int ActiveOnly = 1, RoleMultimedia = 1;
    static readonly int[] DefaultRoles = { 0, 1 };
    static readonly PropertyKey FriendlyName = new PropertyKey { Format = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 14 };

    [DllImport("ole32.dll")]
    static extern int PropVariantClear(ref PropVariant value);

    static IMMDeviceEnumerator Enumerator()
    {
        return (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
    }

    // Names come straight from Windows, so they match the Sound settings exactly.
    public static List<AudioDevice> List(AudioFlow flow)
    {
        IMMDeviceCollection devices;
        Enumerator().EnumAudioEndpoints((int)flow, ActiveOnly, out devices);
        int count;
        devices.GetCount(out count);

        List<AudioDevice> list = new List<AudioDevice>();
        for (int i = 0; i < count; i++)
        {
            IMMDevice device;
            devices.Item(i, out device);
            string id;
            device.GetId(out id);
            list.Add(new AudioDevice { Id = id, Name = NameOf(device) });
        }
        list.Sort(delegate(AudioDevice a, AudioDevice b) { return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); });
        return list;
    }

    static string NameOf(IMMDevice device)
    {
        IPropertyStore store;
        device.OpenPropertyStore(0, out store);
        PropertyKey key = FriendlyName;
        PropVariant value;
        store.GetValue(ref key, out value);
        try { return Marshal.PtrToStringUni(value.Value) ?? "Unknown device"; }
        finally { PropVariantClear(ref value); }
    }

    public static string DefaultId(AudioFlow flow)
    {
        try
        {
            IMMDevice device;
            Enumerator().GetDefaultAudioEndpoint((int)flow, RoleMultimedia, out device);
            string id;
            device.GetId(out id);
            return id;
        }
        catch (COMException) // No device of this kind.
        {
            return null;
        }
    }

    // Sets the default for games and music. The separate default for calls is left alone.
    public static void SetDefault(string id)
    {
        IPolicyConfig policy = (IPolicyConfig)new PolicyConfigCom();
        foreach (int role in DefaultRoles) policy.SetDefaultEndpoint(id, role);
    }

    public static AudioControl Open(string id)
    {
        IMMDevice device;
        Enumerator().GetDevice(id, out device);
        return new AudioControl(device);
    }

    public static IDisposable Watch(Action changed)
    {
        return new AudioWatcher(changed);
    }

    sealed class AudioWatcher : IMMNotificationClient, IDisposable
    {
        readonly IMMDeviceEnumerator enumerator = Enumerator();
        readonly Action changed;

        public AudioWatcher(Action changed)
        {
            this.changed = changed;
            enumerator.RegisterEndpointNotificationCallback(this);
        }

        public void Dispose() { enumerator.UnregisterEndpointNotificationCallback(this); }
        public void OnDeviceStateChanged(string id, int state) { changed(); }
        public void OnDeviceAdded(string id) { changed(); }
        public void OnDeviceRemoved(string id) { changed(); }
        public void OnDefaultDeviceChanged(int flow, int role, string id) { changed(); }
        public void OnPropertyValueChanged(string id, PropertyKey key) { } // Fires constantly and nothing here shows it.
    }
}

// Volume, mute, balance and the live level for one device.
sealed class AudioControl
{
    const int ClsctxAll = 23;
    readonly IAudioEndpointVolume volume;
    readonly IAudioMeterInformation meter;
    Guid context = Guid.Empty;

    public AudioControl(IMMDevice device)
    {
        Guid volumeId = typeof(IAudioEndpointVolume).GUID, meterId = typeof(IAudioMeterInformation).GUID;
        object instance;
        device.Activate(ref volumeId, ClsctxAll, IntPtr.Zero, out instance);
        volume = (IAudioEndpointVolume)instance;
        device.Activate(ref meterId, ClsctxAll, IntPtr.Zero, out instance);
        meter = (IAudioMeterInformation)instance;
    }

    public float Volume
    {
        get { float v; volume.GetMasterVolumeLevelScalar(out v); return v; }
        set { volume.SetMasterVolumeLevelScalar(value, ref context); }
    }

    public bool Muted
    {
        get { bool m; volume.GetMute(out m); return m; }
        set { volume.SetMute(value, ref context); }
    }

    public float Peak
    {
        get { float p; meter.GetPeakValue(out p); return p; }
    }

    public bool HasBalance
    {
        get { int c; volume.GetChannelCount(out c); return c == 2; }
    }

    // -1 is fully left, 0 is centered, 1 is fully right.
    public float Balance
    {
        get
        {
            float left, right;
            volume.GetChannelVolumeLevelScalar(0, out left);
            volume.GetChannelVolumeLevelScalar(1, out right);
            float loudest = Math.Max(left, right);
            if (loudest <= 0) return 0;
            return right < left ? right / loudest - 1 : 1 - left / loudest;
        }
        set
        {
            float left, right;
            volume.GetChannelVolumeLevelScalar(0, out left);
            volume.GetChannelVolumeLevelScalar(1, out right);
            float loudest = Math.Max(left, right);
            volume.SetChannelVolumeLevelScalar(0, loudest * (value > 0 ? 1 - value : 1), ref context);
            volume.SetChannelVolumeLevelScalar(1, loudest * (value < 0 ? 1 + value : 1), ref context);
        }
    }
}
