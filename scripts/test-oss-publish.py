#!/usr/bin/env python3
"""Offline publication-order tests. All OSS and public HTTP operations are fake.

Cryptographic producer tests run separately in test-update-publishing.ps1.
These tests deliberately replace signature verification to exercise publication
failure boundaries without SDK installation, credentials or network access.
"""
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("oss_publisher", Path(__file__).with_name("publish-oss-release.py"))
publisher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(publisher)


class FakeStorage:
    def __init__(self, fail_on=None):
        self.objects = {}
        self.writes = []
        self.fail_on = fail_on

    def read(self, key, maximum):
        data = self.objects.get(key)
        if data is not None and len(data) > maximum:
            raise ValueError("Oversized fake object")
        return data

    def put(self, key, path, content_type, immutable):
        if self.fail_on and self.fail_on in key:
            raise RuntimeError("Simulated upload failure")
        if immutable and key in self.objects:
            raise RuntimeError("Immutable object overwrite")
        self.objects[key] = path.read_bytes()
        self.writes.append((key, immutable, content_type))


class PublishTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="screenline-oss-offline-")
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.tag = "v0.1.7"
        self.installer = "Screenline-" + self.tag + "-windows-x64-setup.exe"
        names = [self.installer, "Screenline-" + self.tag + "-windows-x64.zip", "Screenline-" + self.tag + "-source.zip"]
        checksum_lines = []
        for name in names:
            data = ("fixture bytes for " + name).encode()
            (self.directory / name).write_bytes(data)
            checksum_lines.append(hashlib.sha256(data).hexdigest() + "  " + name)
        (self.directory / "SHA256SUMS.txt").write_text("\n".join(checksum_lines) + "\n", encoding="utf-8")
        (self.directory / "release-notes.txt").write_bytes(b"Update notes\n")
        setup_bytes = (self.directory / self.installer).read_bytes()
        self.manifest = {"schema": 1, "tag": self.tag, "installer": self.installer, "size": len(setup_bytes),
                         "sha256": hashlib.sha256(setup_bytes).hexdigest(), "notes": "Update notes"}
        self.manifest_path = self.directory / "latest.json"
        self.manifest_path.write_text(json.dumps(self.manifest), encoding="utf-8")
        self.config = {"prefix": "screenline/", "SCREENLINE_UPDATE_BASE_URL": "https://updates.example.invalid/screenline/", "SCREENLINE_UPDATE_PUBLIC_KEY": "unused test key"}
        self.latest_key = "screenline/stable/latest.json"
        verifier = patch.object(publisher, "verify_manifest", side_effect=lambda data, key: json.loads(data.decode("utf-8")))
        verifier.start()
        self.addCleanup(verifier.stop)

    def readback(self, storage):
        def check(url, size, digest):
            key = url.removeprefix("https://updates.example.invalid/")
            data = storage.objects[key]
            self.assertEqual(len(data), size)
            self.assertEqual(hashlib.sha256(data).hexdigest(), digest)
        return check

    def old_manifest(self, storage, tag="v0.1.6"):
        old = dict(self.manifest, tag=tag)
        data = json.dumps(old).encode()
        storage.objects[self.latest_key] = data
        return data

    def test_latest_written_only_after_all_five_assets_verify(self):
        storage = FakeStorage()
        publisher.publish(self.config, self.directory, self.manifest_path, storage, self.readback(storage))
        self.assertEqual(len(storage.writes), 6)
        self.assertTrue(all(immutable for _, immutable, _ in storage.writes[:-1]))
        self.assertEqual(storage.writes[-1][:2], (self.latest_key, False))
        self.assertEqual(storage.objects[self.latest_key], self.manifest_path.read_bytes())

    def test_failed_asset_upload_preserves_old_latest(self):
        storage = FakeStorage(fail_on="-source.zip")
        previous = self.old_manifest(storage)
        with self.assertRaises(RuntimeError):
            publisher.publish(self.config, self.directory, self.manifest_path, storage, self.readback(storage))
        self.assertEqual(storage.objects[self.latest_key], previous)
        self.assertFalse(any(key == self.latest_key for key, _, _ in storage.writes))

    def test_public_access_failure_preserves_old_latest(self):
        storage = FakeStorage()
        previous = self.old_manifest(storage)
        def inaccessible(url, size, digest):
            raise PermissionError("Public read is blocked")
        with self.assertRaises(PermissionError):
            publisher.publish(self.config, self.directory, self.manifest_path, storage, inaccessible)
        self.assertEqual(storage.objects[self.latest_key], previous)

    def test_changed_local_installer_never_reaches_cloud(self):
        storage = FakeStorage()
        (self.directory / self.installer).write_bytes(b"tampered")
        with self.assertRaises(ValueError):
            publisher.publish(self.config, self.directory, self.manifest_path, storage, self.readback(storage))
        self.assertFalse(storage.writes)

    def test_signed_crlf_notes_publish_without_normalizing_bytes(self):
        self.manifest["notes"] = "Release notes\r\nA Windows line\r\nSecond line"
        self.manifest_path.write_text(json.dumps(self.manifest), encoding="utf-8")
        expected_notes = (self.manifest["notes"] + "\n").encode("utf-8")
        (self.directory / "release-notes.txt").write_bytes(expected_notes)
        storage = FakeStorage()
        publisher.publish(self.config, self.directory, self.manifest_path, storage, self.readback(storage))
        key = "screenline/releases/" + self.tag + "/release-notes.txt"
        self.assertEqual(storage.objects[key], expected_notes)
        self.assertEqual(storage.objects[self.latest_key], self.manifest_path.read_bytes())

    def test_signed_mixed_line_endings_publish_without_normalizing_bytes(self):
        self.manifest["notes"] = "Release notes\nA source-document line\n\nBuild details\r\nA Windows build line\r\n"
        self.manifest_path.write_text(json.dumps(self.manifest), encoding="utf-8")
        expected_notes = (self.manifest["notes"] + "\n").encode("utf-8")
        (self.directory / "release-notes.txt").write_bytes(expected_notes)
        storage = FakeStorage()
        publisher.publish(self.config, self.directory, self.manifest_path, storage, self.readback(storage))
        key = "screenline/releases/" + self.tag + "/release-notes.txt"
        self.assertEqual(storage.objects[key], expected_notes)

    def test_one_changed_note_byte_preserves_previous_latest_and_blocks_all_uploads(self):
        storage = FakeStorage()
        previous = self.old_manifest(storage)
        (self.directory / "release-notes.txt").write_bytes(b"Vpdate notes\n")
        with self.assertRaisesRegex(ValueError, "Signed manifest does not match the release notes"):
            publisher.publish(self.config, self.directory, self.manifest_path, storage, self.readback(storage))
        self.assertEqual(storage.objects[self.latest_key], previous)
        self.assertFalse(storage.writes)

    def test_newline_normalization_is_a_note_change_and_blocks_publish(self):
        self.manifest["notes"] = "Release notes\r\nWindows line\nSource line"
        self.manifest_path.write_text(json.dumps(self.manifest), encoding="utf-8")
        (self.directory / "release-notes.txt").write_bytes((self.manifest["notes"].replace("\r\n", "\n") + "\n").encode("utf-8"))
        storage = FakeStorage()
        previous = self.old_manifest(storage)
        with self.assertRaisesRegex(ValueError, "Signed manifest does not match the release notes"):
            publisher.publish(self.config, self.directory, self.manifest_path, storage, self.readback(storage))
        self.assertEqual(storage.objects[self.latest_key], previous)
        self.assertFalse(storage.writes)

    def test_existing_immutable_asset_conflict_preserves_old_latest(self):
        storage = FakeStorage()
        previous = self.old_manifest(storage)
        storage.objects["screenline/releases/" + self.tag + "/" + self.installer] = b"other"
        with self.assertRaises(ValueError):
            publisher.publish(self.config, self.directory, self.manifest_path, storage, self.readback(storage))
        self.assertEqual(storage.objects[self.latest_key], previous)
        self.assertFalse(storage.writes)

    def test_older_tag_does_not_downgrade_latest(self):
        storage = FakeStorage()
        previous = self.old_manifest(storage, tag="v0.1.8")
        with self.assertRaises(ValueError):
            publisher.publish(self.config, self.directory, self.manifest_path, storage, self.readback(storage))
        self.assertEqual(storage.objects[self.latest_key], previous)
        self.assertFalse(storage.writes)

    def test_rerun_accepts_identical_immutable_assets(self):
        storage = FakeStorage()
        publisher.publish(self.config, self.directory, self.manifest_path, storage, self.readback(storage))
        storage.writes.clear()
        publisher.publish(self.config, self.directory, self.manifest_path, storage, self.readback(storage))
        self.assertEqual(len(storage.writes), 1)
        self.assertEqual(storage.writes[0][0], self.latest_key)


if __name__ == "__main__":
    unittest.main(verbosity=2)
