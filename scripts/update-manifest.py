#!/usr/bin/env python3
"""Update root manifest.json with a new plugin release entry.

Usage: update-manifest.py <version> <zipUrl> <sha256zip> [baseUrl]
Reads artifacts/meta.json for guid/name/description/category, merges into the
root manifest.json (Jellyfin PackageInfo[] catalog format), newest-first.
"""
import json
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
MANIFEST = ROOT / "manifest.json"
META = ROOT / "artifacts" / "meta.json"


def load_json(path: pathlib.Path, default):
    if not path.exists():
        return default
    return json.loads(path.read_text())


def main() -> None:
    version, zip_url, checksum, *rest = sys.argv[1:]
    base_url = rest[0] if rest else ""

    version_clean = version.lstrip("v")
    meta = load_json(META, {})
    now_iso = meta.get("timestamp", "")

    version_info = {
        "version": version_clean,
        "changelog": meta.get("changelog", ""),
        "targetAbi": meta["targetAbi"],
        "sourceUrl": zip_url,
        "checksum": checksum,
        "timestamp": now_iso,
        "repositoryName": meta.get("name", "JioTV"),
        "repositoryUrl": base_url,
    }

    manifest = load_json(MANIFEST, [])
    repo_base = base_url or f"https://github.com/chetasr/{ROOT.name}"

    guid = meta.get("guid", "")
    existing = next((pkg for pkg in manifest if pkg.get("guid") == guid), None)
    if existing is None:
        pkg = {
            "guid": guid,
            "name": meta.get("name", "JioTV"),
            "description": meta.get("description", ""),
            "overview": meta.get("description", ""),
            "owner": meta.get("owner", "chetasr"),
            "category": meta.get("category", "Live TV"),
            "imageUrl": meta.get("imageUrl", ""),
            "versions": [version_info],
        }
        manifest.append(pkg)
    else:
        existing.setdefault("versions", [])
        existing["versions"] = [
            v for v in existing["versions"]
            if v.get("version") != version_info["version"]
        ]
        existing["versions"].insert(0, version_info)

    MANIFEST.write_text(json.dumps(manifest, indent=4) + "\n")
    print(f"manifest.json updated with {version_clean}")


if __name__ == "__main__":
    main()
