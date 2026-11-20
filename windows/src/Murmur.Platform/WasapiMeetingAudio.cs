using Murmur.Core;

namespace Murmur.Platform;

/// <summary>The Windows <see cref="IMeetingAudio"/>: the microphone through <see cref="AudioCapture"/>, the call through <see cref="ProcessLoopback"/>.</summary>
public sealed class WasapiMeetingAudio(Func<string?> microphoneId) : IMeetingAudio
{
    public bool CallAudioSupported => ProcessLoopback.IsSupported;

    public IMeetingStream StartMicrophone(Action<float[]> samples)
    {
        try { return new Microphone(microphoneId(), samples); }
        catch (AudioCaptureException e) { throw new MeetingCaptureException(e.Message); }
    }

    public IMeetingStream StartCall(MeetingSource source, Action<float[]> samples) =>
        ProcessLoopback.Start(source.BundleID is { } name && CallApp.Find(name) is { } app ? new CallTarget.Only(app) : new CallTarget.AllExcept(AudioSessions.OwnPid), samples);

    public bool CallAppUsesMicrophone(string processName) =>
        ProcessTargets.UsesMicrophone(AudioSessions.Processes(), AudioSessions.Clients(), processName, AudioSessions.OwnPid);

    sealed class Microphone : IMeetingStream
    {
        readonly AudioCapture capture;
        long buffers, levelBits;

        public Microphone(string? endpointId, Action<float[]> samples)
        {
            capture = AudioCapture.Start(endpointId, null, block => { Interlocked.Increment(ref buffers); samples(block); },
                peak => Interlocked.Exchange(ref levelBits, BitConverter.DoubleToInt64Bits(peak)), retainSamples: false);
        }

        public double Level => BitConverter.Int64BitsToDouble(Interlocked.Read(ref levelBits));
        public long Buffers => Interlocked.Read(ref buffers);
        public bool HeardSignal => true;
        public bool IsPlaying => true;
        public bool Refresh() => true;
        public void Stop() => capture.Stop();
    }
}
