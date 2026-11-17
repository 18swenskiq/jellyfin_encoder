import copy
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
import release_package
import publish_release


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        actual = Path(__file__).resolve().parents[1]
        for filename in ("meta.json", "Jellyfin.Plugin.SpaceSaver/Jellyfin.Plugin.SpaceSaver.csproj", "Jellyfin.Plugin.SpaceSaver/Plugin.cs"):
            destination = self.root / filename
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes((actual / filename).read_bytes())
        self.metadata = json.loads((self.root / "meta.json").read_text())
        self.version = self.metadata["version"]
        self.tag = "v" + self.version
        self.repository = "18swenskiq/jellyfin_encoder"
        self.dll = self.root / "Jellyfin.Plugin.SpaceSaver/bin/Release/net9.0/Jellyfin.Plugin.SpaceSaver.dll"
        self.dll.parent.mkdir(parents=True)
        self.dll.write_bytes(b"MZ-test-fixture-not-a-real-assembly")
        self.output = self.root / "artifacts"

    def build(self, **kwargs):
        return release_package.build_package(self.root, self.dll, self.output, self.repository, **kwargs)

    def previous(self):
        _, manifest = self.build()
        previous = json.loads(manifest.read_text())
        entry = previous[0]["versions"][0]
        entry["version"] = "0.0.9.0"
        entry["sourceUrl"] = f"https://github.com/{self.repository}/releases/download/v0.0.9.0/SpaceSaver_0.0.9.0.zip"
        return previous

    def test_zip_layout_and_jellyfin_checksum(self):
        package, manifest = self.build()
        catalog = json.loads(manifest.read_text())
        self.assertEqual(catalog[0]["guid"], self.metadata["guid"])
        entry = catalog[0]["versions"][0]
        self.assertEqual(entry["checksum"], hashlib.md5(package.read_bytes(), usedforsecurity=False).hexdigest())
        self.assertEqual(entry["sourceUrl"], f"https://github.com/{self.repository}/releases/download/{self.tag}/{package.name}")
        self.assertEqual(entry["targetAbi"], "10.11.8.0")
        self.assertTrue(entry["timestamp"].endswith("Z"))
        with zipfile.ZipFile(package) as archive:
            self.assertEqual(set(archive.namelist()), {"Jellyfin.Plugin.SpaceSaver.dll", "meta.json"})
            self.assertEqual(archive.read("Jellyfin.Plugin.SpaceSaver.dll"), self.dll.read_bytes())

    def test_reproducible_zip(self):
        package, _ = self.build()
        original = package.read_bytes()
        self.build()
        self.assertEqual(package.read_bytes(), original)

    def test_keeps_old_versions_unchanged(self):
        previous = self.previous()
        expected = copy.deepcopy(previous[0]["versions"][0])
        _, manifest = self.build(previous=previous)
        versions = json.loads(manifest.read_text())[0]["versions"]
        self.assertEqual([v["version"] for v in versions], [self.version, "0.0.9.0"])
        self.assertEqual(versions[1], expected)

    def test_rejects_republishing_same_version(self):
        _, manifest = self.build()
        with self.assertRaisesRegex(ValueError, "newer"):
            self.build(previous=json.loads(manifest.read_text()))

    def test_rejects_wrong_tag(self):
        with self.assertRaisesRegex(ValueError, "tag"):
            self.build(tag="v99.0.0.0")

    def test_rejects_wrong_plugin_and_foreign_asset(self):
        previous = self.previous()
        previous[0]["guid"] = "bad-guid"
        with self.assertRaisesRegex(ValueError, "GUID"):
            self.build(previous=previous)
        previous = self.previous()
        previous[0]["versions"][0]["sourceUrl"] = "https://example.com/unknown.zip"
        with self.assertRaisesRegex(ValueError, "outside"):
            self.build(previous=previous)

    def test_rejects_version_drift(self):
        self.metadata["version"] = "2.0.0.0"
        (self.root / "meta.json").write_text(json.dumps(self.metadata))
        with self.assertRaisesRegex(ValueError, "project Version"):
            self.build()

    def test_rejects_abi_drift(self):
        self.metadata["targetAbi"] = "10.10.0.0"
        (self.root / "meta.json").write_text(json.dumps(self.metadata))
        with self.assertRaisesRegex(ValueError, "package references"):
            self.build()

    def run_publisher(self, releases=None, private=False, fail_upload=False, tag_commit=None, previous=None):
        calls = []
        def fake_gh(*args):
            calls.append(args)
            if args == ("api", f"repos/{self.repository}"):
                return json.dumps({"private": private})
            if args[:3] == ("api", "--paginate", "--slurp"):
                return json.dumps([releases or []])
            if args == ("api", f"repos/{self.repository}/releases/latest"):
                return json.dumps({"tag_name": "v0.0.9.0", "assets": [{"name": "manifest.json"}]})
            if args[:2] == ("release", "download"):
                directory = Path(args[args.index("--dir") + 1])
                (directory / "manifest.json").write_text(json.dumps(previous), encoding="utf-8")
            if args[:2] == ("release", "upload") and fail_upload:
                raise subprocess.CalledProcessError(1, "gh release upload")
            return ""
        environment = {"GITHUB_ACTIONS": "true", "GITHUB_REPOSITORY": self.repository, "GITHUB_SHA": "a" * 40, "GITHUB_REF_TYPE": "branch", "GITHUB_REF_NAME": "main"}
        result = subprocess.CompletedProcess([], 0 if tag_commit else 1, stdout=tag_commit or "", stderr="")
        with patch.dict("os.environ", environment, clear=True), patch.object(publish_release, "__file__", str(self.root / "scripts/publish_release.py")), patch.object(publish_release, "gh", side_effect=fake_gh), patch.object(publish_release.subprocess, "run", return_value=result), patch("builtins.print"):
            try:
                publish_release.publish()
            finally:
                self.calls = calls

    def test_publishes_only_after_assets_are_uploaded(self):
        self.run_publisher()
        writes = [call for call in self.calls if call[0] == "release"]
        self.assertEqual([call[1] for call in writes], ["create", "upload", "edit"])
        self.assertIn("--draft", writes[0])
        self.assertIn("--draft=false", writes[-1])
        self.assertIn("--latest", writes[-1])

    def test_upload_failure_keeps_release_draft(self):
        with self.assertRaises(subprocess.CalledProcessError):
            self.run_publisher(fail_upload=True)
        self.assertFalse(any(call[:2] == ("release", "edit") for call in self.calls))

    def test_published_release_cannot_be_overwritten(self):
        with self.assertRaisesRegex(ValueError, "already published"):
            self.run_publisher(releases=[{"tag_name": self.tag, "draft": False}])
        self.assertFalse(any(call[0] == "release" for call in self.calls))

    def test_private_repository_rejected_before_publication(self):
        with self.assertRaisesRegex(ValueError, "public repository"):
            self.run_publisher(private=True)
        self.assertFalse(any(call[0] == "release" for call in self.calls))

    def test_wrong_tag_commit_rejected(self):
        with self.assertRaisesRegex(ValueError, "different commit"):
            self.run_publisher(tag_commit="b" * 40)

    def test_draft_from_different_commit_rejected(self):
        with self.assertRaisesRegex(ValueError, "different code"):
            self.run_publisher(releases=[{"tag_name": self.tag, "draft": True, "target_commitish": "b" * 40}])

    def test_publication_downloads_and_preserves_previous_catalog(self):
        previous = self.previous()
        self.run_publisher(releases=[{"tag_name": "v0.0.9.0", "draft": False, "prerelease": False}], previous=previous)
        manifest = json.loads((self.root / "artifacts/release/manifest.json").read_text())
        self.assertEqual(manifest[0]["versions"][1], previous[0]["versions"][0])
        download = next(i for i, call in enumerate(self.calls) if call[:2] == ("release", "download"))
        create = next(i for i, call in enumerate(self.calls) if call[:2] == ("release", "create"))
        self.assertLess(download, create)


if __name__ == "__main__":
    unittest.main()
