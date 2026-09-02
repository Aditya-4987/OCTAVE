using System;
using System.Runtime.InteropServices;

namespace Octave.Core.Helpers;

public static class WindowsAudioDeviceHelper
{
    // CLSID_MMDeviceEnumerator (mmdeviceapi.h). The GUID this used to carry was
    // not any registered class, so EVERY query failed with REGDB_E_CLASSNOTREG
    // (0x80040154) and the app silently ran on the BASS-side fallback strings.
    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    [ClassInterface(ClassInterfaceType.None)]
    private class MMDeviceEnumerator { }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr ppDevices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
        int RegisterEndpointNotificationCallback(IMMNotificationClient pClient);
        int UnregisterEndpointNotificationCallback(IMMNotificationClient pClient);
    }

    [ComImport]
    [Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMNotificationClient
    {
        [PreserveSig]
        int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, int dwNewState);

        [PreserveSig]
        int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);

        [PreserveSig]
        int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);

        [PreserveSig]
        int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string pwstrDefaultDeviceId);

        [PreserveSig]
        int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, PROPERTYKEY key);
    }

    private class NotificationClientImpl : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string pwstrDeviceId, int dwNewState)
        {
            ClearCache();
            AudioEndpointsChanged?.Invoke();
            return 0;
        }

        public int OnDeviceAdded(string pwstrDeviceId)
        {
            ClearCache();
            AudioEndpointsChanged?.Invoke();
            return 0;
        }

        public int OnDeviceRemoved(string pwstrDeviceId)
        {
            ClearCache();
            DeviceRemoved?.Invoke(pwstrDeviceId);
            AudioEndpointsChanged?.Invoke();
            return 0;
        }

        public int OnDefaultDeviceChanged(int flow, int role, string pwstrDefaultDeviceId)
        {
            // flow: eRender = 0. role: eConsole = 0, eMultimedia = 1
            if (flow == 0)
            {
                ClearCache();
                DefaultAudioEndpointChanged?.Invoke(pwstrDefaultDeviceId);
                AudioEndpointsChanged?.Invoke();
            }
            return 0;
        }

        public int OnPropertyValueChanged(string pwstrDeviceId, PROPERTYKEY key)
        {
            ClearCache();
            AudioEndpointsChanged?.Invoke();
            return 0;
        }
    }

    public static event Action? AudioEndpointsChanged;
    public static event Action<string?>? DefaultAudioEndpointChanged;
    public static event Action<string>? DeviceRemoved;

    private static IMMDeviceEnumerator? _notificationEnumerator;
    private static NotificationClientImpl? _notificationClient;
    private static readonly object _notificationLock = new();

    public static void StartMonitoring()
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (_notificationLock)
        {
            if (_notificationClient != null) return;
            try
            {
                _notificationEnumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                _notificationClient = new NotificationClientImpl();
                int hr = _notificationEnumerator.RegisterEndpointNotificationCallback(_notificationClient);
                if (hr != 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[WindowsAudioDeviceHelper] RegisterEndpointNotificationCallback returned 0x{hr:X8}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WindowsAudioDeviceHelper] Failed to register notification client: {ex.Message}");
            }
        }
    }

    public static void StopMonitoring()
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (_notificationLock)
        {
            if (_notificationEnumerator != null && _notificationClient != null)
            {
                try
                {
                    _notificationEnumerator.UnregisterEndpointNotificationCallback(_notificationClient);
                }
                catch { }
                _notificationClient = null;
                _notificationEnumerator = null;
            }
        }
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, out IntPtr ppInterface);
        int OpenPropertyStore(int stgmAccess, out IPropertyStore ppProperties);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        int GetState(out int pdwState);
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out int cProps);
        int GetAt(int iProp, out PROPERTYKEY pkey);
        int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        int SetValue(ref PROPERTYKEY key, ref PROPVARIANT propvar);
        int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(2)] public ushort wReserved1;
        [FieldOffset(4)] public ushort wReserved2;
        [FieldOffset(6)] public ushort wReserved3;
        [FieldOffset(8)] public IntPtr pwszVal;
        [FieldOffset(8)] public uint blobCount;
        [FieldOffset(16)] public IntPtr blobData;
    }

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WAVEFORMATEXTENSIBLE
    {
        public WAVEFORMATEX Format;
        public ushort wValidBitsPerSample;
        public uint dwChannelMask;
        public Guid SubFormat;
    }

    private static readonly PROPERTYKEY PKEY_AudioEngine_DeviceFormat = new PROPERTYKEY
    {
        fmtid = new Guid(0xf19f064d, 0x082c, 0x4e27, 0xbc, 0x73, 0x68, 0x82, 0xa1, 0xbb, 0x8e, 0x4c),
        pid = 0
    };

    private static readonly PROPERTYKEY PKEY_Device_FriendlyName = new PROPERTYKEY
    {
        fmtid = new Guid(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0),
        pid = 14
    };

    private static readonly PROPERTYKEY PKEY_AudioEndpoint_FormFactor = new PROPERTYKEY
    {
        fmtid = new Guid(0x1da5d803, 0xd492, 0x4edd, 0x8c, 0x23, 0xe0, 0xc0, 0xff, 0xee, 0x7f, 0x0e),
        pid = 0
    };

    private static readonly System.Collections.Generic.Dictionary<string, (string Name, string Format, double SampleRateKhz, ushort BitDepth, string DeviceType)> _cachedDeviceDetailsMap = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _lastCacheTime = DateTime.MinValue;
    private static bool _lastQueryFailed = false;
    private static readonly object _cacheLock = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FailureCacheTtl = TimeSpan.FromSeconds(2);
    public static string? LastErrorMessage { get; private set; }

    public static void ClearCache()
    {
        lock (_cacheLock)
        {
            _cachedDeviceDetailsMap.Clear();
            _lastCacheTime = DateTime.MinValue;
            _lastQueryFailed = false;
        }
    }

    public static string ClassifyDeviceType(string deviceName, uint formFactor = 10)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return "Audio Output";

        // 1. High-end dedicated DAC keywords
        string[] dacKeywords = { "DAC", "FiiO", "Topping", "iFi", "Schiit", "Audioengine", "DragonFly", "Chord", "Zen", "Moondrop", "AudioQuest", "Cambridge" };
        foreach (var kw in dacKeywords)
        {
            if (deviceName.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return "External DAC / Audio Interface";
        }

        // 2. USB audio interface
        if (deviceName.Contains("USB", StringComparison.OrdinalIgnoreCase))
        {
            return "USB DAC / Audio Device";
        }

        // 3. Bluetooth audio
        if (deviceName.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase))
        {
            return "Bluetooth Audio";
        }

        // 4. FormFactor detection (Windows EndpointFormFactor enum)
        // 3 = Headphones, 5 = Headset
        if (formFactor == 3 || formFactor == 5 || deviceName.Contains("Headphone", StringComparison.OrdinalIgnoreCase) || deviceName.Contains("Headset", StringComparison.OrdinalIgnoreCase) || deviceName.Contains("IEM", StringComparison.OrdinalIgnoreCase))
        {
            return "Headphones";
        }

        // 2 = LineLevel, 8 = SPDIF (Line Out / Digital Optical / Coaxial DAC)
        if (formFactor == 2 || formFactor == 8)
        {
            return "External DAC / Line Out";
        }

        // 9 = DigitalAudioDisplayDevice / HDMI
        if (formFactor == 9 || deviceName.Contains("HDMI", StringComparison.OrdinalIgnoreCase) || deviceName.Contains("Display", StringComparison.OrdinalIgnoreCase) || deviceName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
        {
            return "Digital HDMI / Display Audio";
        }

        // 1 = Speakers
        if (formFactor == 1 || deviceName.Contains("Speaker", StringComparison.OrdinalIgnoreCase))
        {
            return "Speakers";
        }

        return "Audio Endpoint";
    }

    // TEST-07: pure parse of the PKEY_AudioEngine_DeviceFormat blob (a
    // WAVEFORMATEX or WAVEFORMATEXTENSIBLE) into the user-facing format string,
    // sample rate in kHz and effective bit depth. Extracted from the COM call so
    // tests can feed synthetic blobs; a too-short/garbage blob yields the same
    // ("Unknown", 44.1, 16) fallback the live path used to hardcode.
    internal static (string Format, double SampleRateKhz, ushort BitDepth) ParseDeviceFormatBlob(byte[] blob)
    {
        const ushort FallbackBits = 16;
        const double FallbackKhz = 44.1;

        if (blob == null || blob.Length < Marshal.SizeOf<WAVEFORMATEX>())
        {
            return ("Unknown", FallbackKhz, FallbackBits);
        }

        var waveFormat = ReadBlobStruct<WAVEFORMATEX>(blob, Marshal.SizeOf<WAVEFORMATEX>());
        ushort bits = waveFormat.wBitsPerSample;
        double khz = waveFormat.nSamplesPerSec / 1000.0;
        if (khz <= 0) khz = FallbackKhz;

        if (blob.Length >= Marshal.SizeOf<WAVEFORMATEXTENSIBLE>() && waveFormat.cbSize >= 22)
        {
            var ext = ReadBlobStruct<WAVEFORMATEXTENSIBLE>(blob, Marshal.SizeOf<WAVEFORMATEXTENSIBLE>());
            if (ext.wValidBitsPerSample > 0) bits = ext.wValidBitsPerSample;
        }

        if (bits == 0) bits = FallbackBits;

        return ($"{bits}-bit {khz:0.0}kHz (Shared Mode)", khz, bits);
    }

    // Marshaling needs a stable native address — copy the managed bytes through a
    // temp HGLOBAL sized for the struct read and always free it.
    private static T ReadBlobStruct<T>(byte[] blob, int structBytes) where T : struct
    {
        IntPtr ptr = Marshal.AllocHGlobal(structBytes);
        try
        {
            int bytesToCopy = Math.Min(blob.Length, structBytes);
            Marshal.Copy(blob, 0, ptr, bytesToCopy);
            // Zero-fill any remainder so a short EXTENSIBLE tail can't leak garbage.
            if (bytesToCopy < structBytes)
            {
                Marshal.Copy(new byte[structBytes - bytesToCopy], 0, ptr + bytesToCopy, structBytes - bytesToCopy);
            }
            return Marshal.PtrToStructure<T>(ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public static (string Name, string Format, double SampleRateKhz, ushort BitDepth, string DeviceType) GetOutputDeviceInfo(string? deviceEndpointId = null, bool forceRefresh = false)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ("Default Audio Device", "Unknown", 44.1, 16, "Audio Endpoint");
        }

        string cacheKey = string.IsNullOrWhiteSpace(deviceEndpointId) ? "__default__" : deviceEndpointId;
        lock (_cacheLock)
        {
            var ttl = _lastQueryFailed ? FailureCacheTtl : CacheTtl;
            if (!forceRefresh && DateTime.UtcNow - _lastCacheTime < ttl && _cachedDeviceDetailsMap.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

#pragma warning disable CA1416 // Validate platform compatibility
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IPropertyStore? store = null;
        PROPVARIANT pvName = default;
        PROPVARIANT pvFormat = default;
        PROPVARIANT pvFactor = default;

        StartMonitoring();

        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            int hr = -1;
            if (!string.IsNullOrWhiteSpace(deviceEndpointId))
            {
                hr = enumerator.GetDevice(deviceEndpointId, out device);
            }
            else
            {
                // Default audio endpoint: eRender = 0, eConsole = 0
                hr = enumerator.GetDefaultAudioEndpoint(0, 0, out device);
            }

            if (hr == 0 && device != null)
            {
                string devName = "Default Audio Device";
                string devFormat = "Unknown";
                double devKhz = 44.1;
                ushort devBits = 16;
                uint formFactor = 10;

                if (device.OpenPropertyStore(0 /* STGM_READ */, out store) == 0 && store != null)
                {
                    var pkeyName = PKEY_Device_FriendlyName;
                    if (store.GetValue(ref pkeyName, out pvName) == 0 && pvName.vt == 31 /* VT_LPWSTR */)
                    {
                        devName = Marshal.PtrToStringUni(pvName.pwszVal) ?? devName;
                    }

                    var pkeyFactor = PKEY_AudioEndpoint_FormFactor;
                    if (store.GetValue(ref pkeyFactor, out pvFactor) == 0 && pvFactor.vt == 19 /* VT_UI4 */)
                    {
                        formFactor = (uint)pvFactor.blobCount;
                    }

                    var pkeyFormat = PKEY_AudioEngine_DeviceFormat;
                    if (store.GetValue(ref pkeyFormat, out pvFormat) == 0 && pvFormat.vt == 65 /* VT_BLOB */)
                    {
                        uint blobSize = pvFormat.blobCount;
                        IntPtr dataPtr = pvFormat.blobData;

                        if (dataPtr != IntPtr.Zero && blobSize > 0)
                        {
                            byte[] blob = new byte[blobSize];
                            Marshal.Copy(dataPtr, blob, 0, (int)blobSize);
                            (devFormat, devKhz, devBits) = ParseDeviceFormatBlob(blob);
                        }
                    }
                }

                string devType = ClassifyDeviceType(devName, formFactor);
                var result = (devName, devFormat, devKhz, devBits, devType);
                lock (_cacheLock)
                {
                    _cachedDeviceDetailsMap[cacheKey] = result;
                    _lastCacheTime = DateTime.UtcNow;
                    _lastQueryFailed = false;
                }
                return result;
            }
        }
        catch (Exception ex)
        {
            lock (_cacheLock)
            {
                _lastCacheTime = DateTime.UtcNow;
                LastErrorMessage = ex.ToString();
                if (!_lastQueryFailed)
                {
                    _lastQueryFailed = true;
                    System.Diagnostics.Debug.WriteLine($"[WindowsAudioDeviceHelper] Query failed (cached {FailureCacheTtl.TotalMinutes:0}m): {ex.Message}");
                }
                var fallback = GetBassDeviceFallback(deviceEndpointId);
                _cachedDeviceDetailsMap[cacheKey] = fallback;
                return fallback;
            }
        }
        finally
        {
            if (pvName.vt != 0) PropVariantClear(ref pvName);
            if (pvFormat.vt != 0) PropVariantClear(ref pvFormat);
            if (pvFactor.vt != 0) PropVariantClear(ref pvFactor);
            if (store != null) Marshal.ReleaseComObject(store);
            if (device != null) Marshal.ReleaseComObject(device);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }
#pragma warning restore CA1416

        return GetBassDeviceFallback(deviceEndpointId);
    }

    public static (string Name, string Format, double SampleRateKhz, ushort BitDepth) GetDefaultOutputDeviceDetails(bool forceRefresh = false)
    {
        var (name, format, khz, bits, _) = GetOutputDeviceInfo(null, forceRefresh);
        return (name, format, khz, bits);
    }

    public static string? GetDefaultOutputEndpointId()
    {
        if (!OperatingSystem.IsWindows()) return null;

#pragma warning disable CA1416
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(0, 0, out device) == 0 && device != null)
            {
                if (device.GetId(out string id) == 0)
                {
                    return id;
                }
            }
        }
        catch { }
        finally
        {
            if (device != null) Marshal.ReleaseComObject(device);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }
#pragma warning restore CA1416

        return null;
    }

    private static (string Name, string Format, double SampleRateKhz, ushort BitDepth, string DeviceType) GetBassDeviceFallback(string? deviceEndpointId = null)
    {
        try
        {
            for (int i = 1; ManagedBass.Bass.GetDeviceInfo(i, out var info); i++)
            {
                if ((!string.IsNullOrWhiteSpace(deviceEndpointId) && info.Driver == deviceEndpointId) ||
                    (string.IsNullOrWhiteSpace(deviceEndpointId) && info.IsDefault && info.IsEnabled))
                {
                    string name = info.Name ?? "Default Audio Device";
                    string type = ClassifyDeviceType(name, 10);
                    return (name, "Standard (44.1 kHz / 16-bit)", 44.1, 16, type);
                }
            }
        }
        catch { }
        return ("Default Audio Device", "Standard (44.1 kHz / 16-bit)", 44.1, 16, "Audio Endpoint");
    }
}
