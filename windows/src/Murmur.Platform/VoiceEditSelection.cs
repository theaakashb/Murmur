using Murmur.Core;

namespace Murmur.Platform;

public static partial class FocusProbe
{
    /// <summary>One selection in the original UI Automation element, read on a worker thread.</summary>
    public sealed class VoiceEditSelection(ClipboardPaste paste, CancellationToken cancellation = default) : IVoiceEditTarget
    {
        LearningTarget? element;
        bool captured;
        int processId;
        readonly string identity = Guid.NewGuid().ToString("N");

        public VoiceEditTarget? Capture()
        {
            if (cancellation.IsCancellationRequested) return null;
            if (!captured)
            {
                captured = true;
                var window = GetForegroundWindow();
                if (window == 0 || GetWindowThreadProcessId(window, out var pid) == 0) return null;
                processId = (int)pid;
                if (processId == Environment.ProcessId)
                    return new(processId, identity, null, null, null, false, false);
                element = LearningTarget.Capture(out bool password);
                if (GetForegroundWindow() != window) { element = null; return null; }
                if (password) return new(processId, identity, null, null, null, false, true);
            }

            if (element?.Read() is not { } snapshot) return null;
            var range = snapshot.Selection;
            return new(processId, identity, snapshot.Value, (range.Location, range.Length),
                snapshot.Value.Substring(range.Location, range.Length), true, false);
        }

        public bool TryReplace(VoiceEditTarget expected, string text) =>
            Capture() is { } current && expected.Matches(current) &&
            paste.Paste(text, stillTarget: () =>
                Capture() is { } latest && expected.Matches(latest) &&
                element?.IsNativeFocusCurrent() == true && !cancellation.IsCancellationRequested).Pasted;
    }
}
