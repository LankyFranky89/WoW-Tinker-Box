"""Builds catalog/index.json (Frank's Pack).

Every public, non-fork repo of OWNER with a .toc file in its root is in the pack,
except the ones listed in skip.txt. If the repo has a release for the current .toc
version, that zip is used; otherwise the newest commit is downloaded as a zip.
"""
import datetime, json, os, re, sys, urllib.error, urllib.request

OWNER = "LankyFranky89"
HERE = os.path.dirname(os.path.abspath(__file__))
TOKEN = os.environ.get("GITHUB_TOKEN", "")
FLAVOR = re.compile(r"[-_](forever|camelot|vanilla|classic|mainline|tbc|bcc|wrath|wotlk|cata|mists)$", re.I)


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
        if e.code in (404, 409):  # missing, or empty repo
            return None
        raise


def toc_fields(text):
    fields = {}
    for line in text.splitlines():
        m = re.match(r"##\s*([\w-]+)\s*:\s*(.*)", line)
        if m:
            fields[m.group(1).lower()] = re.sub(r"\|c[0-9a-fA-F]{8}|\|r", "", m.group(2)).strip()
    return fields


def all_repos():
    repos, page = [], 1
    while True:
        batch = get(f"https://api.github.com/users/{OWNER}/repos?per_page=100&type=owner&page={page}") or []
        repos += batch
        if len(batch) < 100:
            return repos
        page += 1


def read_skip():
    path = os.path.join(HERE, "skip.txt")
    if not os.path.exists(path):
        return set()
    return {l.strip().split("/")[-1].lower() for l in open(path) if l.strip() and not l.startswith("#")}


def build_entry(r):
    repo, branch = r["full_name"], r["default_branch"]
    files = get(f"https://api.github.com/repos/{repo}/contents?ref={branch}")
    if not isinstance(files, list):
        return None
    tocs = [f["name"] for f in files if f["type"] == "file" and f["name"].lower().endswith(".toc")]
    if not tocs:
        return None
    bases = sorted({FLAVOR.sub("", t[:-4]) for t in tocs}, key=len)
    folder = bases[0]
    toc_file = next((t for t in tocs if t[:-4].lower() == folder.lower()), tocs[0])
    fields = toc_fields(get(f"https://raw.githubusercontent.com/{repo}/{branch}/{toc_file}", raw=True) or "")
    version = re.sub(r"\s", "", fields.get("version", ""))

    # A release whose tag matches the .toc version wins.
    url = tag = None
    beta = "-" in version
    for rel in get(f"https://api.github.com/repos/{repo}/releases?per_page=10") or []:
        zips = [a for a in rel.get("assets", []) if a["name"].lower().endswith(".zip")]
        if rel.get("draft") or not zips:
            continue
        if version and rel["tag_name"].lstrip("vV") == version:
            url, tag, beta = zips[0]["browser_download_url"], rel["tag_name"], bool(rel.get("prerelease"))
        break
    if url is None:
        commit = get(f"https://api.github.com/repos/{repo}/commits/{branch}")
        if not commit:
            return None
        sha = commit["sha"]
        url = f"https://codeload.github.com/{repo}/zip/{sha}"
        tag = "v" + version if version else sha[:7]

    return {
        "repo": repo,
        "folder": folder,
        "title": fields.get("title", folder),
        "notes": fields.get("notes", r.get("description") or ""),
        "tag": tag,
        "beta": beta,
        "url": url,
    }


def main():
    skip = read_skip()
    addons, removed = [], []
    for r in all_repos():
        if r.get("private") or r.get("fork") or r.get("archived"):
            continue
        if r["name"].lower() in skip:
            removed.append(r["full_name"])
            continue
        e = build_entry(r)
        if e:
            addons.append(e)
    index = {
        "name": "Frank's Pack",
        "updated": datetime.datetime.now(datetime.timezone.utc).replace(microsecond=0).isoformat(),
        "addons": sorted(addons, key=lambda a: a["title"].lower()),
        "removed": sorted(removed + [f"{OWNER}/{s}" for s in skip if f"{OWNER}/{s}".lower() not in {x.lower() for x in removed}], key=str.lower),
    }
    path = os.path.join(HERE, "index.json")
    old = json.load(open(path)) if os.path.exists(path) else {}
    if old.get("addons") == index["addons"] and old.get("removed") == index["removed"]:
        print("No changes.")
    else:
        with open(path, "w") as f:
            json.dump(index, f, indent=2)
            f.write("\n")
    print(f"{len(addons)} addons: " + ", ".join(a['title'] for a in index["addons"]))
    print("Removed: " + ", ".join(index["removed"]))


if __name__ == "__main__":
    sys.exit(main())
