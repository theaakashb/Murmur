using System.Diagnostics;
using System.Runtime.InteropServices;
using Murmur.Core;

namespace Murmur.Platform;

/// <summary>
/// The call's audio ("Them"): WASAPI process loopback through <c>ActivateAudioInterfaceAsync</c> with <c>AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK</c>.
/// INCLUDE_TARGET_PROCESS_TREE captures one call app's tree; EXCLUDE_TARGET_PROCESS_TREE with Murmur's own PID captures everything except Murmur.
/// Needs Windows 10 build 20348 or later (Microsoft Learn, AUDIOCLIENT_ACTIVATION_PARAMS). Delivers nothing while the target is silent, so the gap is filled
/// with zeros (<see cref="SilenceFill"/>) to keep the stream aligned with the microphone.
/// </summary>
public sealed class ProcessLoopback : IMeetingStream
{
    public const int MinimumBuild = 20348;
    public static bool IsSupported => Environment.OSVersion.Version.Build >= MinimumBuild;

    // --- COM interop (vtable order matters) ---
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

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    /// <summary>Marker the completion handler must implement: the callback arrives on an MTA worker thread.</summary>
    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAgileObject { }

    sealed class Handler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new();
        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation) { Done.Set(); return 0; }
    }

    [StructLayout(LayoutKind.Sequential)] struct PropVariantBlob { public ushort Type, R1, R2, R3; public uint Size; public nint Data; }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    static extern int ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath, ref Guid iid, nint activationParams,
        IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation operation);
    [DllImport("kernel32.dll", SetLastError = true)] static extern nint CreateEventW(nint attributes, bool manualReset, bool initial, nint name);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);

    const string ProcessLoopbackDevice = "VAD\\Process_Loopback";
    const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000, AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000, AUDCLNT_BUFFERFLAGS_SILENT = 2;
    const int VT_BLOB = 65, ModeInclude = 0, ModeExclude = 1, CaptureRate = 44100, CaptureChannels = 2;
    const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    static readonly Guid AudioClientId = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), CaptureClientId = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    /// <summary>One activated client on one process tree.</summary>
    sealed class Client : IDisposable
    {
        public readonly int Pid;
        readonly IAudioClient audio;
        public readonly IAudioCaptureClient Capture;
        public readonly nint Event;
        public readonly AudioConverter Converter = new(CaptureRate, CaptureChannels, SampleKind.Pcm16);
        public const int BlockAlign = CaptureChannels * 2;

        public Client(int pid, bool exclude)
        {
            Pid = pid;
            // AUDIOCLIENT_ACTIVATION_PARAMS: { type = PROCESS_LOOPBACK (1), { targetProcessId, mode } }
            var parameters = Marshal.AllocHGlobal(12);
            Marshal.WriteInt32(parameters, 0, 1); Marshal.WriteInt32(parameters, 4, pid); Marshal.WriteInt32(parameters, 8, exclude ? ModeExclude : ModeInclude);
            var variant = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
            Marshal.StructureToPtr(new PropVariantBlob { Type = VT_BLOB, Size = 12, Data = parameters }, variant, false);
            nint format = 0;
            try
            {
                var handler = new Handler();
                var iid = AudioClientId;
                int hr = ActivateAudioInterfaceAsync(ProcessLoopbackDevice, ref iid, variant, handler, out var operation);
                if (hr != 0) throw Fail(hr, "activation");
                if (!handler.Done.Wait(5000)) throw new MeetingCaptureException("Windows did not answer the call-audio request in time.");
                hr = operation.GetActivateResult(out int result, out var activated);
                if (hr != 0 || result != 0) throw Fail(hr != 0 ? hr : result, "activation");
                audio = (IAudioClient)activated;
                // 16-bit stereo PCM: process loopback has no mix format, so the format is ours to name.
                format = Marshal.AllocHGlobal(18);
                Marshal.WriteInt16(format, 0, 1); Marshal.WriteInt16(format, 2, CaptureChannels); Marshal.WriteInt32(format, 4, CaptureRate);
                Marshal.WriteInt32(format, 8, CaptureRate * BlockAlign); Marshal.WriteInt16(format, 12, BlockAlign); Marshal.WriteInt16(format, 14, 16); Marshal.WriteInt16(format, 16, 0);
                hr = audio.Initialize(0, AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK, 0, 0, format, 0);
                if (hr != 0) throw Fail(hr, "initialize");
                Event = CreateEventW(0, false, false, 0);
                hr = audio.SetEventHandle(Event);
                if (hr != 0) throw Fail(hr, "event");
                var cid = CaptureClientId;
                hr = audio.GetService(ref cid, out var service);
                if (hr != 0) throw Fail(hr, "service");
                Capture = (IAudioCaptureClient)service;
                hr = audio.Start();
                if (hr != 0) throw Fail(hr, "start");
            }
            catch { Dispose(); throw; }
            finally
            {
                if (format != 0) Marshal.FreeHGlobal(format);
                Marshal.FreeHGlobal(variant); Marshal.FreeHGlobal(parameters);
            }
        }

        static MeetingCaptureException Fail(int hr, string step) =>
            new($"Windows would not capture the call's audio ({step}, HRESULT 0x{hr:X8}). This needs Windows 11 and an audio output device.");

        public void Dispose()
        {
            try { audio?.Stop(); } catch (COMException) { }
            if (Event != 0) CloseHandle(Event);
        }
    }

    readonly CallTarget target;
    readonly Thread thread;
    readonly Action<float[]> onSamples;
    readonly SilenceFill fill = new();
    volatile int desiredPid;
    volatile bool stopping, playing = true;
    long buffers;
    long levelBits, signalFlag;
    Client? client;

    public double Level => BitConverter.Int64BitsToDouble(Interlocked.Read(ref levelBits));
    public long Buffers => Interlocked.Read(ref buffers);
    public bool HeardSignal => Interlocked.Read(ref signalFlag) != 0;
    public bool IsPlaying => playing;

    /// <summary>Throws <see cref="MeetingCaptureException"/> when the first activation fails (not supported, no audio device, access denied).</summary>
    public static ProcessLoopback Start(CallTarget target, Action<float[]> onSamples)
    {
        if (!IsSupported) throw new MeetingCaptureException($"Recording a call's audio needs Windows build {MinimumBuild} or later.");
        return new(target, onSamples);
    }

    ProcessLoopback(CallTarget target, Action<float[]> onSamples)
    {
        this.target = target;
        this.onSamples = onSamples;
        desiredPid = Resolve();
        if (desiredPid != 0) client = new Client(desiredPid, target is CallTarget.AllExcept);
        fill.Begin(Now);
        thread = new Thread(Loop) { IsBackground = true, Name = "Murmur call audio" };
        thread.Start();
    }

    static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>The PID to capture now: Murmur's own for "everything else", else the call app's root process that is playing (0 while the app is not running).</summary>
    int Resolve()
    {
        if (target is CallTarget.AllExcept own) return own.OwnPid;
        var all = AudioSessions.Processes();
        return ProcessTargets.Pick(ProcessTargets.Roots(all, ((CallTarget.Only)target).App), all, AudioSessions.Clients()) ?? 0;
    }

    /// <summary>Re-resolves the call app's processes, as <c>refreshProcesses()</c> does. The loop thread swaps the client when the root changed.</summary>
    public bool Refresh()
    {
        var all = AudioSessions.Processes();
        var clients = AudioSessions.Clients();
        playing = ProcessTargets.IsPlaying(all, target, clients);
        if (target is CallTarget.Only only && ProcessTargets.Pick(ProcessTargets.Roots(all, only.App), all, clients) is { } root) desiredPid = root;
        return true;
    }

    void Loop()
    {
        try
        {
            while (!stopping)
            {
                if (desiredPid != 0 && desiredPid != client?.Pid)
                {
                    try { var next = new Client(desiredPid, target is CallTarget.AllExcept); client?.Dispose(); client = next; }
                    catch (MeetingCaptureException) { desiredPid = client?.Pid ?? 0; } // keeps what it had, like refreshProcesses()
                }
                if (client is null) { Thread.Sleep(50); EmitSilence(Now - 0.1); continue; }
                WaitForSingleObject(client.Event, 50);
                if (!Drain()) EmitSilence(Now - 0.1); // 0.1 s of device latency allowance
            }
            client?.Dispose();
        }
        catch (COMException e) when (e.HResult == AUDCLNT_E_DEVICE_INVALIDATED) { /* the buffers counter stops; the recorder rebuilds the stream */ }
    }

    bool Drain()
    {
        bool any = false;
        while (client!.Capture.GetNextPacketSize(out uint packet) == 0 && packet > 0)
        {
            if (client.Capture.GetBuffer(out var data, out uint frames, out uint flags, out _, out ulong qpc) != 0) break;
            var bytes = new byte[frames * Client.BlockAlign];
            if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0) Marshal.Copy(data, bytes, 0, bytes.Length); // silent packets stay zero
            client.Capture.ReleaseBuffer(frames);
            var block = client.Converter.Push(bytes);
            if (block.Length == 0) continue;
            double at = qpc != 0 ? qpc / 1e7 : Now; // the device's QPC clock, in the Stopwatch timebase
            EmitSilence(at);
            fill.Audio(at, block.Length);
            Measure(block);
            Interlocked.Increment(ref buffers);
            onSamples(block);
            any = true;
        }
        return any;
    }

    void EmitSilence(double at)
    {
        int pieces = fill.Pieces(at);
        if (pieces == 0) return;
        Interlocked.Exchange(ref levelBits, BitConverter.DoubleToInt64Bits(0));
        for (int i = 0; i < pieces; i++) onSamples(new float[SilenceFill.PieceFrames]);
    }

    void Measure(float[] block)
    {
        double energy = 0;
        foreach (var s in block) energy += (double)s * s;
        if (energy > 0) Interlocked.Exchange(ref signalFlag, 1);
        Interlocked.Exchange(ref levelBits, BitConverter.DoubleToInt64Bits(Math.Min(1, Math.Sqrt(energy / block.Length) * 8)));
    }

    /// <summary>Stops the client, joins the thread and flushes the converter's tail.</summary>
    public void Stop()
    {
        if (stopping) return;
        stopping = true;
        thread.Join(3000);
        if (client is { } last) { var tail = last.Converter.Flush(); if (tail.Length > 0) onSamples(tail); }
        client = null;
    }
}
