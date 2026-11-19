// Port of app/CleanupGuard.swift (partial: the guard port adds the rest of the class).
using System.Collections.Frozen;

namespace Murmur.Core;

public static partial class CleanupGuard
{
    /// <summary>Closed-class words a polish grammar fix may add or drop. Not negations, numbers or modals.</summary>
    public static readonly FrozenSet<string> GrammarWords = CleanupText.Data.GrammarWords.ToFrozenSet();
}
