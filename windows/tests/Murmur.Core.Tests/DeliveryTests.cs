// Ports app/check_delivery.swift.
using Murmur.Core;

namespace Murmur.Core.Tests;

public class DeliveryTests
{
    sealed record Entry(string Status = "saved-not-inserted");

    [Theory]
    [InlineData(1, "verified")] [InlineData(1, "unverified")] [InlineData(1, "failed")]
    [InlineData(2, "verified")] [InlineData(2, "unverified")] [InlineData(2, "failed")]
    [InlineData(0, "verified")] [InlineData(0, "unverified")] [InlineData(0, "failed")]
    public void TheTwoWritesBracketOneInsertion(int failedSave, string insertionStatus)
    {
        var entry = new Entry();
        var events = new List<string>();
        int writes = 0, attempts = 0;
        var result = Delivery.Deliver(ref entry, (e, s) => e with { Status = s }, "insertion-pending", e =>
        {
            writes++; events.Add(e.Status);
            if (writes == failedSave) throw new IOException("disk");
        }, (ref Entry e) =>
        {
            attempts++;
            Assert.True(e.Status == "insertion-pending" && events.SequenceEqual(["insertion-pending"]));
            events.Add("insert");
            return new Insertion<string>(insertionStatus, "result");
        });
        switch (result)
        {
            case DeliveryResult<string>.PendingSaveFailed: Assert.True(failedSave == 1 && attempts == 0 && writes == 1); break;
            case DeliveryResult<string>.StatusSaveFailed: Assert.True(failedSave == 2 && attempts == 1 && writes == 2); break;
            case DeliveryResult<string>.Delivered delivered: Assert.True(failedSave == 0 && attempts == 1 && writes == 2 && delivered.Value == "result"); break;
            default: Assert.Fail("unexpected result");  break;
        }
        if (failedSave != 1)
        {
            Assert.Equal(insertionStatus, entry.Status);
            Assert.Equal(["insertion-pending", "insert", insertionStatus], events);
        }
    }

    [Fact]
    public void StatusSaveFailureMessageTellsTheUserToCheck() =>
        Assert.Equal("Saved. Delivery status could not be saved. Check the destination before pasting again.", Delivery.StatusSaveFailureMessage("Saved."));
}
