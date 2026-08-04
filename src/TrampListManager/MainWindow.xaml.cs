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
    private readonly LabelStore _labels = new();
    private bool _backupOffered;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
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
        if (WalkerList.SelectedItem is not WalkerRow row) return;

        // Already published: no reason to upload it again.
        if (row.Design is not null)
        {
            OpenUrl(row.Design.Url);
            Status($"\"{row.Design.Name}\" is already on TrampList.");
            return;
        }

        OpenUrl(TrampListClient.UploadUrl);
        Process.Start("explorer.exe", $"/select,\"{row.File.Path}\"");
        Status($"Opened the upload page — drag {row.Title} into the form.");
    }

    private void OnOpenDesignPage(object sender, RoutedEventArgs e)
    {
        if (WalkerList.SelectedItem is WalkerRow { Design: not null } row)
            OpenUrl(row.Design.Url);
    }

    /// <summary>Labels a design locally. Only meaningful for ones not on TrampList.</summary>
    private void OnRename(object sender, RoutedEventArgs e)
    {
        if (WalkerList.SelectedItem is not WalkerRow row) return;

        var dialog = new RenameDialog(row.Label?.Name, row.Label?.Notes) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        _labels.Set(row.File.Id, dialog.DesignName, dialog.Notes);
        Refresh();
    }

    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var row = WalkerList.SelectedItem as WalkerRow;

        ShareButton.IsEnabled = row is not null;
        OpenPageButton.IsEnabled = row?.Design is not null;
        // A published design takes its name from the site, so a local label would be ignored.
        RenameButton.IsEnabled = row is not null && row.Design is null;
        ShareButton.Content = row?.Design is not null ? "View on TrampList" : "Share this design…";

        if (row is not null)
            Status($"{row.File.FileName}  ·  {row.SizeText}  ·  saved {row.ModifiedText}");
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
                Status("Done — .wbt files no longer open with TrampList Manager.");
            }
            else
            {
                ShellIntegration.Register();
                Status("Done — double-click a downloaded .wbt to install it.");
            }
            UpdateAssociationButton();
        }
        catch (Exception ex)
        {
            Status($"Could not change the association: {ex.Message}", isError: true);
        }
    }

    // ---- View state -------------------------------------------------------

    /// <summary>
    /// Rebuilds the list, then asks TrampList to identify the files by hash.
    ///
    /// The rows render immediately from local data and are upgraded in place once the
    /// lookup returns, so a slow or unreachable site costs the user nothing.
    /// </summary>
    private async Task RefreshAsync()
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
        _labels.Prune(walkers.Select(w => w.Id));

        var rows = walkers
            .Select(w => new WalkerRow(w, null, _labels.Get(w.Id)))
            .ToList();

        WalkerList.ItemsSource = rows;
        CountLabel.Text = $"YOUR TRAMPLERS  ({rows.Count})";
        EmptyLabel.Text = "No Tramplers here yet.\nBuild one in SAND, or install a design above.";
        EmptyLabel.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (rows.Count == 0) return;

        // Hashing reads every file, so keep it off the UI thread.
        Status("Identifying designs…");
        var hashes = await Task.Run(() =>
            rows.ToDictionary(r => r.File.Path, r => r.File.ComputeSha256()));

        var resolved = await _client.ResolveAsync(hashes.Values);

        var merged = rows
            .Select(r => resolved.TryGetValue(hashes[r.File.Path], out var info)
                ? r with { Design = info }
                : r)
            .ToList();

        WalkerList.ItemsSource = merged;

        var known = merged.Count(r => r.IsPublished);
        Status(known > 0
            ? $"{known} of {merged.Count} recognised from TrampList."
            : "None of these are on TrampList yet — click Rename to label them.");
    }

    /// <summary>Synchronous entry point for callers that cannot await.</summary>
    private async void Refresh() => await RefreshAsync();

    private void SelectById(Guid id)
    {
        if (WalkerList.ItemsSource is not IEnumerable<WalkerRow> items) return;

        var match = items.FirstOrDefault(w => w.File.Id == id);
        if (match is null) return;

        WalkerList.SelectedItem = match;
        WalkerList.ScrollIntoView(match);
    }

    /// <summary>
    /// The button says what clicking it will do, not what the setting is called —
    /// "file association" means nothing to someone who just wants double-click to work.
    /// </summary>
    private void UpdateAssociationButton()
    {
        var on = ShellIntegration.IsFileTypeRegistered();

        AssocButton.Content = on
            ? "Stop opening .wbt files"
            : "Open .wbt files with this app";

        AssocButton.ToolTip = on
            ? "Double-clicking a .wbt file currently opens TrampList Manager.\n"
              + "Click to undo that — files will open with whatever Windows picks instead."
            : "Lets you install a design by double-clicking the .wbt you downloaded,\n"
              + "instead of choosing it here. Only affects your Windows account, and\n"
              + "you can turn it off again at any time.";
    }

    private void Status(string message, bool isError = false)
    {
        StatusLabel.Text = message;
        StatusLabel.Foreground = (System.Windows.Media.Brush)FindResource(isError ? "Danger" : "Muted");
    }

    private static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
