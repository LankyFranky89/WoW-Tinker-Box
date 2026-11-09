using System.ComponentModel;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Avalonia.Media;

namespace WoWTinkerBox;

// ---------- Saved state and settings ----------

public class SourceInfo
{
    public string Repo { get; set; } = "";
    public string Tag { get; set; } = "";
}

public class AppState
{
    public string GamePath { get; set; } = "";
    public bool AutoBackup { get; set; } = true;
    public string GitHubToken { get; set; } = "";
    public string CurseForgeKey { get; set; } = "";
    public string WagoKey { get; set; } = "";
    public string? LastBuild { get; set; }
    public Dictionary<string, SourceInfo> Sources { get; set; } = new();

    [JsonIgnore] public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WoWTinkerBox");
    [JsonIgnore] static string FilePath => Path.Combine(Dir, "state.json");
    [JsonIgnore] public string AddOnsPath => Path.Combine(GamePath, "Interface", "AddOns");
    [JsonIgnore] public string WtfPath => Path.Combine(GamePath, "WTF");

    public static AppState Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppState>(File.ReadAllText(FilePath)) ?? new AppState();
        }
        catch { }
        return new AppState();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string? DetectGamePath()
    {
        string[] candidates =
        {
            @"C:\Program Files (x86)\World of Warcraft\_forever_",
            @"C:\Program Files\World of Warcraft\_forever_",
            @"D:\World of Warcraft\_forever_",
            @"D:\Games\World of Warcraft\_forever_",
            @"E:\World of Warcraft\_forever_",
            @"E:\Games\World of Warcraft\_forever_",
        };
        return candidates.FirstOrDefault(Directory.Exists);
    }

    /// <summary>If the user picked the WoW root folder, use its _forever_ subfolder.</summary>
    public static string NormalizeGamePath(string path)
    {
        path = path.Trim().TrimEnd('\\', '/');
        var sub = Path.Combine(path, "_forever_");
        return Directory.Exists(sub) ? sub : path;
    }
}

// ---------- Addon model ----------

public class Addon : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Folder { get; init; } = "";
    public string Title { get; init; } = "";
    public string Notes { get; init; } = "";
    public string Version { get; init; } = "";
    public string Interface { get; init; } = "";
    public bool ForeverOk { get; init; }
    public string? Repo { get; set; }
    public string? InstalledTag { get; set; }

    bool _selected;
    public bool Selected
    {
        get => _selected;
        set { _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); }
    }

    string? _latest;
    public string? LatestTag
    {
        get => _latest;
        set
        {
            _latest = value;
            foreach (var n in new[] { nameof(LatestTag), nameof(HasUpdate), nameof(StatusLabel), nameof(StatusBrush), nameof(VersionText) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }
    }

    public bool HasUpdate => Repo != null && LatestTag != null && LatestTag != InstalledTag;
    public string Initial => string.IsNullOrEmpty(Title) ? "?" : Title[..1].ToUpperInvariant();
    public string Subtitle =>
        (string.IsNullOrWhiteSpace(Notes) ? Folder : Notes) + " · " + (Repo != null ? Sources.Describe(Repo) : "Manual install");
    string Current => !string.IsNullOrWhiteSpace(InstalledTag) ? InstalledTag! : (string.IsNullOrWhiteSpace(Version) ? "—" : Version);
    public string VersionText => HasUpdate ? $"{Current} → {LatestTag}" : Current;

    public string StatusLabel =>
        !ForeverOk ? "⚠ Not marked for Forever"
        : Repo == null ? "Manual"
        : LatestTag == null ? "Not checked"
        : "✓ Up to date";

    public IBrush StatusBrush =>
        !ForeverOk ? Brush.Parse("#FF99A4")
        : (Repo != null && LatestTag != null) ? Brush.Parse("#6CCB5F")
        : Brush.Parse("#9A9A9A");
}

// ---------- .toc reading ----------

public static class Toc
{
    static readonly string[] Suffixes =
        { "Forever", "Camelot", "Mainline", "Standard", "Vanilla", "Classic", "TBC", "BCC", "Wrath", "WOTLKC", "Cata", "Mists" };

    /// <summary>"Details_Forever.toc" -> "Details"</summary>
    public static string BaseName(string tocFile)
    {
        var n = Path.GetFileNameWithoutExtension(tocFile);
        var i = Math.Max(n.LastIndexOf('_'), n.LastIndexOf('-'));
        if (i > 0 && Suffixes.Contains(n[(i + 1)..], StringComparer.OrdinalIgnoreCase))
            return n[..i];
        return n;
    }

    static string Clean(string s)
    {
        s = Regex.Replace(s, @"\|c[0-9a-fA-F]{8}", "");
        s = Regex.Replace(s, @"\|T[^|]*\|t", "");
        return s.Replace("|r", "").Trim();
    }

    public static Addon? Read(string dir)
    {
        var folder = Path.GetFileName(dir);
        var tocs = Directory.GetFiles(dir, "*.toc");
        if (tocs.Length == 0) return null;

        string? Pick(string suffix) => tocs.FirstOrDefault(t =>
            Path.GetFileNameWithoutExtension(t).Equals(folder + suffix, StringComparison.OrdinalIgnoreCase));

        var foreverToc = Pick("_Forever") ?? Pick("-Forever") ?? Pick("_Camelot") ?? Pick("-Camelot");
        var toc = foreverToc ?? Pick("_Mainline") ?? Pick("-Mainline") ?? Pick("") ?? tocs[0];

        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(toc))
        {
            var l = line.Trim();
            if (!l.StartsWith("##")) continue;
            var c = l.IndexOf(':');
            if (c < 0) continue;
            var key = l[2..c].Trim();
            if (!meta.ContainsKey(key)) meta[key] = l[(c + 1)..].Trim();
        }

        var iface = meta.GetValueOrDefault("Interface", "");
        bool ok = foreverToc != null
                  || meta.ContainsKey("Interface-Forever") || meta.ContainsKey("Interface-Camelot")
                  || iface.Split(',').Any(v => v.Trim().StartsWith("160"));

        return new Addon
        {
            Folder = folder,
            Title = Clean(meta.GetValueOrDefault("Title", folder)),
            Notes = Clean(meta.GetValueOrDefault("Notes", "")),
            Version = meta.GetValueOrDefault("Version", ""),
            Interface = iface,
            ForeverOk = ok,
        };
    }

    public static List<Addon> Scan(AppState st)
    {
        var list = new List<Addon>();
        if (!Directory.Exists(st.AddOnsPath)) return list;
        foreach (var d in Directory.GetDirectories(st.AddOnsPath))
        {
            var name = Path.GetFileName(d);
            if (name.StartsWith("Blizzard_", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var a = Read(d);
                if (a == null) continue;
                if (st.Sources.TryGetValue(name, out var s)) { a.Repo = s.Repo; a.InstalledTag = s.Tag; }
                list.Add(a);
            }
            catch { }
        }
        return list.OrderBy(a => a.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

// ---------- GitHub ----------

public record Release(string Tag, string Url);

public static class GitHub
{
    static readonly HttpClient Http = CreateClient();
    static string? _token;
    /// <summary>Optional token from Settings. Setting it clears an earlier "rejected" state.</summary>
    public static string? Token { get => _token; set { _token = value; TokenRejected = false; } }
    /// <summary>True when GitHub answered 401 to the saved token; requests then go without it.</summary>
    public static bool TokenRejected { get; private set; }

    static HttpClient CreateClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("WoWTinkerBox/1.0");
        return h;
    }

    public static string? NormalizeRepo(string input)
    {
        var s = input.Trim();
        s = Regex.Replace(s, @"^(https?://)?(www\.)?github\.com/", "", RegexOptions.IgnoreCase).TrimEnd('/');
        if (s.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        var p = s.Split('/');
        return p.Length >= 2 && p[0].Length > 0 && p[1].Length > 0 ? p[0] + "/" + p[1] : null;
    }

    static HttpRequestMessage Request(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (url.StartsWith("https://api.github.com"))
        {
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            if (!string.IsNullOrWhiteSpace(Token) && !TokenRejected)
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token.Trim());
        }
        return req;
    }

    /// <summary>Sends a GET; if GitHub rejects the saved token (401), retries once without it.</summary>
    static async Task<HttpResponseMessage> Send(string url, HttpCompletionOption option)
    {
        using (var req = Request(url))
        {
            var res = await Http.SendAsync(req, option).ConfigureAwait(false);
            bool usedToken = req.Headers.Authorization != null;
            if (res.StatusCode != System.Net.HttpStatusCode.Unauthorized || !usedToken) return res;
            res.Dispose();
        }
        TokenRejected = true;
        using var retry = Request(url);
        return await Http.SendAsync(retry, option).ConfigureAwait(false);
    }

    static async Task<JsonDocument?> GetJson(string url)
    {
        var res = await Send(url, HttpCompletionOption.ResponseContentRead).ConfigureAwait(false);
        using var _ = res;
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!res.IsSuccessStatusCode)
        {
            var code = (int)res.StatusCode;
            throw new Exception($"GitHub answered {code}" +
                                (code == 403 || code == 429 ? " (rate limit — add a GitHub token in Settings)" : ""));
        }
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    static readonly string[] OtherFlavors = { "classic", "vanilla", "bcc", "tbc", "wrath", "wotlk", "cata", "mists" };

    public static async Task<Release> Latest(string repo)
    {
        using (var rel = await GetJson($"https://api.github.com/repos/{repo}/releases/latest").ConfigureAwait(false))
        {
            if (rel != null)
            {
                var r = rel.RootElement;
                var tag = r.GetProperty("tag_name").GetString() ?? "release";
                var zips = r.GetProperty("assets").EnumerateArray()
                    .Select(a => (Name: a.GetProperty("name").GetString() ?? "", Url: a.GetProperty("browser_download_url").GetString() ?? ""))
                    .Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && a.Url.Length > 0)
                    .ToList();

                var pick = zips.FirstOrDefault(a => a.Name.Contains("forever", StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrEmpty(pick.Url))
                    pick = zips.FirstOrDefault(a => !OtherFlavors.Any(f => a.Name.Contains(f, StringComparison.OrdinalIgnoreCase)));
                if (string.IsNullOrEmpty(pick.Url) && zips.Count > 0)
                    pick = zips[0];

                var url = !string.IsNullOrEmpty(pick.Url) ? pick.Url : r.GetProperty("zipball_url").GetString()!;
                return new Release(tag, url);
            }
        }

        // No releases: use the newest commit on the default branch.
        using var c = await GetJson($"https://api.github.com/repos/{repo}/commits/HEAD").ConfigureAwait(false)
                      ?? throw new Exception($"Repository {repo} wasn't found on GitHub.");
        var sha = c.RootElement.GetProperty("sha").GetString()!;
        return new Release(sha[..7], $"https://api.github.com/repos/{repo}/zipball/{sha}");
    }

    public static async Task<string> Download(string url)
    {
        var file = Path.Combine(Path.GetTempPath(), "fam-" + Guid.NewGuid().ToString("N") + ".zip");
        using var res = await Send(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) throw new Exception($"Download failed ({(int)res.StatusCode}).");
        await using var fs = File.Create(file);
        await res.Content.CopyToAsync(fs).ConfigureAwait(false);
        return file;
    }
}

// ---------- WoWInterface ----------

public record WowiAddon(string Id, string Name, string Version, string Author, long Downloads);

public static class WowInterface
{
    const string Api = "https://api.mmoui.com/v3/game/WOW";
    static readonly HttpClient Http = CreateClient();
    static List<WowiAddon>? cache;

    static HttpClient CreateClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("WoWTinkerBox/1.0");
        return h;
    }

    /// <summary>Accepts wowi:12345, 12345, or a wowinterface.com link (info12345-Name.html, fileinfo.php?id=12345).</summary>
    public static string? NormalizeId(string input)
    {
        var s = input.Trim();
        var m = Regex.Match(s, @"^wowi:(\d+)$", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        if (s.Contains("wowinterface.com", StringComparison.OrdinalIgnoreCase))
        {
            m = Regex.Match(s, @"(?:info|[?&]id=)(\d+)", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value;
        }
        return Regex.IsMatch(s, @"^\d+$") ? s : null;
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";

    public static async Task<Release> Latest(string id)
    {
        var json = await Http.GetStringAsync($"{Api}/filedetails/{id}.json").ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var x = root.ValueKind == JsonValueKind.Array ? (root.GetArrayLength() > 0 ? root[0] : default) : root;
        if (x.ValueKind != JsonValueKind.Object) throw new Exception($"WoWInterface addon {id} wasn't found.");
        var url = Str(x, "UIDownload");
        if (url.Length == 0) url = $"https://cdn.wowinterface.com/downloads/getfile.php?id={id}";
        var ver = Str(x, "UIVersion");
        return new Release(ver.Length > 0 ? ver : Str(x, "UIDate"), url);
    }

    public static async Task<List<WowiAddon>> Search(string query)
    {
        if (cache == null)
        {
            var json = await Http.GetStringAsync($"{Api}/filelist.json").ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            cache = doc.RootElement.EnumerateArray().Select(x =>
            {
                long.TryParse(Str(x, "UIDownloadTotal"), out var dl);
                return new WowiAddon(Str(x, "UID"), Str(x, "UIName"), Str(x, "UIVersion"), Str(x, "UIAuthorName"), dl);
            }).Where(a => a.Id.Length > 0).ToList();
        }
        var q = query.Trim();
        return cache.Where(a => a.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(a => a.Downloads).Take(40).ToList();
    }
}

/// <summary>Source keys: "owner/repo" for GitHub, "wowi:12345" for WoWInterface.</summary>
public static class Sources
{
    public static bool IsWowi(string key) => key.StartsWith("wowi:", StringComparison.OrdinalIgnoreCase);

    public static Task<Release> Latest(string key) =>
        IsWowi(key) ? WowInterface.Latest(key[5..]) : GitHub.Latest(key);

    public static string Describe(string key) =>
        IsWowi(key) ? "WoWInterface #" + key[5..] : "GitHub: " + key;
}

// ---------- Installing ----------

public static class Installer
{
    public static async Task<List<string>> Install(AppState st, string repo, Release rel)
    {
        var zip = await GitHub.Download(rel.Url).ConfigureAwait(false);
        try { return InstallZip(st, zip, _ => new SourceInfo { Repo = repo, Tag = rel.Tag }); }
        finally { try { File.Delete(zip); } catch { } }
    }

    /// <summary>
    /// Installs a zip picked by the user: either a WoW Tinker Box addon zip (with wtb-pack.json)
    /// or a plain addon zip downloaded from any site.
    /// </summary>
    public static List<string> InstallLocalZip(AppState st, string zipPath)
    {
        Dictionary<string, SourceInfo>? manifest = null;
        using (var z = ZipFile.OpenRead(zipPath))
        {
            var entry = z.GetEntry(Packs.ManifestName);
            if (entry != null)
            {
                using var stream = entry.Open();
                manifest = JsonSerializer.Deserialize<Dictionary<string, SourceInfo>>(stream);
            }
        }
        return InstallZip(st, zipPath, name => manifest != null && manifest.TryGetValue(name, out var si) ? si : null);
    }

    static List<string> InstallZip(AppState st, string zipPath, Func<string, SourceInfo?> sourceFor)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "wtb-" + Guid.NewGuid().ToString("N"));
        try
        {
            ZipFile.ExtractToDirectory(zipPath, tmp);
            var found = new List<(string Dir, string Name)>();
            Find(tmp, found);
            if (found.Count == 0) throw new Exception("No addon (.toc file) was found in the zip.");

            Directory.CreateDirectory(st.AddOnsPath);
            foreach (var (dir, name) in found)
            {
                var target = Path.Combine(st.AddOnsPath, name);
                if (Directory.Exists(target)) Directory.Delete(target, true);
                CopyDir(dir, target);
                var src = sourceFor(name);
                if (src != null && !string.IsNullOrEmpty(src.Repo)) st.Sources[name] = src;
                else st.Sources.Remove(name);
            }
            st.Save();
            return found.Select(f => f.Name).ToList();
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    public static void Remove(AppState st, string folder)
    {
        var target = Path.Combine(st.AddOnsPath, folder);
        if (Directory.Exists(target)) Directory.Delete(target, true);
        st.Sources.Remove(folder);
        st.Save();
    }

    static void Find(string dir, List<(string Dir, string Name)> found)
    {
        var tocs = Directory.GetFiles(dir, "*.toc");
        if (tocs.Length > 0)
        {
            var folderName = Path.GetFileName(dir);
            var bases = tocs.Select(Toc.BaseName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var name = bases.FirstOrDefault(b => b.Equals(folderName, StringComparison.OrdinalIgnoreCase))
                       ?? bases.OrderBy(b => b.Length).First();
            found.Add((dir, name));
            return;
        }
        foreach (var d in Directory.GetDirectories(dir))
        {
            if (Path.GetFileName(d).StartsWith(".")) continue;
            Find(d, found);
        }
    }

    static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(src))
            CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }
}

// ---------- WTF backups ----------

public class BackupItem
{
    public string Name { get; init; } = "";
    public string Info { get; init; } = "";
    public string Path { get; init; } = "";
}

public static class Backups
{
    public const int Keep = 10;
    public static string Dir => Path.Combine(AppState.Dir, "backups");

    public static string? Create(AppState st, string reason, bool prune = true)
    {
        if (!Directory.Exists(st.WtfPath)) return null;
        Directory.CreateDirectory(Dir);
        var file = Path.Combine(Dir, $"WTF-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}-{reason}.zip");
        if (File.Exists(file)) file = file.Replace(".zip", $"-{Guid.NewGuid().ToString("N")[..4]}.zip");
        ZipFile.CreateFromDirectory(st.WtfPath, file);
        if (prune) Prune();
        return file;
    }

    public static List<FileInfo> Files() =>
        Directory.Exists(Dir)
            ? new DirectoryInfo(Dir).GetFiles("WTF-*.zip").OrderByDescending(f => f.Name, StringComparer.Ordinal).ToList()
            : new List<FileInfo>();

    public static void Prune()
    {
        foreach (var f in Files().Skip(Keep))
            try { f.Delete(); } catch { }
    }

    public static void Restore(AppState st, string file)
    {
        Create(st, "before-restore", prune: false);
        if (Directory.Exists(st.WtfPath)) Directory.Delete(st.WtfPath, true);
        ZipFile.ExtractToDirectory(file, st.WtfPath);
        Prune();
    }

    public static List<BackupItem> List() =>
        Files().Select(f => new BackupItem
        {
            Name = f.Name,
            Info = $"{f.Length / 1024.0 / 1024.0:0.0} MB",
            Path = f.FullName,
        }).ToList();
}

// ---------- Shareable packs ----------

public class PackEntry
{
    public string Folder { get; set; } = "";
    public string? Repo { get; set; }
}

public class Pack
{
    public string Name { get; set; } = "";
    public List<PackEntry> Addons { get; set; } = new();
}

public static class Packs
{
    const string Prefix = "WTB1:";
    public const string ManifestName = "wtb-pack.json";

    /// <summary>Zips the chosen addon folders plus a manifest so GitHub/WoWInterface addons keep updating.</summary>
    public static void ExportZip(AppState st, IEnumerable<Addon> addons, string file)
    {
        if (File.Exists(file)) File.Delete(file);
        var manifest = new Dictionary<string, SourceInfo>();
        using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
        foreach (var a in addons)
        {
            var dir = Path.Combine(st.AddOnsPath, a.Folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                var rel = a.Folder + "/" + Path.GetRelativePath(dir, f).Replace('\\', '/');
                zip.CreateEntryFromFile(f, rel, CompressionLevel.Optimal);
            }
            if (st.Sources.TryGetValue(a.Folder, out var src)) manifest[a.Folder] = src;
        }
        var entry = zip.CreateEntry(ManifestName);
        using var w = new StreamWriter(entry.Open());
        w.Write(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string Export(string name, IEnumerable<Addon> addons)
    {
        var pack = new Pack
        {
            Name = name,
            Addons = addons.Select(a => new PackEntry { Folder = a.Folder, Repo = a.Repo }).ToList(),
        };
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(pack));
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize))
            gz.Write(json, 0, json.Length);
        return Prefix + Convert.ToBase64String(ms.ToArray());
    }

    public static Pack Import(string code)
    {
        code = Regex.Replace(code ?? "", @"\s", "");
        if (!code.StartsWith(Prefix)) throw new Exception("That isn't a WoW Tinker Box pack code.");
        try
        {
            var bytes = Convert.FromBase64String(code[Prefix.Length..]);
            using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            gz.CopyTo(outMs);
            return JsonSerializer.Deserialize<Pack>(outMs.ToArray()) ?? throw new Exception();
        }
        catch
        {
            throw new Exception("The pack code is damaged. Copy it again.");
        }
    }
}

// ---------- Game build (for the patch-day checker) ----------

public static class GameBuild
{
    /// <summary>Reads the game version from the .build.info file in the WoW root folder.</summary>
    public static string? Read(string gamePath)
    {
        try
        {
            var root = Directory.GetParent(gamePath.TrimEnd('\\', '/'))?.FullName;
            if (root == null) return null;
            var file = Path.Combine(root, ".build.info");
            if (!File.Exists(file)) return null;

            var lines = File.ReadAllLines(file).Where(l => l.Length > 0).ToArray();
            if (lines.Length < 2) return null;
            var head = lines[0].Split('|').Select(h => h.Split('!')[0]).ToList();
            int vi = head.IndexOf("Version"), pi = head.IndexOf("Product");
            if (vi < 0) return null;

            var rows = lines.Skip(1).Select(l => l.Split('|')).Where(r => r.Length > vi).ToList();
            string[]? row = null;
            if (pi >= 0)
                row = rows.FirstOrDefault(r => r.Length > pi &&
                    (r[pi].Contains("forever", StringComparison.OrdinalIgnoreCase) ||
                     r[pi].Contains("camelot", StringComparison.OrdinalIgnoreCase)));
            row ??= rows.FirstOrDefault();
            return row?[vi];
        }
        catch { return null; }
    }
}
