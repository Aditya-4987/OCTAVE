using System;
using System.Runtime.InteropServices;

namespace Octave.Core.Helpers;

public static class WindowsAudioDeviceHelper
{
    [ComImport]
    [Guid("BCDE0385-4944-4EA6-82B2-9D67097C93FE")]
    [ClassInterface(ClassInterfaceType.None)]
    private class MMDeviceEnumerator { }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr ppDevices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
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
    [Guid("886D8EEB-8CF2-4446-8D02-CDA11D728A32")]
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
        fmtid = new Guid(0xf196b2b5, 0x7b08, 0x4802, 0x91, 0x9b, 0xed, 0x22, 0x64, 0xf6, 0x3d, 0x82),
        pid = 0
    };

    private static readonly PROPERTYKEY PKEY_Device_FriendlyName = new PROPERTYKEY
    {
        fmtid = new Guid(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0),
        pid = 14
    };

    private static (string Name, string Format, double SampleRateKhz, ushort BitDepth) _cachedDeviceDetails = ("Default Audio Device", "Unknown", 44.1, 16);
    private static DateTime _lastCacheTime = DateTime.MinValue;
    private static readonly object _cacheLock = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(3);

    public static (string Name, string Format, double SampleRateKhz, ushort BitDepth) GetDefaultOutputDeviceDetails(bool forceRefresh = false)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ("Default Audio Device", "Unknown", 44.1, 16);
        }

        lock (_cacheLock)
        {
            if (!forceRefresh && DateTime.UtcNow - _lastCacheTime < CacheTtl)
            {
                return _cachedDeviceDetails;
            }
        }

#pragma warning disable CA1416 // Validate platform compatibility
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IPropertyStore? store = null;
        PROPVARIANT pvName = default;
        PROPVARIANT pvFormat = default;

        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            // eRender = 0, eConsole = 0
            if (enumerator.GetDefaultAudioEndpoint(0, 0, out device) == 0 && device != null)
            {
                string devName = "Default Audio Device";
                string devFormat = "Unknown";
                double devKhz = 44.1;
                ushort devBits = 16;

                if (device.OpenPropertyStore(0 /* STGM_READ */, out store) == 0 && store != null)
                {
                    var pkeyName = PKEY_Device_FriendlyName;
                    if (store.GetValue(ref pkeyName, out pvName) == 0 && pvName.vt == 31 /* VT_LPWSTR */)
                    {
                        devName = Marshal.PtrToStringUni(pvName.pwszVal) ?? devName;
                    }

                    var pkeyFormat = PKEY_AudioEngine_DeviceFormat;
                    if (store.GetValue(ref pkeyFormat, out pvFormat) == 0 && pvFormat.vt == 65 /* VT_BLOB */)
                    {
                        uint blobSize = pvFormat.blobCount;
                        IntPtr dataPtr = pvFormat.blobData;

                        if (dataPtr != IntPtr.Zero && blobSize >= (uint)Marshal.SizeOf<WAVEFORMATEX>())
                        {
                            var waveFormat = Marshal.PtrToStructure<WAVEFORMATEX>(dataPtr);
                            ushort bits = waveFormat.wBitsPerSample;
                            if (blobSize >= (uint)Marshal.SizeOf<WAVEFORMATEXTENSIBLE>() && waveFormat.cbSize >= 22)
                            {
                                var ext = Marshal.PtrToStructure<WAVEFORMATEXTENSIBLE>(dataPtr);
                                if (ext.wValidBitsPerSample > 0) bits = ext.wValidBitsPerSample;
                            }
                            devBits = bits;
                            devKhz = waveFormat.nSamplesPerSec / 1000.0;
                            devFormat = $"{bits}-bit {devKhz:0.0}kHz (Shared Mode)";
                        }
                    }
                }

                var result = (devName, devFormat, devKhz, devBits);
                lock (_cacheLock)
                {
                    _cachedDeviceDetails = result;
                    _lastCacheTime = DateTime.UtcNow;
                }
                return result;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WindowsAudioDeviceHelper] Query failed: {ex.Message}");
        }
        finally
        {
            if (pvName.vt != 0) PropVariantClear(ref pvName);
            if (pvFormat.vt != 0) PropVariantClear(ref pvFormat);
            if (store != null) Marshal.ReleaseComObject(store);
            if (device != null) Marshal.ReleaseComObject(device);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }
#pragma warning restore CA1416

        return ("Default Audio Device", "Unknown", 44.1, 16);
    }
}
