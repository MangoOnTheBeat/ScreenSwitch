using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// One paired device, exactly as Windows reports it right now. Nothing here is saved.
sealed class BluetoothDevice
{
    public string Name, Kind;
    public ulong Address;
    public bool Connected, LowEnergy;
    // Headphones and speakers can be connected from the PC. Most other devices connect from their own end.
    public bool CanConnect;
}

// Windows' audio endpoint plumbing, used to ask the Bluetooth audio driver to connect or disconnect.
[ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IDeviceTopology
{
    void GetConnectorCount(out int count);
    void GetConnector(int index, out IConnector connector);
    void GetSubunitCount();
    void GetSubunit();
    void GetPartById();
    void GetDeviceId([MarshalAs(UnmanagedType.LPWStr)] out string id);
}

[ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IConnector
{
    void GetType_();
    void GetDataFlow();
    void ConnectTo();
    void Disconnect();
    void IsConnected();
    void GetConnectedTo(out IConnector other);
}

[ComImport, Guid("AE2DE0E4-5BCA-4F2D-AA46-5D13F8FDB3A9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPart
{
    void GetName(); void GetLocalId(); void GetGlobalId(); void GetPartType(); void GetSubType();
    void GetControlInterfaceCount(); void GetControlInterface(); void EnumPartsIncoming(); void EnumPartsOutgoing();
    void GetTopologyObject(out IDeviceTopology topology);
}

[StructLayout(LayoutKind.Sequential)]
struct KsPropertyRequest
{
    public Guid Set;
    public int Id, Flags;
}

[ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IKsControl
{
    [PreserveSig]
    int KsProperty(ref KsPropertyRequest property, int propertyLength, IntPtr data, int dataLength, out int returned);
}

static class Bluetooth
{
    [StructLayout(LayoutKind.Sequential)]
    struct FindRadioParams { public int Size; }

    [StructLayout(LayoutKind.Sequential)]
    struct SearchParams
    {
        public int Size, ReturnAuthenticated, ReturnRemembered, ReturnUnknown, ReturnConnected, IssueInquiry;
        public byte TimeoutMultiplier;
        public IntPtr Radio;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SystemTime { public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DeviceInfo
    {
        public int Size;
        public ulong Address;
        public uint ClassOfDevice;
        public int Connected, Remembered, Authenticated;
        public SystemTime LastSeen, LastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
        public string Name;
    }

    [DllImport("BluetoothApis.dll", SetLastError = true)]
    static extern IntPtr BluetoothFindFirstRadio(ref FindRadioParams search, out IntPtr radio);
    [DllImport("BluetoothApis.dll")]
    static extern bool BluetoothFindRadioClose(IntPtr find);
    [DllImport("BluetoothApis.dll", SetLastError = true)]
    static extern IntPtr BluetoothFindFirstDevice(ref SearchParams search, ref DeviceInfo info);
    [DllImport("BluetoothApis.dll", SetLastError = true)]
    static extern bool BluetoothFindNextDevice(IntPtr find, ref DeviceInfo info);
    [DllImport("BluetoothApis.dll")]
    static extern bool BluetoothFindDeviceClose(IntPtr find);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    const int ClsctxAll = 23, KsPropertyGet = 1, Reconnect = 0, Disconnect = 1, AllStates = 0xF, AudioVideoClass = 4;
    static readonly Guid BluetoothAudio = new Guid("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
    // The name of the device an audio endpoint belongs to, such as the headphones themselves.
    static readonly PropertyKey InterfaceName = new PropertyKey { Format = new Guid("026E516E-B814-414B-83CD-856D6FEF4822"), Id = 2 };

    [DllImport("ole32.dll")]
    static extern int PropVariantClear(ref PropVariant value);

    // An adapter that's switched off counts as missing, since nothing can connect through it.
    public static bool HasRadio()
    {
        FindRadioParams search = new FindRadioParams { Size = Marshal.SizeOf(typeof(FindRadioParams)) };
        IntPtr radio;
        IntPtr find = BluetoothFindFirstRadio(ref search, out radio);
        if (find == IntPtr.Zero) return false;
        CloseHandle(radio);
        BluetoothFindRadioClose(find);
        return true;
    }

    static DeviceInfo NewInfo()
    {
        return new DeviceInfo { Size = Marshal.SizeOf(typeof(DeviceInfo)) };
    }

    public static string KindOf(uint classOfDevice)
    {
        switch ((classOfDevice >> 8) & 0x1F)
        {
            case 1: return "Computer";
            case 2: return "Phone";
            case 4: return "Headphones or speaker";
            case 5: return "Controller, keyboard or mouse";
            case 7: return "Wearable";
            case 8: return "Toy";
            default: return "Device";
        }
    }

    // Paired devices. Classic ones come from the Bluetooth API, low-energy ones only show up as Windows devices.
    public static List<BluetoothDevice> Paired(bool includeLowEnergy = true)
    {
        List<BluetoothDevice> devices = new List<BluetoothDevice>();
        HashSet<string> audio = AudioDeviceNames();

        SearchParams search = new SearchParams { Size = Marshal.SizeOf(typeof(SearchParams)), ReturnAuthenticated = 1, ReturnRemembered = 1, ReturnConnected = 1 };
        DeviceInfo info = NewInfo();
        IntPtr find = BluetoothFindFirstDevice(ref search, ref info);
        if (find != IntPtr.Zero)
        {
            try
            {
                do
                {
                    if (info.Remembered != 0 || info.Authenticated != 0)
                    {
                        string name = string.IsNullOrEmpty(info.Name) ? "Unnamed device" : info.Name;
                        devices.Add(new BluetoothDevice
                        {
                            Name = name,
                            Address = info.Address,
                            Kind = KindOf(info.ClassOfDevice),
                            Connected = info.Connected != 0,
                            // Controllers with a headset jack have sound too, but only headphones and speakers can be connected from here.
                            CanConnect = audio.Contains(name) && ((info.ClassOfDevice >> 8) & 0x1F) == AudioVideoClass
                        });
                    }
                    info = NewInfo();
                } while (BluetoothFindNextDevice(find, ref info));
            }
            finally { BluetoothFindDeviceClose(find); }
        }

        if (includeLowEnergy) AddLowEnergy(devices);
        devices.Sort(delegate(BluetoothDevice a, BluetoothDevice b)
        {
            if (a.Connected != b.Connected) return a.Connected ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        });
        return devices;
    }

    static void AddLowEnergy(List<BluetoothDevice> devices)
    {
        try
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'BTHLE\\\\DEV[_]%'"))
            using (ManagementObjectCollection results = searcher.Get())
            {
                foreach (ManagementBaseObject item in results)
                {
                    string id = item["PNPDeviceID"] as string, name = item["Name"] as string;
                    ulong address;
                    if (id == null || !TryAddress(id, out address)) continue;
                    // A device with both kinds of Bluetooth is already listed from the classic side.
                    if (devices.Exists(delegate(BluetoothDevice d) { return d.Address == address; })) continue;
                    devices.Add(new BluetoothDevice { Name = string.IsNullOrEmpty(name) ? "Unnamed device" : name, Address = address, Kind = "Low energy", LowEnergy = true });
                }
            }
        }
        catch (ManagementException) { } // Windows' device list is busy or unavailable. The classic devices still show.
        catch (COMException) { }
    }

    // Device ids look like BTHLE\DEV_0123456789AB\..., where the twelve digits are the address.
    public static bool TryAddress(string id, out ulong address)
    {
        address = 0;
        int at = id.IndexOf("DEV_", StringComparison.OrdinalIgnoreCase);
        if (at < 0 || id.Length < at + 16) return false;
        return ulong.TryParse(id.Substring(at + 4, 12), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
    }

    // Every sound endpoint, connected or not, keyed by the name of the device it belongs to.
    static List<KeyValuePair<string, IMMDevice>> AudioEndpoints()
    {
        List<KeyValuePair<string, IMMDevice>> endpoints = new List<KeyValuePair<string, IMMDevice>>();
        try
        {
            IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            foreach (AudioFlow flow in new[] { AudioFlow.Output, AudioFlow.Input })
            {
                IMMDeviceCollection collection;
                enumerator.EnumAudioEndpoints((int)flow, AllStates, out collection);
                int count;
                collection.GetCount(out count);
                for (int i = 0; i < count; i++)
                {
                    IMMDevice device;
                    collection.Item(i, out device);
                    string name = ReadString(device, InterfaceName);
                    if (name != null) endpoints.Add(new KeyValuePair<string, IMMDevice>(name, device));
                }
            }
        }
        catch (COMException) { }
        return endpoints;
    }

    static HashSet<string> AudioDeviceNames()
    {
        HashSet<string> names = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (KeyValuePair<string, IMMDevice> endpoint in AudioEndpoints()) names.Add(endpoint.Key);
        return names;
    }

    static string ReadString(IMMDevice device, PropertyKey key)
    {
        try
        {
            IPropertyStore store;
            device.OpenPropertyStore(0, out store);
            PropVariant value;
            store.GetValue(ref key, out value);
            try { return Marshal.PtrToStringUni(value.Value); }
            finally { PropVariantClear(ref value); }
        }
        catch (COMException) { return null; }
    }

    // Asks the Bluetooth audio driver to connect or disconnect, the same request Windows' own Connect button sends.
    // Returns null if the request went through. Connecting still takes a few seconds after that.
    public static string SetConnected(BluetoothDevice device, bool connect)
    {
        string lastError = "No sound connection was found for it.";
        foreach (KeyValuePair<string, IMMDevice> endpoint in AudioEndpoints())
        {
            if (!string.Equals(endpoint.Key, device.Name, StringComparison.CurrentCultureIgnoreCase)) continue;
            try
            {
                Send(endpoint.Value, connect ? Reconnect : Disconnect);
                return null;
            }
            catch (Exception e)
            {
                if (!(e is COMException || e is InvalidCastException)) throw;
                lastError = e.Message;
            }
        }
        return lastError;
    }

    // The sound endpoint connects to the Bluetooth audio driver's own device, and that device takes the request.
    static void Send(IMMDevice endpoint, int request)
    {
        Guid topologyId = typeof(IDeviceTopology).GUID;
        object instance;
        endpoint.Activate(ref topologyId, ClsctxAll, IntPtr.Zero, out instance);
        IConnector connector, other;
        ((IDeviceTopology)instance).GetConnector(0, out connector);
        connector.GetConnectedTo(out other);

        IDeviceTopology driverTopology;
        ((IPart)other).GetTopologyObject(out driverTopology);
        string driverId;
        driverTopology.GetDeviceId(out driverId);
        IMMDevice driver;
        ((IMMDeviceEnumerator)new MMDeviceEnumeratorCom()).GetDevice(driverId, out driver);

        Guid controlId = typeof(IKsControl).GUID;
        driver.Activate(ref controlId, ClsctxAll, IntPtr.Zero, out instance);
        KsPropertyRequest property = new KsPropertyRequest { Set = BluetoothAudio, Id = request, Flags = KsPropertyGet };
        int returned;
        int result = ((IKsControl)instance).KsProperty(ref property, Marshal.SizeOf(typeof(KsPropertyRequest)), IntPtr.Zero, 0, out returned);
        if (result < 0) Marshal.ThrowExceptionForHR(result);
    }
}

sealed class BluetoothPage : Panel
{
    readonly float scale;
    readonly Action<string> report;
    readonly Panel list = new Panel();
    readonly Label summary = new Label();
    readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer { Interval = 4000 };
    string shown;
    bool loading;
    int generation;

    int S(int v) { return (int)Math.Round(v * scale); }

    public BluetoothPage(float scale, Action<string> report)
    {
        this.scale = scale;
        this.report = report;

        Card card = new Card(scale) { Bounds = new Rectangle(S(20), S(6), S(524), S(430)) };
        Controls.Add(card);
        card.Controls.Add(MakeLabel("Bluetooth devices", Ui.Font(15, true, scale), Ui.Text, 16, 10, 260, 24));
        summary.Font = Ui.Font(13, false, scale);
        summary.ForeColor = Ui.SubText;
        summary.BackColor = Ui.Card;
        summary.UseMnemonic = false;
        summary.TextAlign = ContentAlignment.MiddleLeft;
        summary.Bounds = new Rectangle(S(16), S(34), S(250), S(20));
        card.Controls.Add(summary);

        PillButton refresh = new PillButton("Refresh", false, scale) { Bounds = new Rectangle(S(268), S(14), S(96), S(36)), Backdrop = Ui.Card };
        refresh.Font = Ui.Font(13, true, scale);
        refresh.Click += delegate { Reload(true); };
        card.Controls.Add(refresh);
        PillButton add = new PillButton("Add a device", true, scale) { Bounds = new Rectangle(S(370), S(14), S(138), S(36)), Backdrop = Ui.Card };
        add.Font = Ui.Font(13, true, scale);
        add.Click += delegate { OpenSettings(); };
        card.Controls.Add(add);

        list.BackColor = Ui.Card;
        list.AutoScroll = true;
        list.Bounds = new Rectangle(S(8), S(62), S(508), S(360));
        card.Controls.Add(list);

        poll.Tick += delegate { Reload(false); };
    }

    Label MakeLabel(string text, Font font, Color color, int x, int y, int w, int h)
    {
        return new Label { Text = text, UseMnemonic = false, AutoEllipsis = true, BackColor = Ui.Card, Font = font, ForeColor = color, TextAlign = ContentAlignment.MiddleLeft, Bounds = new Rectangle(S(x), S(y), S(w), S(h)) };
    }

    void OpenSettings()
    {
        try { Process.Start("ms-settings:bluetooth"); }
        catch (System.ComponentModel.Win32Exception e) { report("Couldn't open Bluetooth settings: " + e.Message); }
    }

    // Only looks while the page can be seen, and keeps looking so connections show up as they happen.
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        poll.Enabled = Visible;
        if (Visible) Reload(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) poll.Dispose();
        base.Dispose(disposing);
    }

    void OnUi(Action action)
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    // Reading the devices can take a moment, so it happens off the window's thread.
    void Reload(bool force)
    {
        if (loading) return;
        loading = true;
        if (force) shown = null;
        ThreadPool.QueueUserWorkItem(delegate
        {
            bool radio = false;
            List<BluetoothDevice> devices = null;
            try
            {
                radio = Bluetooth.HasRadio();
                devices = radio ? Bluetooth.Paired() : new List<BluetoothDevice>();
            }
            catch (Exception) { devices = new List<BluetoothDevice>(); } // Shown as an empty list and tried again on the next look.
            OnUi(delegate
            {
                loading = false;
                Show(radio, devices);
            });
        });
    }

    void Show(bool radio, List<BluetoothDevice> devices)
    {
        // Rebuilding the rows only when something changed keeps the list still between looks.
        List<string> parts = new List<string> { radio.ToString() };
        foreach (BluetoothDevice d in devices) parts.Add(d.Name + d.Connected + d.CanConnect);
        string signature = string.Join("|", parts);
        if (signature == shown) return;
        shown = signature;
        generation++;

        list.SuspendLayout();
        list.Controls.Clear();
        if (!radio)
        {
            summary.Text = "Bluetooth is off";
            list.Controls.Add(MakeLabel("Bluetooth is turned off, or this PC doesn't have it. Turn it on in Windows, then press Refresh.", Ui.Font(13, false, scale), Ui.SubText, 8, 6, 480, 44));
            PillButton settings = new PillButton("Bluetooth settings", false, scale) { Bounds = new Rectangle(S(8), S(56), S(170), S(36)), Backdrop = Ui.Card };
            settings.Font = Ui.Font(13, true, scale);
            settings.Click += delegate { OpenSettings(); };
            list.Controls.Add(settings);
        }
        else
        {
            int connected = devices.FindAll(delegate(BluetoothDevice d) { return d.Connected; }).Count;
            summary.Text = devices.Count == 0 ? "No paired devices yet" : connected + " connected, " + devices.Count + " paired";
            if (devices.Count == 0)
                list.Controls.Add(MakeLabel("Press Add a device to pair one in Windows.", Ui.Font(13, false, scale), Ui.SubText, 8, 6, 480, 28));
            for (int i = 0; i < devices.Count; i++) AddRow(devices[i], i);
        }
        list.ResumeLayout();
    }

    static string Status(BluetoothDevice device)
    {
        if (device.LowEnergy) return "Connects by itself when it's used";
        if (device.Connected) return "Connected";
        return device.CanConnect ? "Not connected" : "Not connected. Turn it on to connect";
    }

    void AddRow(BluetoothDevice device, int index)
    {
        int y = 2 + index * 50;
        list.Controls.Add(MakeLabel(device.Name, Ui.Font(14, false, scale), Ui.Text, 8, y, device.CanConnect ? 380 : 480, 24));
        Label detail = MakeLabel(device.Kind + "  ·  " + Status(device), Ui.Font(12, false, scale), device.Connected ? Ui.Honey : Ui.SubText, 8, y + 23, device.CanConnect ? 380 : 480, 20);
        list.Controls.Add(detail);
        if (!device.CanConnect) return;

        PillButton button = new PillButton(device.Connected ? "Disconnect" : "Connect", !device.Connected, scale)
        {
            Bounds = new Rectangle(S(390), S(y + 6), S(100), S(34)),
            Backdrop = Ui.Card,
            AccessibleName = (device.Connected ? "Disconnect " : "Connect ") + device.Name
        };
        button.Font = Ui.Font(13, true, scale);
        button.Click += delegate { Toggle(device, button); };
        list.Controls.Add(button);
    }

    void Toggle(BluetoothDevice device, PillButton button)
    {
        bool connect = !device.Connected;
        button.Enabled = false;
        button.Text = connect ? "Connecting" : "Disconnecting";
        int started = generation;
        ThreadPool.QueueUserWorkItem(delegate
        {
            string problem;
            try { problem = Bluetooth.SetConnected(device, connect); }
            catch (Exception e) { problem = e.Message; }
            OnUi(delegate
            {
                if (problem != null)
                {
                    report("Couldn't " + (connect ? "connect " : "disconnect ") + device.Name + ". " + problem + " Make sure it's on and nearby.");
                    if (generation == started) { button.Enabled = true; button.Text = connect ? "Connect" : "Disconnect"; }
                    return;
                }
                report((connect ? "Connecting to " : "Disconnecting from ") + device.Name + "...");
                // The driver takes a few seconds either way, so look again shortly after.
                System.Windows.Forms.Timer check = new System.Windows.Forms.Timer { Interval = 3500 };
                check.Tick += delegate { check.Stop(); check.Dispose(); Reload(true); };
                check.Start();
            });
        });
    }
}
