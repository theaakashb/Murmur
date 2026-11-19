// Port of app/Delivery.swift.
namespace Murmur.Core;

/// <summary>Swift <c>Delivery.Result</c>.</summary>
public abstract record DeliveryResult<TValue>
{
    DeliveryResult() { }
    public sealed record PendingSaveFailed : DeliveryResult<TValue>;
    public sealed record Delivered(TValue Value) : DeliveryResult<TValue>;
    public sealed record StatusSaveFailed : DeliveryResult<TValue>;
}

/// <summary>What one insertion attempt reports: the status to store, and the value to hand back.</summary>
public readonly record struct Insertion<TValue>(string Status, TValue Value);

/// <summary>The insertion callback; it may change the entry (Swift passes it <c>inout</c>).</summary>
public delegate Insertion<TValue> InsertAction<TEntry, TValue>(ref TEntry entry);

/// <summary>The two writes bracket one nonthrowing insertion attempt. Never retry an uncertain paste.</summary>
public static class Delivery
{
    /// <param name="setStatus">Returns the entry with the given status string (the Swift key path).</param>
    public static DeliveryResult<TValue> Deliver<TEntry, TValue>(ref TEntry entry, Func<TEntry, string, TEntry> setStatus, string pending,
                                                                 Action<TEntry> save, InsertAction<TEntry, TValue> insert)
    {
        entry = setStatus(entry, pending);
        try { save(entry); } catch (Exception) { return new DeliveryResult<TValue>.PendingSaveFailed(); }
        var insertion = insert(ref entry);
        entry = setStatus(entry, insertion.Status);
        try { save(entry); } catch (Exception) { return new DeliveryResult<TValue>.StatusSaveFailed(); }
        return new DeliveryResult<TValue>.Delivered(insertion.Value);
    }

    public static string StatusSaveFailureMessage(string saved) =>
        $"{saved} Delivery status could not be saved. Check the destination before pasting again.";
}
