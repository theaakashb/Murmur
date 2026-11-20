using Murmur.Platform;
using Xunit;

namespace Murmur.Platform.Tests;

public sealed class MicrophonePrivacyTests
{
    [Theory]
    [InlineData("Allow", "Allow", "Allow", MicrophonePrivacy.State.Allowed)]
    [InlineData("Allow", "Deny", "Allow", MicrophonePrivacy.State.Blocked)]
    [InlineData("Allow", "Allow", "Deny", MicrophonePrivacy.State.Blocked)]
    [InlineData("Deny", null, null, MicrophonePrivacy.State.Blocked)]
    [InlineData(null, null, null, MicrophonePrivacy.State.Unknown)]
    [InlineData(null, "allow", null, MicrophonePrivacy.State.Allowed)]
    public void AnyDenyBlocksAndAnyAllowPermits(string? machine, string? user, string? desktop, MicrophonePrivacy.State expected) =>
        Assert.Equal(expected, MicrophonePrivacy.Decide(machine, user, desktop));

    [Fact]
    public void ReadNeverThrows() => _ = MicrophonePrivacy.Read();
}
