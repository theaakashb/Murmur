using System.Runtime.InteropServices;
using Murmur.Core;

namespace Murmur.Platform;

public enum AudioCaptureError { NoDevice, AccessDenied, UnsupportedFormat, DeviceFailed }

/// <summary>Content-free capture failure. AccessDenied usually means Windows Settings > Privacy > Microphone is off for desktop apps.</summary>
public sealed class AudioCaptureException(AudioCaptureError kind, string message) : Exception(message)
{
    public AudioCaptureError Kind { get; } = kind;
}

public sealed record AudioDevice(string Id, string Name, bool IsDefault);

public sealed record AudioCaptureResult(float[] Samples16k, double Seconds, string? WavPath);

/// <summary>
/// WASAPI shared-mode capture (hand-written COM interop; NAudio was not needed). Delivers 16 kHz mono Float32 to the app and writes a private
/// retry WAV in the device's own format (spec/pipeline.md §3). The 300 s cap is the caller's: it calls <see cref="Stop"/>.
/// </summary>
public sealed class AudioCapture : IDisposable
{
    // --- COM interop (vtable order matters; unused methods are declared only where they precede used ones) ---
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorClass { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, nint format, nint sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, nint format, out nint closest);
        [PreserveSig] int GetMixFormat(out nint format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(nint handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out nint data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [StructLayout(LayoutKind.Sequential)] struct PropertyKey { public Guid Format; public int Id; }
    [StructLayout(LayoutKind.Sequential)] struct PropVariant { public ushort Type, R1, R2, R3; public nint Ptr, Ptr2; }

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant value);
    [DllImport("ole32.dll")] static extern void CoTaskMemFree(nint ptr);

    const int eCapture = 1, eConsole = 0, DEVICE_STATE_ACTIVE = 1, AUDCLNT_SHAREMODE_SHARED = 0, CLSCTX_ALL = 23;
    const uint AUDCLNT_BUFFERFLAGS_SILENT = 2;
    const int E_ACCESSDENIED = unchecked((int)0x80070005), E_NOTFOUND = unchecked((int)0x80070490), AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    static readonly Guid IAudioClientId = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), IAudioCaptureClientId = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    static readonly Guid FriendlyNameFormat = new("a45c254e-df1c-4efd-8020-67d146a850e0");

    static IMMDeviceEnumerator Enumerator() => (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorClass();

    static AudioCaptureException Map(int hr, string what) => hr switch
    {
        E_ACCESSDENIED => new(AudioCaptureError.AccessDenied, "Microphone access is blocked. Allow desktop apps to use the microphone in Windows Settings > Privacy & security > Microphone."),
        E_NOTFOUND => new(AudioCaptureError.NoDevice, "No microphone is available."),
        _ => new(AudioCaptureError.DeviceFailed, $"The microphone could not be used ({what}, HRESULT 0x{hr:X8})."),
    };

    static string DeviceId(IMMDevice device) => device.GetId(out var id) == 0 ? id : "";

    static string FriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(0, out var store) != 0) return "";
        var key = new PropertyKey { Format = FriendlyNameFormat, Id = 14 };
        if (store.GetValue(ref key, out var v) != 0) return "";
        try { return v.Type == 31 ? Marshal.PtrToStringUni(v.Ptr) ?? "" : ""; } finally { PropVariantClear(ref v); }
    }

    /// <summary>Active capture endpoints for Settings; the Id is what <c>inputDeviceUID</c> stores. Empty when there is no microphone.</summary>
    public static IReadOnlyList<AudioDevice> ListDevices()
    {
        var enumerator = Enumerator();
        string defaultId = enumerator.GetDefaultAudioEndpoint(eCapture, eConsole, out var def) == 0 ? DeviceId(def) : "";
        if (enumerator.EnumAudioEndpoints(eCapture, DEVICE_STATE_ACTIVE, out var all) != 0 || all.GetCount(out uint n) != 0) return [];
        var list = new List<AudioDevice>();
        for (uint i = 0; i < n; i++)
            if (all.Item(i, out var d) == 0) { var id = DeviceId(d); list.Add(new AudioDevice(id, FriendlyName(d), id == defaultId)); }
        return list;
    }

    // --- recording ---
    readonly IAudioClient client;
    readonly IAudioCaptureClient capture;
    readonly AudioConverter converter;
    readonly WavWriter? wav;
    readonly int blockAlign;
    readonly Thread thread;
    readonly List<float> samples = [];
    readonly object gate = new();
    readonly Action<float[]>? onSamples;
    readonly Action<float>? onLevel;
    volatile bool stopping;
    Exception? failure;
    bool disposed;

    /// <summary>Peak (0..1) of the most recent 16 kHz block, for the bubble.</summary>
    public float Peak { get; private set; }
    /// <summary>Set if the device failed after start (unplugged, invalidated). Partial audio stays available from <see cref="Stop"/>.</summary>
    public Exception? Failure => failure;

    /// <param name="endpointId">Null for the default microphone (a stored ID that no longer exists is an error, not a silent fallback).</param>
    /// <param name="wavPath">Retry audio file, created exclusively (never overwrites) inside a hardened folder; null to skip the file.</param>
    /// <param name="onSamples">16 kHz mono blocks, on the capture thread.</param>
    /// <param name="onLevel">Peak per block, on the capture thread.</param>
    public static AudioCapture Start(string? endpointId = null, string? wavPath = null, Action<float[]>? onSamples = null, Action<float>? onLevel = null)
    {
        var enumerator = Enumerator();
        IMMDevice device;
        int hr = endpointId is null ? enumerator.GetDefaultAudioEndpoint(eCapture, eConsole, out device) : enumerator.GetDevice(endpointId, out device);
        if (hr != 0) throw Map(hr, "device");
        return new AudioCapture(device, wavPath, onSamples, onLevel);
    }

    AudioCapture(IMMDevice device, string? wavPath, Action<float[]>? onSamples, Action<float>? onLevel)
    {
        this.onSamples = onSamples;
        this.onLevel = onLevel;
        var iid = IAudioClientId;
        int hr = device.Activate(ref iid, CLSCTX_ALL, 0, out var obj);
        if (hr != 0) throw Map(hr, "activate");
        client = (IAudioClient)obj;
        nint format = 0;
        try
        {
            hr = client.GetMixFormat(out format);
            if (hr != 0) throw Map(hr, "format");
            var f = ParseFormat(format);
            converter = new AudioConverter(f.Rate, f.Channels, f.Kind);
            blockAlign = f.Channels * f.BitsPerSample / 8;
            hr = client.Initialize(AUDCLNT_SHAREMODE_SHARED, 0, 10_000_000, 0, format, 0); // 1 s buffer
            if (hr != 0) throw Map(hr, "initialize");
            var cid = IAudioCaptureClientId;
            hr = client.GetService(ref cid, out var cc);
            if (hr != 0) throw Map(hr, "service");
            capture = (IAudioCaptureClient)cc;
            if (wavPath is not null)
            {
                PrivateFiles.PrepareDirectory(Path.GetDirectoryName(Path.GetFullPath(wavPath))!);
                wav = new WavWriter(wavPath, f.Rate, f.Channels, f.BitsPerSample, f.Kind == SampleKind.Float32);
            }
        }
        finally { if (format != 0) CoTaskMemFree(format); }
        thread = new Thread(Loop) { IsBackground = true, Name = "Murmur audio capture" };
        hr = client.Start();
        if (hr != 0) { wav?.Dispose(); throw Map(hr, "start"); }
        thread.Start();
    }

    sealed record DeviceFormat(int Rate, int Channels, int BitsPerSample, SampleKind Kind);

    static DeviceFormat ParseFormat(nint p)
    {
        int tag = (ushort)Marshal.ReadInt16(p, 0), channels = (ushort)Marshal.ReadInt16(p, 2), rate = Marshal.ReadInt32(p, 4), bits = (ushort)Marshal.ReadInt16(p, 14);
        if (tag == 0xFFFE) tag = Marshal.ReadInt32(p, 24); // WAVEFORMATEXTENSIBLE: first word of the sub-format GUID is the tag
        var kind = (tag, bits) switch
        {
            (3, 32) => SampleKind.Float32,
            (1, 16) => SampleKind.Pcm16,
            (1, 24) => SampleKind.Pcm24,
            (1, 32) => SampleKind.Pcm32,
            _ => throw new AudioCaptureException(AudioCaptureError.UnsupportedFormat, "The microphone's audio format is not supported."),
        };
        return new DeviceFormat(rate, channels, bits, kind);
    }

    void Loop()
    {
        try
        {
            while (!stopping)
            {
                Drain();
                Thread.Sleep(10);
            }
            Drain();
        }
        catch (Exception e) { failure = e is COMException c && c.HResult == AUDCLNT_E_DEVICE_INVALIDATED ? new AudioCaptureException(AudioCaptureError.DeviceFailed, "The microphone was disconnected.") : e; }
    }

    void Drain()
    {
        while (capture.GetNextPacketSize(out uint packet) == 0 && packet > 0)
        {
            int hr = capture.GetBuffer(out var data, out uint frames, out uint flags, out _, out _);
            if (hr != 0) throw Map(hr, "read");
            var bytes = new byte[frames * blockAlign];
            if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0) Marshal.Copy(data, bytes, 0, bytes.Length); // silent packets stay zero
            capture.ReleaseBuffer(frames);
            Feed(bytes);
        }
    }

    void Feed(byte[] deviceBytes)
    {
        wav?.Write(deviceBytes);
        Deliver(converter.Push(deviceBytes));
    }

    void Deliver(float[] block)
    {
        if (block.Length == 0) return;
        float peak = 0;
        foreach (var s in block) peak = Math.Max(peak, Math.Abs(s));
        lock (gate) samples.AddRange(block);
        Peak = peak;
        onSamples?.Invoke(block);
        onLevel?.Invoke(peak);
    }

    /// <summary>Stops the device, flushes the filter and closes the WAV. Returns everything captured as 16 kHz mono (no samples: the caller reports "No audio was captured").</summary>
    public AudioCaptureResult Stop()
    {
        if (!disposed)
        {
            stopping = true;
            client.Stop();
            thread.Join(2000);
            Deliver(converter.Flush());
            wav?.Dispose();
            disposed = true;
        }
        lock (gate) return new AudioCaptureResult([.. samples], samples.Count / (double)AudioConverter.TargetRate, wav?.Path);
    }

    public void Dispose() => Stop();

    /// <summary>Little RIFF/WAVE writer: PCM or IEEE float in the device's format, created exclusively (like O_EXCL), header patched about once a second so partial audio stays playable.</summary>
    internal sealed class WavWriter : IDisposable
    {
        readonly FileStream file;
        readonly int rate, channels, bits;
        readonly bool isFloat;
        long dataBytes, lastPatch;
        public string Path { get; }

        public WavWriter(string path, int rate, int channels, int bits, bool isFloat)
        {
            Path = path; this.rate = rate; this.channels = channels; this.bits = bits; this.isFloat = isFloat;
            file = new FileStream(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read, Options = FileOptions.None });
            Patch();
        }

        void Patch()
        {
            var h = new byte[44];
            void Put(int at, byte[] b) => b.CopyTo(h, at);
            Put(0, "RIFF"u8.ToArray()); Put(4, BitConverter.GetBytes((int)(36 + dataBytes))); Put(8, "WAVEfmt "u8.ToArray());
            Put(16, BitConverter.GetBytes(16)); Put(20, BitConverter.GetBytes((short)(isFloat ? 3 : 1))); Put(22, BitConverter.GetBytes((short)channels));
            Put(24, BitConverter.GetBytes(rate)); Put(28, BitConverter.GetBytes(rate * channels * bits / 8)); Put(32, BitConverter.GetBytes((short)(channels * bits / 8)));
            Put(34, BitConverter.GetBytes((short)bits)); Put(36, "data"u8.ToArray()); Put(40, BitConverter.GetBytes((int)dataBytes));
            var end = file.Position;
            file.Position = 0; file.Write(h); file.Position = Math.Max(end, 44);
            lastPatch = dataBytes;
        }

        public void Write(byte[] bytes)
        {
            file.Write(bytes);
            dataBytes += bytes.Length;
            if (dataBytes - lastPatch >= rate * channels * bits / 8) { Patch(); file.Flush(); }
        }

        public void Dispose() { Patch(); file.Dispose(); }
    }
}
