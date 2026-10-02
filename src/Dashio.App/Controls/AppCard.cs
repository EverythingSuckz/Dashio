using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace Dashio.App.Controls;

/// <summary>
/// A clickable settings card that assistive technology can also activate.
/// The toolkit's card responds to mouse and keyboard but does not offer the UI Automation
/// "invoke" action, which screen readers and automated tests rely on.
/// </summary>
public sealed partial class AppCard : SettingsCard
{
    public AppCard()
    {
        IsClickEnabled = true;
        Click += (_, e) => Activated?.Invoke(this, e);
    }

    /// <summary>Raised when the card is clicked, pressed with the keyboard, or invoked through UI Automation.</summary>
    public event RoutedEventHandler? Activated;

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private void RaiseActivated() => Activated?.Invoke(this, new RoutedEventArgs());

    private sealed partial class Peer(AppCard owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

        protected override string GetClassNameCore() => nameof(AppCard);

        protected override object? GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Invoke ? this : base.GetPatternCore(patternInterface);

        public void Invoke()
        {
            if (!IsEnabled())
                throw new ElementNotEnabledException();
            // Automation calls arrive off the UI thread.
            owner.DispatcherQueue.TryEnqueue(owner.RaiseActivated);
        }
    }
}
