// Ports the ShortcutMachine assertions of app/check_recording.swift (button start, hold, double press, toggle, Escape); the listener and key events are platform code.
using Murmur.Core;

namespace Murmur.Core.Tests;

public class ShortcutMachineTests
{
    [Fact]
    public void ButtonStartLatchesLikeAToggle()
    {
        var c = new Checker();
        var machine = new ShortcutMachine();
        c.Check(machine.StartToggled() == ShortcutAction.Start && machine.State == ShortcutState.Listening, "button start listens");
        c.Check(machine.StartToggled() is null, "a second button start does nothing");
        c.Check(machine.Release(1) is null, "a stray release does not finish it");
        c.Check(machine.Press(2) == ShortcutAction.Finish && machine.State == ShortcutState.Processing, "the next press finishes it");
        c.Check(machine.Release(2.1) is null, "that press's release does nothing");
        c.Check(machine.StartToggled() is null, "no start while processing");
        machine.ProcessingFinished();
        c.Check(machine.StartToggled() == ShortcutAction.Start && machine.Finish() == ShortcutAction.Finish, "Finish ends a button start");
        machine.ProcessingFinished();
        machine.Press(5);
        c.Check(machine.StartToggled() is null, "no button start while a key is down");
        var cancelled = new ShortcutMachine(); cancelled.StartToggled();
        c.Check(cancelled.Cancel() == ShortcutAction.Cancel && cancelled.State == ShortcutState.Idle, "Cancel stops a button start");
        c.Done();
    }

    [Fact]
    public void HoldDoublePressToggleAndEscape()
    {
        var c = new Checker();
        var m = new ShortcutMachine();
        var actions = new List<ShortcutAction?>();
        void Feed(ShortcutAction? action) { if (action is not null) actions.Add(action); }
        Feed(m.Press(1)); Feed(m.Release(1.6));
        c.Check(actions.SequenceEqual([ShortcutAction.Start, ShortcutAction.Finish]), "hold records until release");
        m.ProcessingFinished();
        Feed(m.Press(3)); Feed(m.Release(3.1)); Feed(m.Press(3.3)); Feed(m.Release(3.4));
        c.Check(actions.SequenceEqual([ShortcutAction.Start, ShortcutAction.Finish, ShortcutAction.Start]) && m.State == ShortcutState.Listening, "double press latches a toggle");
        Feed(m.Cancel());
        c.Check(actions[^1] == ShortcutAction.Cancel && m.State == ShortcutState.Idle, "Escape cancels");

        var toggle = new ShortcutMachine { PressToToggle = true };
        actions.Clear();
        Feed(toggle.Press(1)); Feed(toggle.Release(1.1)); Feed(toggle.Press(4));
        c.Check(actions.SequenceEqual([ShortcutAction.Start, ShortcutAction.Finish]), "press-to-toggle: tap starts, next press finishes");

        var cancelling = new ShortcutMachine();
        cancelling.Press(20); cancelling.Release(20.1);
        c.Check(cancelling.AwaitingSecondPress && cancelling.DoublePressExpired(20.3) is null, "a lone tap waits for the window");
        c.Check(cancelling.DoublePressExpired(21) == ShortcutAction.Cancel && cancelling.State == ShortcutState.Idle && !cancelling.AwaitingSecondPress, "a lone tap is cancelled once the window passes");
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShortcutMachine(0));
        c.Done();
    }
}
