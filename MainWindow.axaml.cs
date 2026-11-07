using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace WoWTinkerBox;

public partial class MainWindow : Window
{
    readonly AppState st = AppState.Load();
    readonly ObservableCollection<Addon> shown = new();
    readonly Dictionary<string, Release> latest = new(StringComparer.OrdinalIgnoreCase);
    List<Addon> all = new();
    bool busy;

    public MainWindow()
    {
        InitializeComponent();
        AddonList.ItemsSource = shown;
        GitHub.Token = st.GitHubToken;

        if (string.IsNullOrEmpty(st.GamePath))
        {
            var found = AppState.DetectGamePath();
            if (found != null) { st.GamePath = found; st.Save(); }
        }

        LoadSettingsUi();
        Nav.SelectedIndex = string.IsNullOrEmpty(st.GamePath) ? 4 : 0;
        Rescan();
        RefreshBackups();
        Opened += async (_, _) => await Startup();
    }

    // ---------- helpers ----------

    void Status(string text) => StatusBar.Text = text;

    async Task Run(Func<Task> work)
    {
        if (busy) { Status("Busy — wait for the current task to finish."); return; }
        busy = true;
        try { await work(); }
        catch (Exception ex) { Status("Error: " + ex.Message); }
        finally { busy = false; }
    }

    bool HasGameFolder()
    {
        if (!string.IsNullOrEmpty(st.GamePath) && Directory.Exists(st.GamePath)) return true;
        Status("Set your WoW Forever folder in Settings first.");
        Nav.SelectedIndex = 4;
        return false;
    }

    void Rescan()
    {
        all = Toc.Scan(st);
        if (string.IsNullOrEmpty(st.GamePath))
            FolderInfo.Text = "Set your WoW Forever folder in Settings.";
        else if (!Directory.Exists(st.AddOnsPath))
            FolderInfo.Text = $"AddOns folder not found: {st.AddOnsPath}";
        else
            FolderInfo.Text = $"{st.AddOnsPath} · {all.Count} addons";

        var build = string.IsNullOrEmpty(st.GamePath) ? null : GameBuild.Read(st.GamePath);
        GameInfo.Text = $"Game: WoW Forever\nBuild: {build ?? "unknown"}";
        ApplyLatest();
    }

    void ApplyLatest()
    {
        foreach (var a in all)
            if (a.Repo != null && latest.TryGetValue(a.Repo, out var r))
                a.LatestTag = r.Tag;
        ApplyFilter();
    }

    void ApplyFilter()
    {
        var q = Search.Text?.Trim() ?? "";
        shown.Clear();
        foreach (var a in all)
            if (q.Length == 0
                || a.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || a.Folder.Contains(q, StringComparison.OrdinalIgnoreCase))
                shown.Add(a);
        var n = all.Where(a => a.HasUpdate).Select(a => a.Repo).Distinct().Count();
        UpdateAllBtn.Content = n > 0 ? $"Update all ({n})" : "Update all";
    }

    void RefreshBackups() => BackupList.ItemsSource = Backups.List();

    async Task BackupBeforeUpdate()
    {
        if (!st.AutoBackup) return;
        Status("Backing up WTF settings…");
        await Task.Run(() => Backups.Create(st, "before-update"));
        RefreshBackups();
    }

    // ---------- startup and patch day ----------

    async Task Startup()
    {
        if (string.IsNullOrEmpty(st.GamePath)) return;
        await Run(async () =>
        {
            var build = GameBuild.Read(st.GamePath);
            bool patchDay = build != null && st.LastBuild != null && build != st.LastBuild;
            var oldBuild = st.LastBuild;

            if (patchDay)
            {
                PatchText.Text = $"Patch day: the game updated from {oldBuild} to {build}. Checking your addons…";
                PatchBanner.IsVisible = true;
            }

            await CheckUpdates();

            if (patchDay)
            {
                int upd = all.Where(a => a.HasUpdate).Select(a => a.Repo).Distinct().Count();
                int bad = all.Count(a => !a.ForeverOk);
                PatchText.Text = $"Patch day: the game updated from {oldBuild} to {build}. " +
                                 $"{upd} addon(s) have updates and {bad} aren't marked for Forever. " +
                                 "Addons without a fix may break until their authors update them.";
            }

            if (build != null) { st.LastBuild = build; st.Save(); }
        });
    }

    async Task CheckUpdates()
    {
        var repos = all.Where(a => a.Repo != null).Select(a => a.Repo!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (repos.Count == 0) { Status("No GitHub or WoWInterface addons to check. Manual installs can't be checked."); return; }

        int i = 0, failed = 0;
        foreach (var repo in repos)
        {
            Status($"Checking {repo} ({++i}/{repos.Count})…");
            try { latest[repo] = await Sources.Latest(repo); }
            catch (Exception ex) { failed++; Status($"{repo}: {ex.Message}"); }
        }
        ApplyLatest();
        int n = all.Where(a => a.HasUpdate).Select(a => a.Repo).Distinct().Count();
        Status($"Checked {repos.Count} source(s): {n} update(s) available" + (failed > 0 ? $", {failed} failed" : "") + ".");
    }

    async Task UpdateRepos(IReadOnlyList<string> repos)
    {
        if (repos.Count == 0) { Status("Everything is up to date."); return; }
        await BackupBeforeUpdate();
        int i = 0;
        foreach (var repo in repos)
        {
            Status($"Updating {repo} ({++i}/{repos.Count})…");
            if (!latest.TryGetValue(repo, out var rel))
                latest[repo] = rel = await Sources.Latest(repo);
            await Installer.Install(st, repo, rel);
        }
        Rescan();
        Status($"Updated {repos.Count} addon source(s).");
    }

    // ---------- navigation ----------

    void Nav_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        Control[] pages = { PageInstalled, PageInstall, PageBackups, PagePacks, PageSettings };
        for (int i = 0; i < pages.Length; i++)
            pages[i].IsVisible = i == Nav.SelectedIndex;
        if (Nav.SelectedIndex == 2) RefreshBackups();
    }

    // ---------- installed page ----------

    void Search_TextChanged(object? sender, TextChangedEventArgs e) => ApplyFilter();

    void DismissBanner_Click(object? sender, RoutedEventArgs e) => PatchBanner.IsVisible = false;

    void Rescan_Click(object? sender, RoutedEventArgs e)
    {
        Rescan();
        Status("Rescanned the AddOns folder.");
    }

    async void Check_Click(object? sender, RoutedEventArgs e)
    {
        if (!HasGameFolder()) return;
        await Run(CheckUpdates);
    }

    async void UpdateAll_Click(object? sender, RoutedEventArgs e)
    {
        if (!HasGameFolder()) return;
        var repos = all.Where(a => a.HasUpdate).Select(a => a.Repo!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        await Run(() => UpdateRepos(repos));
    }

    async void RowUpdate_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is Addon { Repo: not null } a)
            await Run(() => UpdateRepos(new[] { a.Repo! }));
    }

    void RowRemove_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not Addon a) return;
        if (btn.Content as string != "Confirm")
        {
            btn.Content = "Confirm";
            Status($"Click Confirm to remove {a.Title}.");
            return;
        }
        try
        {
            Installer.Remove(st, a.Folder);
            Rescan();
            Status($"Removed {a.Title}.");
        }
        catch (Exception ex) { Status("Error: " + ex.Message); }
    }

    // ---------- install page ----------

    async void Install_Click(object? sender, RoutedEventArgs e)
    {
        if (!HasGameFolder()) { Nav.SelectedIndex = 4; return; }
        var input = RepoBox.Text ?? "";
        var wowiId = WowInterface.NormalizeId(input);
        var repo = wowiId != null ? "wowi:" + wowiId : GitHub.NormalizeRepo(input);
        if (repo == null) { InstallResult.Text = "Enter a GitHub repository (owner/repo) or a WoWInterface link."; return; }
        await InstallSource(repo);
    }

    async Task InstallSource(string repo)
    {
        if (!HasGameFolder()) return;
        await Run(async () =>
        {
            InstallResult.Text = "";
            Status($"Finding the latest version of {Sources.Describe(repo)}…");
            var rel = await Sources.Latest(repo);
            Status($"Installing {Sources.Describe(repo)} {rel.Tag}…");
            var names = await Installer.Install(st, repo, rel);
            latest[repo] = rel;
            Rescan();

            var notForever = all.Where(a => names.Contains(a.Folder) && !a.ForeverOk).Select(a => a.Title).ToList();
            InstallResult.Text = $"Installed {string.Join(", ", names)} ({rel.Tag})." +
                                 (notForever.Count > 0
                                     ? $" Note: {string.Join(", ", notForever)} isn't marked for Forever and may not load."
                                     : "");
            RepoBox.Text = "";
            Status("Install finished. Type /reload in game if WoW is running.");
        });
    }

    async void WowiSearch_Click(object? sender, RoutedEventArgs e)
    {
        var q = WowiQuery.Text?.Trim() ?? "";
        if (q.Length < 2) { WowiInfo.Text = "Type at least 2 letters."; return; }
        await Run(async () =>
        {
            Status("Searching WoWInterface…");
            var results = await WowInterface.Search(q);
            WowiResults.ItemsSource = results;
            WowiInfo.Text = results.Count == 0 ? "No addons found." : $"{results.Count} result(s). Check that an addon supports Forever before installing.";
            Status("Search finished.");
        });
    }

    async void WowiInstall_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string id)
        {
            Nav.SelectedIndex = 1;
            await InstallSource("wowi:" + id);
        }
    }

    // ---------- backups page ----------

    async void BackupNow_Click(object? sender, RoutedEventArgs e)
    {
        if (!HasGameFolder()) return;
        await Run(async () =>
        {
            Status("Backing up WTF settings…");
            var file = await Task.Run(() => Backups.Create(st, "manual"));
            RefreshBackups();
            Status(file == null ? "No WTF folder found to back up." : $"Backup saved: {Path.GetFileName(file)}");
        });
    }

    async void Restore_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string file) return;
        if (!HasGameFolder()) return;
        if (btn.Content as string != "Confirm")
        {
            btn.Content = "Confirm";
            Status("Close WoW, then click Confirm to restore. Your current settings are backed up first.");
            return;
        }
        await Run(async () =>
        {
            Status("Restoring WTF settings…");
            await Task.Run(() => Backups.Restore(st, file));
            RefreshBackups();
            Status($"Restored {Path.GetFileName(file)}.");
        });
    }

    // ---------- packs page ----------

    void ExportPack_Click(object? sender, RoutedEventArgs e)
    {
        if (all.Count == 0) { Status("No addons installed to share."); return; }
        var name = string.IsNullOrWhiteSpace(PackName.Text) ? "My addons" : PackName.Text.Trim();
        PackCode.Text = Packs.Export(name, all);
        int manual = all.Count(a => a.Repo == null);
        Status($"Pack code created with {all.Count} addons" +
               (manual > 0 ? $" ({manual} manual installs will be listed for your friend to get by hand)." : "."));
    }

    async void CopyPack_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(PackCode.Text)) { Status("Create a pack code first."); return; }
        if (Clipboard != null)
        {
            await Clipboard.SetTextAsync(PackCode.Text);
            Status("Pack code copied.");
        }
    }

    async void ImportPack_Click(object? sender, RoutedEventArgs e)
    {
        if (!HasGameFolder()) return;
        Pack pack;
        try { pack = Packs.Import(ImportCode.Text ?? ""); }
        catch (Exception ex) { ImportResult.Text = ex.Message; return; }

        await Run(async () =>
        {
            var repos = pack.Addons.Where(a => a.Repo != null).Select(a => a.Repo!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var manual = pack.Addons.Where(a => a.Repo == null).Select(a => a.Folder).ToList();
            var failed = new List<string>();

            if (repos.Count > 0) await BackupBeforeUpdate();
            int i = 0;
            foreach (var repo in repos)
            {
                Status($"Installing {repo} ({++i}/{repos.Count})…");
                try
                {
                    var rel = await Sources.Latest(repo);
                    await Installer.Install(st, repo, rel);
                    latest[repo] = rel;
                }
                catch (Exception ex) { failed.Add($"{repo} ({ex.Message})"); }
            }
            Rescan();

            var text = $"Pack \"{pack.Name}\": installed {repos.Count - failed.Count} of {repos.Count} addon source(s).";
            if (failed.Count > 0) text += "\nFailed: " + string.Join(", ", failed);
            if (manual.Count > 0) text += "\nGet these by hand (no online source): " + string.Join(", ", manual);
            ImportResult.Text = text;
            Status("Pack install finished.");
        });
    }

    // ---------- settings page ----------

    void LoadSettingsUi()
    {
        GameBox.Text = st.GamePath;
        AutoBackupBox.IsChecked = st.AutoBackup;
        TokenBox.Text = st.GitHubToken;
        CfBox.Text = st.CurseForgeKey;
        WagoBox.Text = st.WagoKey;
    }

    async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose your WoW Forever (_forever_) folder",
            AllowMultiple = false,
        });
        var path = result.Count > 0 ? result[0].TryGetLocalPath() : null;
        if (path != null) GameBox.Text = AppState.NormalizeGamePath(path);
    }

    void SaveSettings_Click(object? sender, RoutedEventArgs e)
    {
        var path = AppState.NormalizeGamePath(GameBox.Text ?? "");
        if (path.Length > 0 && !Directory.Exists(path))
        {
            SettingsResult.Text = "That folder doesn't exist. Check the path.";
            return;
        }
        bool pathChanged = path != st.GamePath;
        st.GamePath = path;
        GameBox.Text = path;
        st.AutoBackup = AutoBackupBox.IsChecked == true;
        st.GitHubToken = TokenBox.Text?.Trim() ?? "";
        st.CurseForgeKey = CfBox.Text?.Trim() ?? "";
        st.WagoKey = WagoBox.Text?.Trim() ?? "";
        if (pathChanged) st.LastBuild = null;
        st.Save();
        GitHub.Token = st.GitHubToken;
        Rescan();
        SettingsResult.Text = "Settings saved.";
        Status(Directory.Exists(st.AddOnsPath)
            ? $"Found {all.Count} addons."
            : "Saved. No AddOns folder yet — it's created when you install your first addon.");
    }
}
