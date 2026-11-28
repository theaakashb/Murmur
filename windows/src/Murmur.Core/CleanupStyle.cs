// Port of app/SettingsView.swift and app/Controller+Settings.swift.
namespace Murmur.Core;

public enum CleanupStyle { Faithful, Natural, Polish, Professional }

public static class CleanupStyles
{
    public static bool NeedsModel(this CleanupStyle style) => style is CleanupStyle.Polish or CleanupStyle.Professional;

    public static CleanupOptions EffectiveOptions(this CleanupChoice route, CleanupOptions saved) =>
        route.RunsModelLevels() ? saved : saved with { Polish = false, Professional = false, TrustModel = false };

    public static DictationStyle EffectiveStyle(this CleanupChoice route, DictationStyle style) =>
        route.RunsModelLevels() ? style : style with
        {
            Options = route.EffectiveOptions(style.Options),
            Context = null,
            Instructions = "",
            Transform = null,
            UsesEngine = false,
        };

    public static CleanupStyle Current(CleanupChoice route, CleanupOptions saved)
    {
        var options = route.EffectiveOptions(saved);
        return !options.AnyEnabled ? CleanupStyle.Faithful
            : options.Professional ? CleanupStyle.Professional
            : options.Polish ? CleanupStyle.Polish : CleanupStyle.Natural;
    }

    public static (CleanupChoice Route, CleanupOptions Options) Select(CleanupStyle style, CleanupChoice route, CleanupOptions saved)
    {
        if (style == CleanupStyle.Faithful) return (route, CleanupOptions.Faithful);
        if (!saved.AnyEnabled) saved = CleanupOptions.Standard;
        // Polish and Professional need a model route: from Local rules they move to the Local model when it runs them, else to the Custom Provider.
        if (style.NeedsModel() && !route.RunsModelLevels()) route = LocalCleanupModels.Current.RunsModelLevels ? CleanupChoice.LocalModel : CleanupChoice.CustomAPI;
        return (route, saved with { Polish = style.NeedsModel(), Professional = style == CleanupStyle.Professional });
    }
}
