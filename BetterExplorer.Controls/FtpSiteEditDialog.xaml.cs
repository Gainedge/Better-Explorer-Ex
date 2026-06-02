using Microsoft.UI.Xaml.Controls;

namespace BetterExplorer.Controls;

/// <summary>ContentDialog for creating or editing a single <see cref="FtpSiteEntry"/>.</summary>
public sealed partial class FtpSiteEditDialog : ContentDialog
{
    /// <summary>Set after the user clicks Save; null if cancelled.</summary>
    public FtpSiteEntry? ResultEntry { get; private set; }

    private readonly FtpSiteEntry? _existing;

    public FtpSiteEditDialog(FtpSiteEntry? existing)
    {
        _existing = existing;
        InitializeComponent();
        Title = existing == null ? "Add FTP Site" : "Edit FTP Site";
        PrimaryButtonClick += OnPrimaryButtonClick;

        if (existing != null)
            PopulateFields(existing);
    }

    private void PopulateFields(FtpSiteEntry entry)
    {
        DisplayNameBox.Text   = entry.DisplayName;
        HostBox.Text          = entry.Host;
        PortBox.Value         = entry.Port;
        UsernameBox.Text      = entry.Username;
        PasswordBox.Password  = FtpSiteDb.DecryptPassword(entry.EncryptedPassword);
        RemotePathBox.Text    = entry.RemotePath;
        AcceptAnyKeyBox.IsChecked = entry.AcceptAnyHostKey;
        FingerprintBox.Text   = entry.HostFingerprint;

        ProtocolBox.SelectedIndex = (int)entry.Protocol;
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Validate required fields.
        if (string.IsNullOrWhiteSpace(DisplayNameBox.Text) ||
            string.IsNullOrWhiteSpace(HostBox.Text))
        {
            args.Cancel = true;
            return;
        }

        ResultEntry = new FtpSiteEntry
        {
            Id                = _existing?.Id ?? 0,
            DisplayName       = DisplayNameBox.Text.Trim(),
            Protocol          = (FtpProtocol)(ProtocolBox.SelectedIndex >= 0 ? ProtocolBox.SelectedIndex : 0),
            Host              = HostBox.Text.Trim(),
            Port              = (int)PortBox.Value,
            Username          = UsernameBox.Text.Trim(),
            EncryptedPassword = FtpSiteDb.EncryptPassword(PasswordBox.Password),
            RemotePath        = string.IsNullOrWhiteSpace(RemotePathBox.Text) ? "/" : RemotePathBox.Text.Trim(),
            AcceptAnyHostKey  = AcceptAnyKeyBox.IsChecked == true,
            HostFingerprint   = FingerprintBox.Text.Trim(),
        };
    }
}
