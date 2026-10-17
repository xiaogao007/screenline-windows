"""Offline deployment safety checks; no cloud, systemctl or real Caddy mutations."""
import importlib.util
import io
import json
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location("screenline_deploy", Path(__file__).with_name("deploy.py"))
deploy = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(deploy)
COMMIT = "a" * 40
OLD_COMMIT = "b" * 40


class DeploymentSafetyTests(unittest.TestCase):
    def make_archive(self, path, extra=None, omit=None):
        with tarfile.open(path, "w:gz") as archive:
            names = ["screenline-windows-" + COMMIT + "/website/" + name
                     for name in deploy.ALLOWLIST if name != omit]
            names.append("screenline-windows-" + COMMIT + "/website/releases/latest.json")
            names.append("screenline-windows-" + COMMIT + "/website/DEPLOY.md")
            for name in names:
                data = ("synthetic " + name).encode()
                member = tarfile.TarInfo(name)
                member.size = len(data)
                archive.addfile(member, io.BytesIO(data))
            if extra:
                archive.addfile(extra, io.BytesIO(b"x" * extra.size) if extra.isfile() else None)

    def test_extracts_only_six_runtime_files(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "archive.tar.gz"
            self.make_archive(path)
            files = deploy.read_runtime_files(path, COMMIT)
            self.assertEqual(set(files), set(deploy.ALLOWLIST))
            self.assertNotIn("releases/latest.json", files)
            self.assertNotIn("DEPLOY.md", files)

    def test_rejects_traversal(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "archive.tar.gz"
            self.make_archive(path, tarfile.TarInfo("screenline-windows-" + COMMIT + "/../escape"))
            with self.assertRaises(deploy.DeploymentError):
                deploy.read_runtime_files(path, COMMIT)

    def test_rejects_links_even_outside_allowlist(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "archive.tar.gz"
            member = tarfile.TarInfo("screenline-windows-" + COMMIT + "/link")
            member.type = tarfile.SYMTYPE
            member.linkname = "/etc/passwd"
            self.make_archive(path, member)
            with self.assertRaises(deploy.DeploymentError):
                deploy.read_runtime_files(path, COMMIT)

    def test_rejects_duplicate_member(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "archive.tar.gz"
            member = tarfile.TarInfo("screenline-windows-" + COMMIT + "/website/index.html")
            self.make_archive(path, member)
            with self.assertRaises(deploy.DeploymentError):
                deploy.read_runtime_files(path, COMMIT)

    def test_rejects_missing_runtime_asset(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "archive.tar.gz"
            self.make_archive(path, omit="release-key.json")
            with self.assertRaises(deploy.DeploymentError):
                deploy.read_runtime_files(path, COMMIT)

    def test_rejects_archive_checksum_mismatch(self):
        with tempfile.TemporaryDirectory() as folder:
            source, target = Path(folder) / "source", Path(folder) / "target"
            source.write_bytes(b"synthetic archive")
            with self.assertRaises(deploy.DeploymentError):
                deploy.obtain_archive(COMMIT, "0" * 64, str(source), target)

    def test_preserves_existing_site_bytes(self):
        original = "{\n    email ops@example.com\n}\n\nxiaogao.xyz {\n respond existing\n}\n"
        candidate = deploy.render_config(original, deploy.site_block())
        self.assertTrue(candidate.startswith(original))
        self.assertEqual(candidate.count(deploy.BEGIN), 1)
        self.assertIn("header_up Host screenline-updates", candidate)

    def test_replaces_only_managed_marker_span(self):
        prefix, suffix = "xiaogao.xyz { respond old }\n\n", "\nother.xiaogao.xyz { respond other }\n"
        old = deploy.BEGIN + "\n" + deploy.DOMAIN + " { respond old }\n" + deploy.END + "\n"
        candidate = deploy.render_config(prefix + old + suffix, deploy.site_block())
        self.assertTrue(candidate.startswith(prefix))
        self.assertTrue(candidate.endswith(suffix))
        self.assertNotIn(deploy.DOMAIN + " { respond old }", candidate)

    def test_refuses_unmanaged_domain_and_broken_markers(self):
        for original in (deploy.DOMAIN + " { respond existing }", deploy.BEGIN + "\n",
                         deploy.END + "\n" + deploy.BEGIN, deploy.site_block() + deploy.site_block()):
            with self.subTest(original=original), self.assertRaises(deploy.DeploymentError):
                deploy.render_config(original, deploy.site_block())

    def activation_fixture(self, folder, fail_validation=False, fail_reload=False):
        base = Path(folder)
        config = base / "Caddyfile"
        state = base / "state"
        state.mkdir()
        original = b"xiaogao.xyz {\n    respond existing\n}\n"
        config.write_bytes(original)
        info = config.stat()
        releases = base / "releases"
        previous = str(releases / OLD_COMMIT)
        current = {"value": previous}
        reloads = []

        def fake_atomic(path, data, mode=0o600, owner=None):
            path.write_bytes(data)

        def fake_command(args, timeout=45):
            if fail_validation and args[:2] == ["caddy", "validate"]:
                raise deploy.DeploymentError("synthetic validate failure")
            if args[:2] == ["systemctl", "reload"]:
                reloads.append(args)
                if fail_reload and len(reloads) == 1:
                    raise deploy.DeploymentError("synthetic reload failure")
            return ""

        patches = [patch.object(deploy, "BASE", base), patch.object(deploy, "STATE", state),
                   patch.object(deploy, "RELEASES", releases), patch.object(deploy, "CONFIG", config),
                   patch.object(deploy, "require_directory", return_value=None),
                   patch.object(deploy, "require_file", return_value=info),
                   patch.object(deploy, "current_target", side_effect=lambda: current["value"]),
                   patch.object(deploy, "switch_current", side_effect=lambda value: current.update(value=value)),
                   patch.object(deploy, "atomic_file", side_effect=fake_atomic),
                   patch.object(deploy, "sync_directory", return_value=None),
                   patch.object(deploy, "command", side_effect=fake_command)]
        from contextlib import ExitStack
        with ExitStack() as stack:
            for item in patches:
                stack.enter_context(item)
            if fail_validation or fail_reload:
                with self.assertRaises(deploy.DeploymentError):
                    deploy.activate(COMMIT, {"synthetic": True}, "deploy")
                self.assertEqual(config.read_bytes(), original)
                self.assertEqual(current["value"], previous)
                metadata = json.loads(next(p for p in state.glob("*.json")).read_bytes())
                self.assertEqual(metadata["status"], "failed_restored")
            else:
                deploy.activate(COMMIT, {"synthetic": True}, "deploy")
                self.assertEqual(current["value"], str(releases / COMMIT))
                self.assertTrue(config.read_bytes().startswith(original))
            return reloads

    def test_validation_failure_does_not_activate(self):
        with tempfile.TemporaryDirectory() as folder:
            self.assertEqual(self.activation_fixture(folder, fail_validation=True), [])

    def test_reload_failure_restores_config_and_current(self):
        with tempfile.TemporaryDirectory() as folder:
            self.assertEqual(len(self.activation_fixture(folder, fail_reload=True)), 2)

    def test_success_preserves_existing_sites(self):
        with tempfile.TemporaryDirectory() as folder:
            self.assertEqual(len(self.activation_fixture(folder)), 1)


if __name__ == "__main__":
    unittest.main()
