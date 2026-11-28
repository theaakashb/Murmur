// Direct checks for Observation.cs. The Mac checks DictationObservation and KeystrokeSpan only through check_processing's learning checks
// (ported in ProcessingTests.Learning); these pin the boundary, clock, anchor and stop-reason rules those leave out.
using System.Text.Json;
using Murmur.Core;

namespace Murmur.Core.Tests;

public class ObservationTests
{
    static Utf16Range R(int location, int length) => new(location, length);

    [Fact]
    public void CreateRejectsInvalidSpans()
    {
        Assert.NotNull(DictationObservation.Create("Hi Call Acme today", R(3, 15), 0));
        Assert.Null(DictationObservation.Create("Call Acme", R(0, 0), 0));
        Assert.Null(DictationObservation.Create("Call Acme", R(-1, 2), 0));
        Assert.Null(DictationObservation.Create("Call Acme", R(5, 10), 0));
        Assert.Null(DictationObservation.Create("Call Acme", R(0, 4), double.NaN));
        // A span cutting a word cannot establish a whole-word correction.
        Assert.Null(DictationObservation.Create("Recall Acme", R(2, 9), 0));
        Assert.Null(DictationObservation.Create("Call Acmeish", R(0, 9), 0));
        // UTF-16 offsets must fall on character boundaries: not inside a surrogate pair or a ZWJ sequence.
        Assert.Null(DictationObservation.Create("ok 😀 go", R(4, 4), 0));
        var family = "hi 👨‍👩‍👧 go";
        Assert.Null(DictationObservation.Create(family, R(3, 2), 0));
        Assert.NotNull(DictationObservation.Create(family, R(3, family.Length - 3), 0));
    }

    [Fact]
    public void ObserveFollowsTheSpanAndStops()
    {
        var o = DictationObservation.Create("Hi Call Acme. Bye", R(3, 10), 10)!;
        Assert.Equal("Call Acme.", o.Initial);
        o.Observe("Hi Call Akme. Bye", R(3, 0), 11, true, true);
        Assert.True(o.Edited == "Call Akme." && o.Range == R(3, 10) && !o.Stopped);
        o.Observe("Hi Call Akme today. Bye", R(19, 0), 12, true, true);
        Assert.Equal("Call Akme today.", o.Edited);
        // A selection outside the span stops it and keeps the edit.
        o.Observe("Hi Call Akme today. Bye", R(0, 1), 13, true, true);
        Assert.True(o.Stopped && o.Reason == DictationObservation.StopReason.SelectionLeftSpan && o.Edited == "Call Akme today.");
        o.Observe("Hi Call Acme. Bye", R(3, 0), 14, true, true);
        Assert.Equal("Call Akme today.", o.Edited); // stopped: later snapshots are ignored
        o.Finish();
        Assert.Equal(DictationObservation.StopReason.SelectionLeftSpan, o.Reason);
    }

    [Fact]
    public void StopReasonsKeepOrDropTheEdit()
    {
        DictationObservation Edited()
        {
            var o = DictationObservation.Create("Hi Call Acme. Bye", R(3, 10), 10)!;
            o.Observe("Hi Call Akme. Bye", R(3, 0), 11, true, true);
            return o;
        }
        (DictationObservation.StopReason?, string?) After(Action<DictationObservation> step) { var o = Edited(); step(o); return (o.Reason, o.Edited); }

        Assert.Equal((DictationObservation.StopReason.AccessLost, (string?)null), After(o => o.Observe("Hi Call Akme. Bye", R(3, 0), 12, true, false)));
        Assert.Equal((DictationObservation.StopReason.InvalidClock, (string?)null), After(o => o.Observe("Hi Call Akme. Bye", R(3, 0), 9, true, true)));
        Assert.Equal((DictationObservation.StopReason.InvalidClock, (string?)null), After(o => o.Observe("Hi Call Akme. Bye", R(3, 0), double.PositiveInfinity, true, true)));
        Assert.Equal((DictationObservation.StopReason.FocusDeparted, "Call Akme."), After(o => o.Observe("Hi Call Akme. Bye", R(3, 0), 12, false, true)));
        Assert.Equal((DictationObservation.StopReason.WindowEnded, "Call Akme."), After(o => o.Observe("Hi Call Akme. Bye", R(3, 0), 70, true, true)));
        Assert.Equal((DictationObservation.StopReason.OutsideAnchorsChanged, (string?)null), After(o => o.Observe("Yo Call Akme. Bye", R(3, 0), 12, true, true)));
        // A chat composer empties when Return sends it: the fix made before that is kept.
        Assert.Equal((DictationObservation.StopReason.SpanCleared, "Call Akme."), After(o => o.Observe("Hi  Bye", R(3, 0), 12, true, true)));
        Assert.Equal((DictationObservation.StopReason.Finished, "Call Akme."), After(o => o.Finish()));
        Assert.Equal("selection left dictated span", DictationObservation.StopReason.SelectionLeftSpan.RawValue());
        Assert.Equal("access or security lost", DictationObservation.StopReason.AccessLost.RawValue());
    }

    [Fact]
    public void KeystrokeSpanEditsAndDeparts()
    {
        var span = new KeystrokeSpan("Call Acme");
        Assert.Equal(R(9, 0), span.Selection);
        span.Apply(new KeystrokeSpan.Key.Left(true, true)); // select "Acme"
        Assert.Equal(R(5, 4), span.Selection);
        span.Apply(new KeystrokeSpan.Key.Text("Akme"));
        Assert.True(span.Value == "Call Akme" && span.State == KeystrokeSpan.SpanState.Editing);
        // Characters are grapheme clusters, offsets UTF-16: one backspace removes a whole emoji.
        span.Apply(new KeystrokeSpan.Key.Text(" 👍🏽"));
        Assert.Equal(R(14, 0), span.Selection);
        span.Apply(new KeystrokeSpan.Key.Backspace(false));
        Assert.Equal("Call Akme ", span.Value);
        // Foundation's controlCharacters include format characters (Cf): a zero-width space ends the edit, like Return.
        span.Apply(new KeystrokeSpan.Key.Text("​"));
        Assert.Equal(KeystrokeSpan.SpanState.Departed, span.State);
        var lost = new KeystrokeSpan("Hi");
        lost.Apply(new KeystrokeSpan.Key.ForwardDelete());
        Assert.Equal(KeystrokeSpan.SpanState.Lost, lost.State);
        var right = new KeystrokeSpan("Hi");
        right.Apply(new KeystrokeSpan.Key.Right(false, false));
        Assert.Equal(KeystrokeSpan.SpanState.Departed, right.State);
    }

    [Fact]
    public void RawValuesSerializeAsInSwift()
    {
        Assert.Equal("\"cloud\"", JsonSerializer.Serialize(CleanupRoute.Cloud));
        Assert.Equal("\"custom-provider\"", JsonSerializer.Serialize(AIEngine.CustomProvider));
        Assert.Equal("\"apple-intelligence\"", JsonSerializer.Serialize(AIEngine.AppleIntelligence));
    }
}
