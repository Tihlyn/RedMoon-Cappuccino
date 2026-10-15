#!/usr/bin/env python3
"""Update pluginmaster.json for a stable (v*) or testing (testing-*) release.

Stable releases set AssemblyVersion and point install/update links at the tag's zip.
Testing releases only touch the Testing* fields, so the stable entry is never replaced
by a candidate. Adapted from MahjongHater's make_repo_json.py.
"""

import argparse
import json
from pathlib import Path
import re
import time
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent
REPO = "https://github.com/Tihlyn/RedMoon-Cappuccino"
ASSET = "RedMoonCappuccino.zip"
VERSION_RE = r"(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)"


def parse_version(version):
    if not re.fullmatch(VERSION_RE, version):
        raise ValueError(f"Version {version!r} must be major.minor.patch.revision")
    parts = tuple(int(p) for p in version.split("."))
    if any(p > 65534 for p in parts):
        raise ValueError("Version components must fit a .NET assembly version (0..65534)")
    return parts


def csproj_version(csproj):
    versions = ET.parse(csproj).getroot().findall("./PropertyGroup/Version")
    if len(versions) != 1 or not versions[0].text:
        raise ValueError(f"Expected exactly one <Version> in {csproj}")
    return versions[0].text.strip()


def asset_url(tag):
    return f"{REPO}/releases/download/{tag}/{ASSET}"


def stable_entry(entry, manifest, version, timestamp):
    if manifest.get("AssemblyVersion") != version:
        raise ValueError("Packaged AssemblyVersion does not match the release version; rebuild first")
    url = asset_url(f"v{version}")
    entry = dict(entry)
    entry.update(
        AssemblyVersion=version,
        DalamudApiLevel=manifest["DalamudApiLevel"],
        DownloadLinkInstall=url,
        DownloadLinkUpdate=url,
        LastUpdate=timestamp,
        IsTestingExclusive=False,
    )
    # A testing build at or below the new stable version is obsolete: retire it.
    testing = entry.get("TestingAssemblyVersion")
    if not testing or parse_version(testing) <= parse_version(version):
        for key in ("TestingAssemblyVersion", "TestingDalamudApiLevel", "TestingChangelog"):
            entry.pop(key, None)
        entry["DownloadLinkTesting"] = url
    return entry


def testing_entry(entry, manifest, version, timestamp, changelog):
    if manifest.get("AssemblyVersion") != version:
        raise ValueError("Packaged AssemblyVersion does not match the testing version; rebuild first")
    if parse_version(version) <= parse_version(entry["AssemblyVersion"]):
        raise ValueError(f"Testing version {version} must be newer than stable {entry['AssemblyVersion']}")
    entry = dict(entry)
    entry.update(
        TestingAssemblyVersion=version,
        TestingDalamudApiLevel=manifest["DalamudApiLevel"],
        TestingChangelog=changelog,
        DownloadLinkTesting=asset_url(f"testing-{version}"),
        LastUpdate=timestamp,
    )
    return entry


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tag", required=True, help="v<version> or testing-<version>")
    parser.add_argument("--manifest", type=Path, required=True, help="Packaged RedMoonCappuccino.json")
    parser.add_argument("--pluginmaster", type=Path, default=ROOT / "pluginmaster.json")
    parser.add_argument("--csproj", type=Path, default=ROOT / "RedMoonCappuccino" / "RedMoonCappuccino.csproj")
    parser.add_argument("--changelog", default="")
    parser.add_argument("--check-only", action="store_true", help="Validate the tag, write nothing")
    args = parser.parse_args()
    try:
        testing = args.tag.startswith("testing-")
        version = args.tag[len("testing-"):] if testing else args.tag.removeprefix("v")
        if not testing and not args.tag.startswith("v"):
            raise ValueError("Tag must start with v or testing-")
        parse_version(version)
        if not testing and csproj_version(args.csproj) != version:
            raise ValueError(f"Tag {args.tag} does not match <Version>{csproj_version(args.csproj)}</Version>")
        if args.check_only:
            return
        manifest = json.loads(args.manifest.read_text(encoding="utf-8-sig"))
        if manifest.get("InternalName") != "RedMoonCappuccino" or type(manifest.get("DalamudApiLevel")) is not int:
            raise ValueError("Expected the packaged RedMoonCappuccino manifest with an integer DalamudApiLevel")
        entries = json.loads(args.pluginmaster.read_text(encoding="utf-8-sig"))
        if not isinstance(entries, list) or len(entries) != 1:
            raise ValueError("Expected exactly one pluginmaster entry")
        timestamp = int(time.time())
        entry = (testing_entry(entries[0], manifest, version, timestamp, args.changelog) if testing
                 else stable_entry(entries[0], manifest, version, timestamp))
        args.pluginmaster.write_text(json.dumps([entry], ensure_ascii=False, indent=2) + "\n",
                                     encoding="utf-8", newline="\n")
    except (OSError, ValueError, KeyError, ET.ParseError) as error:
        parser.exit(1, f"error: {error}\n")


if __name__ == "__main__":
    main()
