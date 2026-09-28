using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Rdpeek.Companion.WinUI;

/// <summary>Compact per-connection status card, shared by the Overview rail and the dashboard overlay
/// (one template instead of several drifting copies). Set <see cref="Row"/> to the ConnectionRow.</summary>
public sealed partial class ConnectionCard : UserControl
{
    public ConnectionCard() => InitializeComponent();

    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
        nameof(Row), typeof(ConnectionRow), typeof(ConnectionCard), new PropertyMetadata(null));

    public ConnectionRow? Row
    {
        get => (ConnectionRow?)GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }
}
