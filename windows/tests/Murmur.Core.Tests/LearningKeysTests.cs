using Murmur.Core;

namespace Murmur.Core.Tests;

/// <summary>The keystroke edit model: key events -> KeystrokeSpan edits, with synthetic text only.</summary>
public class LearningKeysTests
{
    static KeystrokeSpan.Key Type(string s) => new KeystrokeSpan.Key.Text(s);
    static KeystrokeSpan.Key Map(int vk, bool ctrl = false, bool shift = false, bool alt = false, bool win = false, string? typed = "x")
        => LearningKeys.Map(vk, ctrl, shift, alt, win, () => typed);

    [Fact]
    public void PlainKeysTypeAndBackspaceEditsTheSpan()
    {
        var span = new KeystrokeSpan("call acme now");
        span.Apply(Map(LearningKeys.VkBack)); span.Apply(Map(LearningKeys.VkBack)); span.Apply(Map(LearningKeys.VkBack));
        span.Apply(Map('O', typed: "N")); span.Apply(Map('O', typed: "O")); span.Apply(Map('W', typed: "w"));
        Assert.Equal("call acme NOw", span.Value);
        Assert.Equal(KeystrokeSpan.SpanState.Editing, span.State);
    }

    [Fact]
    public void WordBackspaceAndSelectionReplaceFollowTheCaret()
    {
        var span = new KeystrokeSpan("send it to git hub now");
        span.Apply(Map(LearningKeys.VkBack, ctrl: true));          // "now" removed
        span.Apply(Map(LearningKeys.VkBack, ctrl: true));          // "hub " removed
        span.Apply(Map('G', typed: "hub"));
        Assert.Equal("send it to git hub", span.Value.TrimEnd());
        var left = new KeystrokeSpan("one two");
        left.Apply(Map(LearningKeys.VkLeft, ctrl: true, shift: true)); // select "two"
        left.Apply(Map('Z', typed: "2"));
        Assert.Equal("one 2", left.Value);
    }

    [Fact]
    public void ArrowsAndForwardDeleteStayInsideTheSpan()
    {
        var span = new KeystrokeSpan("abc");
        span.Apply(Map(LearningKeys.VkLeft)); span.Apply(Map(LearningKeys.VkLeft));
        span.Apply(Map(LearningKeys.VkDelete));
        Assert.Equal("ac", span.Value);
        Assert.Equal(1, span.Selection.Location);
    }

    [Fact]
    public void UnmodelledInputDropsTheEvidence()
    {
        foreach (var key in new[] { Map('Z', ctrl: true), Map('C', ctrl: true), Map('A', alt: true), Map('X', win: true), Map(LearningKeys.VkProcessKey),
                                   Map('K', typed: null), Map('K', typed: ""), Map(LearningKeys.VkDelete, ctrl: true), Map(LearningKeys.VkBack, alt: true) })
        {
            var span = new KeystrokeSpan("call acme");
            span.Apply(key);
            Assert.Equal(KeystrokeSpan.SpanState.Lost, span.State);
        }
    }

    [Fact]
    public void ReturnTabAndVerticalKeysDepartKeepingTheSpan()
    {
        foreach (var vk in new[] { LearningKeys.VkReturn, LearningKeys.VkTab, LearningKeys.VkEscape, LearningKeys.VkUp, LearningKeys.VkDown, LearningKeys.VkHome, LearningKeys.VkEnd, LearningKeys.VkPrior, LearningKeys.VkNext })
        {
            var span = new KeystrokeSpan("call acme");
            span.Apply(Map(vk));
            Assert.Equal(KeystrokeSpan.SpanState.Departed, span.State);
            Assert.Equal("call acme", span.Value);
        }
    }

    [Fact]
    public void AltGrTypesWhileCtrlOrAltAloneDoNot()
    {
        Assert.Equal(Type("€"), Map('E', ctrl: true, alt: true, typed: "€"));
        Assert.IsType<KeystrokeSpan.Key.Other>(Map('E', ctrl: true, typed: "e"));
    }

    [Fact]
    public void TranslatorIsNeverCalledForNavigationOrShortcuts()
    {
        int calls = 0;
        foreach (var (vk, ctrl) in new[] { (LearningKeys.VkLeft, false), (LearningKeys.VkReturn, false), ((int)'Z', true), (LearningKeys.VkBack, false) })
            LearningKeys.Map(vk, ctrl, false, false, false, () => { calls++; return "x"; });
        Assert.Equal(0, calls);
    }

    [Fact]
    public void ModifierKeysAreRecognised()
    {
        Assert.All(new[] { 0x10, 0x11, 0x12, 0x14, 0xA0, 0xA3, 0xA5, 0x5B }, vk => Assert.True(LearningKeys.IsModifier(vk)));
        Assert.False(LearningKeys.IsModifier('A'));
    }
}
