"""Builds catalog/index.json (Frank's Pack) from the latest release of each repo in addons.txt."""
import datetime, json, os, re, sys, urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
TOKEN = os.environ.get("GITHUB_TOKEN", "")


def get(url, raw=False):
    req = urllib.request.Request(url, headers={"User-Agent": "WoWTinkerBox-catalog"})
    if TOKEN and "api.github.com" in url:
        req.add_header("Authorization", "Bearer " + TOKEN)
        req.add_header("Accept", "application/vnd.github+json")
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            data = r.read().decode("utf-8", "replace")
            return data if raw else json.loads(data)
    except urllib.error.HTTPError as e:
        if e.code == 404:
            return None
        raise


def toc_fields(repo, ref, folder):
    text = get(f"https://raw.githubusercontent.com/{repo}/{ref}/{folder}.toc", raw=True) or ""
    fields = {}
    for line in text.splitlines():
        m = re.match(r"##\s*([\w-]+)\s*:\s*(.*)", line)
        if m:
            fields[m.group(1).lower()] = re.sub(r"\|c[0-9a-fA-F]{8}|\|r", "", m.group(2)).strip()
    return fields


def latest_release(repo):
    rels = get(f"https://api.github.com/repos/{repo}/releases?per_page=20") or []
    rels = [r for r in rels if not r.get("draft")]
    zips = lambda r: [a for a in r.get("assets", []) if a["name"].lower().endswith(".zip")]
    stable = [r for r in rels if not r.get("prerelease") and zips(r)]
    beta = [r for r in rels if r.get("prerelease") and zips(r)]
    return (stable or beta or [None])[0]


def main():
    repos = [l.strip() for l in open(os.path.join(HERE, "addons.txt")) if l.strip() and not l.startswith("#")]
    out, problems = [], []
    for repo in repos:
        rel = latest_release(repo)
        if not rel:
            problems.append(f"{repo}: no release with a zip yet")
            continue
        asset = next(a for a in rel["assets"] if a["name"].lower().endswith(".zip"))
        folder = re.sub(r"-[^-]+-forever\.zip$", "", asset["name"], flags=re.I)
        if folder.lower().endswith(".zip"):
            folder = folder[:-4]
        toc = toc_fields(repo, rel["tag_name"], folder)
        out.append({
            "repo": repo,
            "folder": folder,
            "title": toc.get("title", folder),
            "notes": toc.get("notes", ""),
            "tag": rel["tag_name"],
            "beta": bool(rel.get("prerelease")),
            "url": asset["browser_download_url"],
            "published": rel.get("published_at"),
        })
    index = {
        "name": "Frank's Pack",
        "updated": datetime.datetime.now(datetime.timezone.utc).replace(microsecond=0).isoformat(),
        "addons": sorted(out, key=lambda a: a["title"].lower()),
    }
    path = os.path.join(HERE, "index.json")
    old = json.load(open(path)) if os.path.exists(path) else {}
    if old.get("addons") == index["addons"]:
        print("No changes.")
    else:
        with open(path, "w") as f:
            json.dump(index, f, indent=2)
            f.write("\n")
        print(f"Wrote {len(out)} addons.")
    for p in problems:
        print("::warning::" + p)


if __name__ == "__main__":
    sys.exit(main())
