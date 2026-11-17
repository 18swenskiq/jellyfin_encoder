"""Publish a tested build from GitHub Actions, keeping the previous catalog available until ready."""

import json
import os
from pathlib import Path
import subprocess
import tempfile

from release_package import build_package, read_metadata


def gh(*args):
    result = subprocess.run(["gh", *args], check=True, capture_output=True, text=True)
    return result.stdout


def publish():
    if os.environ.get("GITHUB_ACTIONS") != "true":
        raise RuntimeError("Run the Publish plugin workflow on GitHub to publish a release")
    repository = os.environ["GITHUB_REPOSITORY"]
    commit = os.environ["GITHUB_SHA"]
    root = Path(__file__).resolve().parents[1]
    metadata = read_metadata(root)
    tag = "v" + metadata["version"]
    if os.environ.get("GITHUB_REF_TYPE") == "tag" and os.environ["GITHUB_REF_NAME"] != tag:
        raise ValueError(f"Pushed tag must be {tag}")
    repo_info = json.loads(gh("api", f"repos/{repository}"))
    if repo_info["private"]:
        raise ValueError("Use a public repository so Jellyfin can download the manifest and ZIP without GitHub credentials")
    # No mutation yet: fail on API/auth errors, and read every release, not just the first page.
    pages = json.loads(gh("api", "--paginate", "--slurp", f"repos/{repository}/releases?per_page=100"))
    releases = [release for page in pages for release in page]
    existing = next((release for release in releases if release["tag_name"] == tag), None)
    if existing and not existing["draft"]:
        raise ValueError(f"{tag} is already published. Bump the version for a new release")
    # A pre-existing tag must name the code that was built, including annotated tags.
    tag_commit = subprocess.run(["git", "rev-parse", "--verify", f"refs/tags/{tag}^{{commit}}"], capture_output=True, text=True)
    if tag_commit.returncode == 0 and tag_commit.stdout.strip() != commit:
        raise ValueError("Existing release tag points to a different commit; use a new version")
    if existing and tag_commit.returncode != 0 and existing.get("target_commitish") != commit:
        raise ValueError("Existing draft targets different code; remove that draft or run the workflow at its original commit")
    published = [release for release in releases if not release["draft"] and not release["prerelease"]]
    previous = None
    if published:
        latest = json.loads(gh("api", f"repos/{repository}/releases/latest"))
        if not any(asset["name"] == "manifest.json" for asset in latest["assets"]):
            raise ValueError("Latest stable release has no manifest.json; migrate its catalog before publishing")
        with tempfile.TemporaryDirectory() as directory:
            gh("release", "download", latest["tag_name"], "--repo", repository, "--pattern", "manifest.json", "--dir", directory)
            previous = json.loads((Path(directory) / "manifest.json").read_text(encoding="utf-8"))
    output = root / "artifacts/release"
    package, manifest = build_package(root, root / "Jellyfin.Plugin.SpaceSaver/bin/Release/net9.0/Jellyfin.Plugin.SpaceSaver.dll", output, repository, tag, previous)
    notes = output / "release-notes.md"
    url = f"https://github.com/{repository}/releases/latest/download/manifest.json"
    notes.write_text(
        f"Space Saver {metadata['version']} for Jellyfin {metadata['targetAbi']}.\n\n"
        f"Add this repository in **Dashboard → Plugins → Repositories**:\n\n{url}\n\n"
        "Install **Space Saver** from the Catalog and restart Jellyfin. "
        "Configure Intel QSV / VA-API and run Test GPU before your first conversion.\n\n"
        "Original backups are retained until explicitly deleted. See the README for supported sources and Docker GPU access.\n",
        encoding="utf-8",
    )
    if not existing:
        gh("release", "create", tag, "--repo", repository, "--target", commit, "--draft", "--title", "Space Saver " + metadata["version"], "--notes-file", str(notes))
    # Upload everything while the release is a draft; the stable manifest URL still serves the prior release.
    gh("release", "upload", tag, str(package), str(manifest), str(output / "SHA256SUMS"), "--repo", repository, "--clobber")
    gh("release", "edit", tag, "--repo", repository, "--draft=false", "--latest", "--notes-file", str(notes))
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as stream:
            stream.write(f"Published **{tag}**.\n\nJellyfin repository URL: [{url}]({url})\n")
    print(f"Published {tag}. Jellyfin repository URL: {url}")


if __name__ == "__main__":
    publish()
