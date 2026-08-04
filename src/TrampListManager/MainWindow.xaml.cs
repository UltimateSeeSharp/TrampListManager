using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using TrampListManager.Services;

namespace TrampListManager;

public partial class MainWindow : Window
{
    private readonly WalkerFolder _folder = new();
    private readonly TrampListClient _client = new();
    private bool _backupOffered;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    // ---- Installing -------------------------------------------------------

    /// <summary>Entry point used by App for both .wbt files and tramplist:// links.</summary>
    public async Task HandleLaunchAsync(LaunchRequest request)
    {
        switch (request)
        {
            case LaunchRequest.InstallFile f:
                InstallFrom(f.Path, cleanUpSource: false);
                break;

            case LaunchRequest.InstallSlug s:
                await InstallFromSlugAsync(s.Slug);
                break;

            case LaunchRequest.Invalid i:
                Status(i.Reason, isError: true);
                break;
        }
    }

    private async Task InstallFromSlugAsync(string slug)
    {
        Status($"Fetching {slug}…");
        InstallButton.IsEnabled = false;

        try
        {
            var download = await _client.DownloadAsync(slug);
            if (!download.Success)
            {
                Status(download.Error!, isError: true);
                return;
            }

            // The temp file is ours alone, so it is removed once installed.
            InstallFrom(download.Path!, cleanUpSource: true, label: slug);
        }
        finally
        {
            InstallButton.IsEnabled = true;
        }
    }

    private void InstallFrom(string path, bool cleanUpSource, string? label = null)
    {
        // Offer a backup before the first write of the session: the alternative is a
        // mistake in the folder holding every design the player has built.
        OfferBackupOnce();

        var result = _folder.Install(path);

        if (cleanUpSource)
        {
            try { File.Delete(path); } catch { /* temp file; not worth reporting */ }
        }

        if (!result.Success)
        {
            Status(result.Error!, isError: true);
            return;
        }

        Refresh();
        SelectById(result.Id);

        var name = label ?? Path.GetFileNameWithoutExtension(path);
        Status($"Installed {name} as {result.Id.ToString()[..8]}… — restart SAND to see it.");
    }

    private void OfferBackupOnce()
    {
        if (_backupOffered || !_folder.Exists) return;
        _backupOffered = true;

        if (_folder.List().Count == 0) return;

        var answer = MessageBox.Show(
            "Back up your Walkers folder before installing?\n\n" +
            "This copies your existing Tramplers to a timestamped folder alongside the original. " +
            "Recommended the first time.",
            "TrampList Manager",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;

        try
        {
            var target = _folder.Backup();
            Status($"Backed up to {Path.GetFileName(target)}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Backup failed: {ex.Message}", "TrampList Manager",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---- Event handlers ---------------------------------------------------

    private async void OnInstallSlug(object sender, RoutedEventArgs e)
    {
        var slug = SlugParser.Extract(SlugInput.Text);
        if (slug is null)
        {
            Status("That doesn't look like a design ID or a TrampList link.", isError: true);
            return;
        }

        await InstallFromSlugAsync(slug);
        SlugInput.Clear();
    }

    private void OnSlugKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        OnInstallSlug(sender, e);
    }

    private void OnInstallFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a Trampler blueprint",
            Filter = "Trampler blueprint (*.wbt)|*.wbt|All files (*.*)|*.*",
            InitialDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
        };

        if (dialog.ShowDialog() == true) InstallFrom(dialog.FileName, cleanUpSource: false);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasWbtFile(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (!HasWbtFile(e)) return;

        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        foreach (var file in files.Where(f => f.EndsWith(".wbt", StringComparison.OrdinalIgnoreCase)))
            InstallFrom(file, cleanUpSource: false);
    }

    private static bool HasWbtFile(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) &&
        ((string[])e.Data.GetData(DataFormats.FileDrop)!)
            .Any(f => f.EndsWith(".wbt", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Opens the upload page and reveals the file, so the user can drag it into the form.
    /// The app deliberately holds no account and uploads nothing itself.
    /// </summary>
    private void OnShare(object sender, RoutedEventArgs e)
    {
        if (WalkerList.SelectedItem is not WalkerFile walker) return;

        OpenUrl(TrampListClient.UploadUrl);
        Process.Start("explorer.exe", $"/select,\"{walker.Path}\"");
        Status($"Opened the upload page — drag {walker.ShortId}… into the form.");
    }

    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var walker = WalkerList.SelectedItem as WalkerFile;
        ShareButton.IsEnabled = walker is not null;

        if (walker is not null)
            Status($"{walker.FileName}  ·  {walker.SizeText}  ·  saved {walker.ModifiedText}");
    }

    private void OnBackup(object sender, RoutedEventArgs e)
    {
        if (!_folder.Exists) { Status("No Walkers folder found.", isError: true); return; }

        try
        {
            var target = _folder.Backup();
            Status($"Backed up to {Path.GetFileName(target)}");
            Process.Start("explorer.exe", $"\"{target}\"");
        }
        catch (Exception ex)
        {
            Status($"Backup failed: {ex.Message}", isError: true);
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (!_folder.Exists) { Status("No Walkers folder found.", isError: true); return; }
        Process.Start("explorer.exe", $"\"{_folder.Path_}\"");
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();

    private void OnOpenSite(object sender, RoutedEventArgs e) => OpenUrl(TrampListClient.SiteUrl);

    private void OnToggleAssociation(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ShellIntegration.IsFileTypeRegistered())
            {
                ShellIntegration.Unregister();
                Status("Removed the .wbt file association.");
            }
            else
            {
                ShellIntegration.Register();
                Status("Registered .wbt files and tramplist:// links with this app.");
            }
            UpdateAssociationButton();
        }
        catch (Exception ex)
        {
            Status($"Could not change the association: {ex.Message}", isError: true);
        }
    }

    // ---- View state -------------------------------------------------------

    private void Refresh()
    {
        UpdateAssociationButton();

        if (!_folder.Exists)
        {
            WalkerList.ItemsSource = null;
            EmptyLabel.Text = "No Walkers folder found.\nIs SAND installed and has it been run once?";
            EmptyLabel.Visibility = Visibility.Visible;
            CountLabel.Text = "YOUR TRAMPLERS";
            return;
        }

        var walkers = _folder.List();
        WalkerList.ItemsSource = walkers;
        CountLabel.Text = $"YOUR TRAMPLERS  ({walkers.Count})";

        EmptyLabel.Text = "No Tramplers here yet.\nBuild one in SAND, or install a design above.";
        EmptyLabel.Visibility = walkers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SelectById(Guid id)
    {
        if (WalkerList.ItemsSource is not IEnumerable<WalkerFile> items) return;

        var match = items.FirstOrDefault(w => w.Id == id);
        if (match is null) return;

        WalkerList.SelectedItem = match;
        WalkerList.ScrollIntoView(match);
    }

    private void UpdateAssociationButton() =>
        AssocButton.Content = ShellIntegration.IsFileTypeRegistered()
            ? "Association: on"
            : "File association";

    private void Status(string message, bool isError = false)
    {
        StatusLabel.Text = message;
        StatusLabel.Foreground = (System.Windows.Media.Brush)FindResource(isError ? "Danger" : "Muted");
    }

    private static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
