#!/usr/bin/env python3
"""Deploy only Screenline's public website to the existing Ubuntu/Caddy host.

Python 3 standard library only. `inspect` is read-only except temporary downloads.
`deploy` and `rollback` require Linux root and never remove prior releases.
"""

import argparse
import contextlib
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
import tarfile
import tempfile
import time
from datetime import datetime, timezone
from urllib.parse import urlsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener
import uuid


DOMAIN = "screenline.xiaogao.xyz"
BASE = Path("/var/www/screenline")
RELEASES = BASE / "releases"
STATE = BASE / "state"
CONFIG = Path("/etc/caddy/Caddyfile")
LOCK = STATE / "deploy.lock"
BEGIN = "# BEGIN SCREENLINE MANAGED SITE"
END = "# END SCREENLINE MANAGED SITE"
REPO = "xiaogao007/screenline-windows"
ALLOWLIST = (
    "index.html", "styles.css", "release.js", "release-key.json",
    "assets/logo.svg", "assets/shelf-preview.png",
)
MAX_ARCHIVE = 64 * 1024 * 1024
MAX_MEMBER = 20 * 1024 * 1024
MAX_EXPANDED = 512 * 1024 * 1024
MAX_CONFIG = 2 * 1024 * 1024
MAX_MEMBERS = 10000
DOWNLOAD_SECONDS = 120


class DeploymentError(Exception):
    pass


def full_commit(value):
    if not re.fullmatch(r"[0-9a-f]{40}", value):
        raise argparse.ArgumentTypeError("commit must be exactly 40 lowercase hex characters")
    return value


def sha256_value(value):
    if not re.fullmatch(r"[0-9a-f]{64}", value):
        raise argparse.ArgumentTypeError("sha256 must be exactly 64 lowercase hex characters")
    return value


def digest(data):
    return hashlib.sha256(data).hexdigest()


def archive_url(commit):
    full_commit(commit)
    return "https://codeload.github.com/{}/tar.gz/{}".format(REPO, commit)


class RestrictedRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        parsed = urlsplit(newurl)
        if (parsed.scheme != "https" or parsed.hostname != "codeload.github.com"
                or parsed.port not in (None, 443) or parsed.username or parsed.password):
            raise DeploymentError("archive redirect left the approved HTTPS host")
        return super().redirect_request(req, fp, code, msg, headers, newurl)


def obtain_archive(commit, expected_sha256, local_path, target):
    started = time.monotonic()
    if local_path:
        source = Path(local_path)
        if source.is_symlink() or not source.is_file():
            raise DeploymentError("local archive must be a regular, non-symlink file")
        incoming = source.open("rb")
    else:
        request = Request(archive_url(commit), headers={"User-Agent": "ScreenlineWebsiteDeploy/1"})
        incoming = build_opener(RestrictedRedirect()).open(request, timeout=20)
        if incoming.status != 200:
            incoming.close()
            raise DeploymentError("archive download did not return HTTP 200")
    hasher = hashlib.sha256()
    size = 0
    with incoming, target.open("xb") as output:
        while True:
            if time.monotonic() - started > DOWNLOAD_SECONDS:
                raise DeploymentError("archive copy/download exceeded 120 seconds")
            chunk = incoming.read(128 * 1024)
            if not chunk:
                break
            size += len(chunk)
            if size > MAX_ARCHIVE:
                raise DeploymentError("archive exceeds the 64 MiB limit")
            hasher.update(chunk)
            output.write(chunk)
    if hasher.hexdigest() != expected_sha256:
        raise DeploymentError("archive SHA-256 mismatch; no server state was changed")


def read_runtime_files(archive_path, commit):
    """No extract()/extractall(): every accepted byte is read into bounded memory."""
    root = "screenline-windows-" + commit
    prefix = root + "/website/"
    found = {}
    seen = set()
    expanded = 0
    started = time.monotonic()
    with tarfile.open(archive_path, mode="r:gz") as archive:
        for number, member in enumerate(archive, 1):
            if number > MAX_MEMBERS or time.monotonic() - started > 60:
                raise DeploymentError("archive member/time limit exceeded")
            name = member.name.rstrip("/")
            parts = name.split("/")
            if (not name or "\\" in name or "\x00" in name
                    or any(p in ("", ".", "..") for p in parts) or parts[0] != root):
                raise DeploymentError("unsafe or unexpected archive path")
            if name in seen:
                raise DeploymentError("duplicate archive path: " + name)
            seen.add(name)
            if not (member.isdir() or member.isfile()) or member.issym() or member.islnk():
                raise DeploymentError("archive contains a link or special file")
            if member.size < 0:
                raise DeploymentError("negative archive member size")
            expanded += member.size
            if expanded > MAX_EXPANDED:
                raise DeploymentError("archive expanded-size limit exceeded")
            if not name.startswith(prefix):
                continue
            relative = name[len(prefix):]
            if relative not in ALLOWLIST:
                continue
            if not member.isfile() or member.size > MAX_MEMBER:
                raise DeploymentError("runtime asset must be a bounded regular file: " + relative)
            with archive.extractfile(member) as incoming:
                data = incoming.read(MAX_MEMBER + 1)
            if len(data) != member.size:
                raise DeploymentError("runtime asset has an invalid/truncated size")
            found[relative] = data
    missing = set(ALLOWLIST) - set(found)
    if missing:
        raise DeploymentError("archive is missing runtime assets: " + ", ".join(sorted(missing)))
    return found


def site_block():
    path = Path(__file__).with_name("site.caddy")
    if path.is_symlink() or not path.is_file() or path.stat().st_size > 16384:
        raise DeploymentError("the reviewed site.caddy template is missing or unsafe")
    text = path.read_bytes().decode("utf-8")
    if not text.startswith(BEGIN + "\n") or not text.rstrip().endswith(END):
        raise DeploymentError("site template markers are invalid")
    return text.rstrip("\n") + "\n"


def render_config(original, block):
    """Replace only our marked site; all bytes outside the marker remain intact."""
    begin_matches = list(re.finditer(r"(?m)^" + re.escape(BEGIN) + r"\r?$", original))
    end_matches = list(re.finditer(r"(?m)^" + re.escape(END) + r"\r?$", original))
    if not begin_matches and not end_matches:
        if DOMAIN in original:
            raise DeploymentError("domain exists outside managed markers; refusing to alter it")
        separator = "\n" if original.endswith("\n") else "\n\n"
        return original + separator + block
    if len(begin_matches) != 1 or len(end_matches) != 1:
        raise DeploymentError("missing or duplicate managed-site marker")
    start = begin_matches[0].start()
    end = end_matches[0].end()
    if start >= end_matches[0].start():
        raise DeploymentError("managed-site marker order is invalid")
    if DOMAIN in original[:start] + original[end:]:
        raise DeploymentError("domain also exists outside managed markers")
    # Consume one existing newline; the template supplies exactly one of its own.
    if original[end:end + 1] == "\n":
        end += 1
    return original[:start] + block + original[end:]


def command(args, timeout=45):
    result = subprocess.run(args, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            text=True, timeout=timeout, check=False)
    if result.returncode:
        raise DeploymentError("command failed: {}\n{}".format(" ".join(args), result.stdout[-8000:]))
    return result.stdout


def require_directory(path, create=False, mode=0o755):
    """Check each absolute ancestor; refuse symlinks and non-root-writable trees."""
    if not path.is_absolute():
        raise DeploymentError("expected an absolute server path")
    current = Path(path.anchor)
    for part in path.parts[1:]:
        current = current / part
        if not current.exists() and not current.is_symlink() and create:
            current.mkdir(mode=mode if current == path else 0o755)
        info = current.lstat()
        if (not stat.S_ISDIR(info.st_mode) or info.st_uid != 0
                or stat.S_IMODE(info.st_mode) & 0o022):
            raise DeploymentError("directory is not a protected root-owned real directory: " + str(current))
    if path.resolve(strict=True) != path:
        raise DeploymentError("server path resolved outside its literal location")


def require_file(path, limit=MAX_CONFIG):
    info = path.lstat()
    if (not stat.S_ISREG(info.st_mode) or info.st_uid != 0 or info.st_nlink != 1
            or stat.S_IMODE(info.st_mode) & 0o022 or info.st_size > limit):
        raise DeploymentError("unsafe server file: " + str(path))
    return info


def sync_directory(path):
    fd = os.open(path, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)


def atomic_file(path, content, mode=0o600, owner=None):
    temporary = path.with_name("." + path.name + "." + uuid.uuid4().hex)
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW
    fd = os.open(temporary, flags, mode)
    try:
        with os.fdopen(fd, "wb") as output:
            output.write(content)
            output.flush()
            os.fsync(output.fileno())
            os.fchmod(output.fileno(), mode)
            if owner:
                os.fchown(output.fileno(), owner[0], owner[1])
        os.replace(temporary, path)
        sync_directory(path.parent)
    finally:
        if temporary.exists():
            temporary.unlink()


def save_json(path, data):
    atomic_file(path, (json.dumps(data, ensure_ascii=False, indent=2, sort_keys=True) + "\n").encode("utf-8"))


def current_target():
    current = BASE / "current"
    if not current.exists() and not current.is_symlink():
        return None
    info = current.lstat()
    if not stat.S_ISLNK(info.st_mode) or info.st_uid != 0:
        raise DeploymentError("current must be a root-owned symlink")
    target = Path(os.readlink(current))
    if not target.is_absolute():
        target = BASE / target
    if target.parent != RELEASES or not re.fullmatch(r"[0-9a-f]{40}", target.name):
        raise DeploymentError("current points outside the approved release directory")
    require_directory(target)
    return str(target)


def switch_current(target):
    if target is None:
        # Only removes this tool's validated symlink, never a directory or release.
        current_target()
        (BASE / "current").unlink(missing_ok=True)
    else:
        path = Path(target)
        if path.parent != RELEASES or not re.fullmatch(r"[0-9a-f]{40}", path.name):
            raise DeploymentError("refusing unsafe current target")
        require_directory(path)
        temporary = BASE / (".current-" + uuid.uuid4().hex)
        os.symlink(str(path), temporary)
        os.replace(temporary, BASE / "current")
    sync_directory(BASE)


def file_hashes(files):
    return {name: digest(data) for name, data in sorted(files.items())}


def read_release(path):
    require_directory(path)
    actual = set()
    files = {}
    for item in path.rglob("*"):
        info = item.lstat()
        relative = item.relative_to(path).as_posix()
        if stat.S_ISDIR(info.st_mode):
            require_directory(item)
            if relative != "assets":
                raise DeploymentError("unexpected release subdirectory")
            continue
        if relative not in ALLOWLIST:
            raise DeploymentError("unexpected file in release: " + relative)
        require_file(item, MAX_MEMBER)
        actual.add(relative)
        files[relative] = item.read_bytes()
    if actual != set(ALLOWLIST):
        raise DeploymentError("release does not contain exactly the public allowlist")
    return files


def install_release(commit, files, sha256):
    destination = RELEASES / commit
    if destination.exists() or destination.is_symlink():
        if file_hashes(read_release(destination)) != file_hashes(files):
            raise DeploymentError("existing immutable release differs; refusing overwrite")
    else:
        staging = RELEASES / (".staging-" + uuid.uuid4().hex)
        staging.mkdir(mode=0o755)
        (staging / "assets").mkdir(mode=0o755)
        for name, data in files.items():
            atomic_file(staging / name, data, 0o644)
        os.rename(staging, destination)
        sync_directory(RELEASES)
    record = {"commit": commit, "archive_sha256": sha256,
              "source": archive_url(commit), "files": file_hashes(files)}
    record_path = STATE / ("release-" + commit + ".json")
    if record_path.exists():
        require_file(record_path)
        if json.loads(record_path.read_bytes()) != record:
            raise DeploymentError("existing release metadata differs")
    else:
        save_json(record_path, record)
    return destination


@contextlib.contextmanager
def deployment_lock():
    import fcntl
    fd = os.open(LOCK, os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW, 0o600)
    try:
        info = os.fstat(fd)
        if not stat.S_ISREG(info.st_mode) or info.st_uid != 0 or info.st_nlink != 1:
            raise DeploymentError("unsafe deployment lock")
        try:
            fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as exc:
            raise DeploymentError("another Screenline deployment is running") from exc
        yield
    finally:
        os.close(fd)


def activate(commit, source_record, action):
    require_directory(CONFIG.parent)
    config_info = require_file(CONFIG)
    original = CONFIG.read_bytes()
    previous = current_target()
    desired = str(RELEASES / commit)
    candidate_bytes = render_config(original.decode("utf-8"), site_block()).encode("utf-8")
    identity = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S") + "-" + uuid.uuid4().hex[:12]
    candidate = CONFIG.with_name(".Caddyfile.screenline-" + identity + ".candidate")
    backup = STATE / (identity + ".Caddyfile.before")
    metadata_path = STATE / (identity + ".json")
    metadata = {"id": identity, "action": action, "commit": commit,
                "previous_current": previous, "new_current": desired,
                "config_before_sha256": digest(original),
                "config_after_sha256": digest(candidate_bytes),
                "config_backup": str(backup), "release": source_record,
                "created_utc": datetime.now(timezone.utc).isoformat(), "status": "preparing"}
    switched = False
    config_installed = False
    try:
        # Candidate in /etc/caddy preserves relative import resolution.
        atomic_file(candidate, candidate_bytes, stat.S_IMODE(config_info.st_mode),
                    (config_info.st_uid, config_info.st_gid))
        command(["caddy", "validate", "--config", str(candidate), "--adapter", "caddyfile"])
        command(["systemctl", "is-active", "--quiet", "caddy"])
        if CONFIG.read_bytes() != original or current_target() != previous:
            raise DeploymentError("configuration or current changed during preflight")
        atomic_file(backup, original)
        metadata["status"] = "validated"
        save_json(metadata_path, metadata)
        switch_current(desired)
        switched = True
        # Recheck the snapshot immediately before replacing the shared config.
        if CONFIG.read_bytes() != original:
            raise DeploymentError("Caddyfile changed before replacement")
        os.replace(candidate, CONFIG)
        sync_directory(CONFIG.parent)
        config_installed = True
        command(["systemctl", "reload", "caddy"])
        command(["systemctl", "is-active", "--quiet", "caddy"])
        metadata["status"] = "active"
        metadata["completed_utc"] = datetime.now(timezone.utc).isoformat()
        save_json(metadata_path, metadata)
        print(json.dumps({"status": "active", "commit": commit, "url": "https://" + DOMAIN,
                          "metadata": str(metadata_path)}, ensure_ascii=False))
    except BaseException as exc:
        rollback_errors = []
        if switched:
            try:
                if current_target() != desired:
                    raise DeploymentError("current changed externally; refusing to overwrite it")
                switch_current(previous)
            except Exception as rollback_exc:
                rollback_errors.append("current: " + str(rollback_exc))
        if config_installed:
            try:
                if CONFIG.read_bytes() != candidate_bytes:
                    raise DeploymentError("Caddyfile changed externally; restore backup manually")
                atomic_file(CONFIG, original, stat.S_IMODE(config_info.st_mode),
                            (config_info.st_uid, config_info.st_gid))
                command(["caddy", "validate", "--config", str(CONFIG), "--adapter", "caddyfile"])
                command(["systemctl", "reload", "caddy"])
            except Exception as rollback_exc:
                rollback_errors.append("Caddyfile/reload: " + str(rollback_exc))
        metadata["status"] = "rollback_failed" if rollback_errors else "failed_restored"
        metadata["error"] = str(exc)
        metadata["rollback_errors"] = rollback_errors
        save_json(metadata_path, metadata)
        if rollback_errors:
            raise DeploymentError("deployment failed; rollback needs attention: " + "; ".join(rollback_errors)) from exc
        raise
    finally:
        if candidate.exists():
            candidate.unlink()


def prepare_server():
    if sys.platform != "linux" or os.geteuid() != 0:
        raise DeploymentError("server changes require Linux root")
    require_directory(BASE, create=True)
    require_directory(RELEASES, create=True)
    require_directory(STATE, create=True, mode=0o700)
    os.chmod(STATE, 0o700)
    require_directory(LOCK.parent)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    actions = parser.add_subparsers(dest="action", required=True)
    for action in ("inspect", "deploy"):
        child = actions.add_parser(action)
        child.add_argument("--commit", type=full_commit, required=True)
        child.add_argument("--sha256", type=sha256_value, required=True)
        child.add_argument("--archive", help="optional pre-downloaded exact commit .tar.gz")
    child = actions.add_parser("rollback")
    child.add_argument("--commit", type=full_commit, required=True)
    args = parser.parse_args()
    try:
        if args.action == "rollback":
            prepare_server()
            with deployment_lock():
                record_path = STATE / ("release-" + args.commit + ".json")
                require_file(record_path)
                record = json.loads(record_path.read_bytes())
                if record["commit"] != args.commit or record["files"] != file_hashes(read_release(RELEASES / args.commit)):
                    raise DeploymentError("rollback release does not match saved metadata")
                if BEGIN not in CONFIG.read_text(encoding="utf-8"):
                    raise DeploymentError("rollback requires an existing managed site")
                activate(args.commit, record, "rollback")
        else:
            with tempfile.TemporaryDirectory(prefix="screenline-archive-") as folder:
                archive = Path(folder) / "source.tar.gz"
                obtain_archive(args.commit, args.sha256, args.archive, archive)
                files = read_runtime_files(archive, args.commit)
            if args.action == "inspect":
                print(json.dumps({"commit": args.commit, "archive_sha256": args.sha256,
                                  "source": archive_url(args.commit), "files": file_hashes(files)}, indent=2))
                print(site_block())
            else:
                prepare_server()
                with deployment_lock():
                    install_release(args.commit, files, args.sha256)
                    record = json.loads((STATE / ("release-" + args.commit + ".json")).read_bytes())
                    activate(args.commit, record, "deploy")
    except (DeploymentError, OSError, ValueError, tarfile.TarError, subprocess.SubprocessError) as exc:
        print("ERROR: " + str(exc), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
