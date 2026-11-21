using System.Reflection;
using System.Runtime.Loader;

namespace Murmur.Core.Tests;

/// <summary>Static initialisers run in first-use order, which a shared test process hides. Each case loads Murmur.Core into a
/// fresh load context (its own statics) and touches one type first, as the first dictation after launch does.</summary>
public class StaticInitOrderTests
{
    static Assembly FreshCore(out AssemblyLoadContext context)
    {
        context = new AssemblyLoadContext("fresh-core-" + Guid.NewGuid(), isCollectible: true);
        return context.LoadFromAssemblyPath(typeof(TransformGuard).Assembly.Location);
    }

    [Fact]
    public void TransformGuardFirstInAFreshProcessDoesNotThrow()
    {
        // VM 2026-11-21: a spoken Email/Prompt mode as the first dictation reached TransformGuard before RuleCleaner, and Icu
        // read RuleCleaner's half-built tables (null LetterAstral at Icu.Translate).
        var core = FreshCore(out var context);
        try
        {
            var accepts = core.GetType("Murmur.Core.TransformGuard", throwOnError: true)!.GetMethod("Accepts", [typeof(string), typeof(string)])!;
            var ok = (bool)accepts.Invoke(null, ["Tell Sam I'm late, no meeting on the 3rd.", "Hi Sam, I'm running late. No meeting on the 3rd."])!;
            Assert.True(ok);
        }
        finally { context.Unload(); }
    }

    [Fact]
    public void RuleCleanerFirstInAFreshProcessStillBuilds()
    {
        var core = FreshCore(out var context);
        try
        {
            var icuRegex = core.GetType("Murmur.Core.RuleCleaner", throwOnError: true)!.GetMethod("IcuRegex", BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.NotNull(icuRegex.Invoke(null, [@"\bword\b", System.Text.RegularExpressions.RegexOptions.None]));
        }
        finally { context.Unload(); }
    }
}
