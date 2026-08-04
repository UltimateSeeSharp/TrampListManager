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
        EnsureShellIntegration();
        Loaded += async (_, _) => await RefreshAsync();
    }

    /// <summary>
    /// Claims .wbt files and tramplist:// links on every start.
    ///
    /// Normally seizing a file type without asking is hostile, but nothing else on a
    /// Windows machine opens a .wbt — the extension belongs to this game — so there is no
    /// association to trample, and double-clicking a download is the whole point of the
    /// app. Re-registered each run so a Windows update or a reinstall elsewhere cannot
    /// quietly break the handler.
    ///
    /// Failure is ignored: the registry write is per-user and should not fail, but if it
    /// does the app still works through its own install button.
    /// </summary>
    private static void EnsureShellIntegration()
    {
        try
        {
            ShellIntegration.Register();
        }
        catch
        {
            // Not worth interrupting startup over.
        }
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

        var result = _folder.Install(path, AskAboutDuplicate);

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

    /// <summary>
    /// Asked when the design is already installed. Both answers are legitimate — replacing
    /// is what you want after an author updates a design, adding a copy is what you want to
    /// keep an original and tinker with a variant — so the app does not guess.
    /// </summary>
    private DuplicateChoice AskAboutDuplicate(Guid id)
    {
        var existing = _labels.Get(id)?.Name ?? $"{id.ToString()[..8]}…";

        var answer = MessageBox.Show(
            $"""
            You already have this design installed ({existing}).

            Replace it with the downloaded copy?

            Yes — replace it.
            No — keep both, installing this as a separate design.
            """,
            "Already installed",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        return answer switch
        {
            MessageBoxResult.Yes => DuplicateChoice.Replace,
            MessageBoxResult.No => DuplicateChoice.AddCopy,
            _ => DuplicateChoice.Cancel
        };
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
    /// Sends the blueprint to TrampList and opens the upload page with it already attached.
    ///
    /// The app still holds no account: it parks the file and gets a short-lived token, and
    /// the browser — where the user is signed in with Steam — does the publishing. All the
    /// user adds is screenshots and the description.
    /// </summary>
    private async void OnShare(object sender, RoutedEventArgs e)
    {
        if (WalkerList.SelectedItem is not WalkerRow row) return;

        // Already published: no reason to upload it again.
        if (row.Design is not null)
        {
            OpenUrl(row.Design.Url);
            Status($"\"{row.Design.Name}\" is already on TrampList.");
            return;
        }

        ShareButton.IsEnabled = false;
        Status($"Uploading {row.Title} to TrampList…");

        try
        {
            var result = await _client.StageAsync(row.File.Path);

            if (result.ExistingSlug is { } slug)
            {
                // Published by someone else, or by this user before the list was refreshed.
                OpenUrl(TrampListClient.DesignUrl(slug));
                Status("That design is already on TrampList — opened its page.");
                Refresh();
                return;
            }

            if (!result.Success)
            {
                // Falls back to the manual route, which still works. Said plainly rather
                // than quietly: the browser is about to open an empty form, and without
                // being told why, that looks like the upload simply did nothing.
                Status($"{result.Error} Opening the form for you to attach it by hand.", isError: true);
                OpenUrl(TrampListClient.UploadUrl);
                Process.Start("explorer.exe", $"/select,\"{row.File.Path}\"");
                return;
            }

            OpenUrl(result.UploadUrl!);
            Status($"Uploaded {row.Title} — add screenshots and details in your browser.");
        }
        finally
        {
            // Selection may have changed while the upload ran, so re-derive rather than
            // unconditionally re-enabling.
            ShareButton.IsEnabled = WalkerList.SelectedItem is WalkerRow;
        }
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
        ShareButton.Content = row?.Design is not null ? "View on TrampList" : "Upload to TrampList…";

        if (row is not null)
            Status($"{row.File.FileName}  ·  {row.SizeText}  ·  saved {row.ModifiedText}");
    }

    private void OnBackup(object sender, RoutedEventArgs e)
    {
        if (!_folder.Exists) { Status("Nothing to back up yet — you have no saved designs."); return; }

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
        // Create on demand: a player who has never saved a design has no folder yet, and
        // refusing to open it is less useful than making it.
        try
        {
            System.IO.Directory.CreateDirectory(_folder.Path_);
        }
        catch (Exception ex)
        {
            Status($"Could not open the Walkers folder: {ex.Message}", isError: true);
            return;
        }

        Process.Start("explorer.exe", $"\"{_folder.Path_}\"");
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();

    /// <summary>
    /// Fits the columns to the viewport.
    ///
    /// GridView has no "fill remaining space" width, so fixed widths either overflow —
    /// producing a horizontal scrollbar over content that would otherwise fit — or leave
    /// dead space on the right. The fixed columns keep their natural size and the two
    /// text columns share what is left.
    /// </summary>
    private void OnListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;

        const double Source = 90, Size = 80, Date = 110;
        // Border, padding, and the vertical scrollbar once the list is long enough.
        var chrome = 4 + (WalkerList.Items.Count > 12 ? 18 : 0);

        var free = WalkerList.ActualWidth - (Source + Size + Date) - chrome;
        if (free < 200) return; // Too narrow to be worth reflowing.

        ColDesign.Width = Math.Round(free * 0.42);
        ColDetail.Width = Math.Round(free * 0.58);
        ColSource.Width = Source;
        ColSize.Width = Size;
        ColDate.Width = Date;
    }

    /// <summary>Double-click opens the design page when there is one, else offers a label.</summary>
    private void OnRowActivated(object sender, MouseButtonEventArgs e)
    {
        if (WalkerList.SelectedItem is not WalkerRow row) return;
        if (row.Design is not null) OpenUrl(row.Design.Url);
        else OnRename(sender, e);
    }

    private void OnOpenSite(object sender, RoutedEventArgs e) => OpenUrl(TrampListClient.SiteUrl);

    // ---- View state -------------------------------------------------------

    /// <summary>
    /// Rebuilds the list, then asks TrampList to identify the files by hash.
    ///
    /// The rows render immediately from local data and are upgraded in place once the
    /// lookup returns, so a slow or unreachable site costs the user nothing.
    /// </summary>
    private async Task RefreshAsync()
    {
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

    private void Status(string message, bool isError = false)
    {
        StatusLabel.Text = message;
        StatusLabel.Foreground = (System.Windows.Media.Brush)FindResource(isError ? "Danger" : "Muted");
    }

    private static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
