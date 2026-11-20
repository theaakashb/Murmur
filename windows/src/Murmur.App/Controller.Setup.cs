namespace Murmur.App;

/// <summary>The welcome guide's test dictation (app/Controller+...: setupShowing on the Mac). While the guide's last step is in front, a dictation may
/// be pasted into Murmur's own window; otherwise Murmur never pastes into itself and shows the copy card instead.</summary>
internal sealed partial class Controller
{
    public bool SetupShowing { get; set; }
}
