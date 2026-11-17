"""Build a Jellyfin install ZIP and repository manifest from a compiled plugin."""

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import uuid
import xml.etree.ElementTree as ET
import zipfile


def version_tuple(value):
    if not isinstance(value, str) or not re.fullmatch(r"\d+\.\d+\.\d+\.\d+", value):
        raise ValueError("Versions must have four numeric parts, for example 0.1.0.0")
    return tuple(int(part) for part in value.split("."))


def read_metadata(root):
    metadata = json.loads((root / "meta.json").read_text(encoding="utf-8"))
    version_tuple(metadata["version"])
    version_tuple(metadata["targetAbi"])
    uuid.UUID(metadata["guid"])
    project = ET.parse(root / "Jellyfin.Plugin.SpaceSaver/Jellyfin.Plugin.SpaceSaver.csproj")
    if project.findtext(".//Version") != metadata["version"]:
        raise ValueError("meta.json version must match the project Version")
    for reference in project.findall(".//PackageReference"):
        if reference.get("Include") in ("Jellyfin.Controller", "Jellyfin.Model"):
            abi = reference.get("Version").split(".")
            if tuple(map(int, abi + ["0"] * (4 - len(abi)))) != version_tuple(metadata["targetAbi"]):
                raise ValueError("targetAbi must match the Jellyfin package references")
    plugin = (root / "Jellyfin.Plugin.SpaceSaver/Plugin.cs").read_text(encoding="utf-8")
    if metadata["guid"] not in plugin:
        raise ValueError("Manifest GUID must match the plugin GUID")
    return metadata


def build_package(root, dll, output, repository, tag=None, previous=None):
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9_.-]+", repository):
        raise ValueError("Repository must be a GitHub owner/name")
    metadata = read_metadata(root)
    version = metadata["version"]
    tag = tag or "v" + version
    if tag != "v" + version:
        raise ValueError(f"Release tag must be v{version}, matching meta.json and the project")
    history = []
    if previous is not None:
        if not isinstance(previous, list) or len(previous) != 1 or previous[0].get("guid") != metadata["guid"]:
            raise ValueError("Previous manifest must contain this plugin's GUID")
        history = previous[0]["versions"]
        if not isinstance(history, list):
            raise ValueError("Previous versions must be a list")
        seen = set()
        for entry in history:
            old = version_tuple(entry["version"])
            version_tuple(entry["targetAbi"])
            if old >= version_tuple(version):
                raise ValueError("New release must be newer than every published version; never replace published ZIPs")
            if old in seen:
                raise ValueError("Duplicate version in previous manifest")
            seen.add(old)
            if not re.fullmatch(r"[a-fA-F0-9]{32}", entry["checksum"]):
                raise ValueError("Invalid checksum in previous manifest")
            if not entry["sourceUrl"].startswith(f"https://github.com/{repository}/releases/download/"):
                raise ValueError("Previous version points outside this repository")
    dll_bytes = dll.read_bytes()
    if not dll_bytes:
        raise ValueError("Compiled plugin DLL is empty")
    output.mkdir(parents=True, exist_ok=True)
    package_name = f"SpaceSaver_{version}.zip"
    package = output / package_name
    # Fixed ZIP metadata keeps reruns of the same build byte-identical.
    with zipfile.ZipFile(package, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for name, data in [("Jellyfin.Plugin.SpaceSaver.dll", dll_bytes), ("meta.json", (json.dumps(metadata, indent=2) + "\n").encode())]:
            info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            archive.writestr(info, data)
    data = package.read_bytes()
    current = {
        "version": version,
        "changelog": f"See https://github.com/{repository}/releases/tag/{tag}",
        "targetAbi": metadata["targetAbi"],
        "sourceUrl": f"https://github.com/{repository}/releases/download/{tag}/{package_name}",
        # Jellyfin 10.11 requires MD5 for repository package checksums.
        "checksum": hashlib.md5(data, usedforsecurity=False).hexdigest(),
        "timestamp": datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z"),
    }
    manifest = [{key: metadata[key] for key in ("guid", "name", "description", "overview", "owner", "category")}]
    manifest[0]["versions"] = sorted([current, *history], key=lambda entry: version_tuple(entry["version"]), reverse=True)
    manifest_path = output / "manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    (output / "SHA256SUMS").write_text(f"{hashlib.sha256(data).hexdigest()}  {package_name}\n", encoding="utf-8")
    return package, manifest_path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", default="18swenskiq/jellyfin_encoder")
    parser.add_argument("--tag")
    parser.add_argument("--previous-manifest", type=Path)
    parser.add_argument("--output", type=Path, default=Path("artifacts"))
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    previous = json.loads(args.previous_manifest.read_text(encoding="utf-8")) if args.previous_manifest else None
    package, manifest = build_package(root, root / "Jellyfin.Plugin.SpaceSaver/bin/Release/net9.0/Jellyfin.Plugin.SpaceSaver.dll", args.output, args.repository, args.tag, previous)
    print(f"Package: {package}\nManifest: {manifest}")


if __name__ == "__main__":
    main()
