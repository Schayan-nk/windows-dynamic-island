using System;
using System.Runtime.InteropServices;

namespace Island;

/// <summary>Output level meter and master volume of the default speakers (Core Audio, no extra packages).</summary>
public sealed class AudioService
{
    IAudioMeterInformation? _meter;
    IAudioEndpointVolume? _volume;
    DateTime _acquired;
    static Guid _ctx = Guid.Empty;

    void Ensure()
    {
        if (_meter != null && (DateTime.Now - _acquired).TotalSeconds < 5) return;
        Release();
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            if (en.GetDefaultAudioEndpoint(0, 1, out var dev) != 0 || dev == null) return;
            var iidMeter = typeof(IAudioMeterInformation).GUID;
            if (dev.Activate(ref iidMeter, 23, IntPtr.Zero, out var m) == 0) _meter = m as IAudioMeterInformation;
            var iidVol = typeof(IAudioEndpointVolume).GUID;
            if (dev.Activate(ref iidVol, 23, IntPtr.Zero, out var v) == 0) _volume = v as IAudioEndpointVolume;
            _acquired = DateTime.Now;
            Marshal.ReleaseComObject(dev);
            Marshal.ReleaseComObject(en);
        }
        catch { Release(); }
    }

    void Release()
    {
        try { if (_meter != null) Marshal.ReleaseComObject(_meter); } catch { }
        try { if (_volume != null) Marshal.ReleaseComObject(_volume); } catch { }
        _meter = null;
        _volume = null;
    }

    public float Peak
    {
        get
        {
            Ensure();
            try { if (_meter != null && _meter.GetPeakValue(out var p) == 0) return p; } catch { _meter = null; }
            return 0;
        }
    }

    public float Volume
    {
        get
        {
            Ensure();
            try { if (_volume != null && _volume.GetMasterVolumeLevelScalar(out var v) == 0) return v; } catch { _volume = null; }
            return 0;
        }
        set
        {
            Ensure();
            try { _volume?.SetMasterVolumeLevelScalar(Math.Clamp(value, 0f, 1f), ref _ctx); } catch { _volume = null; }
        }
    }

    public bool Muted
    {
        get
        {
            Ensure();
            try { if (_volume != null && _volume.GetMute(out var m) == 0) return m; } catch { _volume = null; }
            return false;
        }
        set
        {
            Ensure();
            try { _volume?.SetMute(value, ref _ctx); } catch { _volume = null; }
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr props);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
        [PreserveSig] int GetMeteringChannelCount(out int count);
        [PreserveSig] int GetChannelsPeakValues(int count, [Out, MarshalAs(UnmanagedType.LPArray)] float[] peaks);
        [PreserveSig] int QueryHardwareSupport(out int mask);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out int count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint count);
        [PreserveSig] int VolumeStepUp(ref Guid ctx);
        [PreserveSig] int VolumeStepDown(ref Guid ctx);
        [PreserveSig] int QueryHardwareSupport(out uint mask);
        [PreserveSig] int GetVolumeRange(out float min, out float max, out float step);
    }
}
