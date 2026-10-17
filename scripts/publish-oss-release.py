#!/usr/bin/env python3
"""Publish a tested stable release to OSS; commit stable/latest.json last.

Uses Alibaba Cloud's official oss2 SDK with V4 STS authentication. No bucket,
ACL, domain, RAM role or resource is created by this script. Credentials are
read only from the environment exported by the official OIDC action.
"""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import sys
import unicodedata
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET

STABLE_TAG = re.compile(r"v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\Z")
MAX_MANIFEST = 128 * 1024


def configuration():
    names = ("OSS_BUCKET", "OSS_REGION", "SCREENLINE_UPDATE_BASE_URL", "SCREENLINE_UPDATE_PUBLIC_KEY")
    result = {name: os.environ.get(name, "").strip() for name in names}
    if not all(result.values()):
        raise ValueError("Required OSS mirror variables are missing")
    bucket, region = result["OSS_BUCKET"], result["OSS_REGION"]
    if not re.fullmatch(r"[a-z0-9][a-z0-9-]{1,61}[a-z0-9]", bucket):
        raise ValueError("Invalid OSS_BUCKET")
    if not re.fullmatch(r"[a-z]+(?:-[a-z0-9]+)+", region):
        raise ValueError("OSS_REGION must be the region ID, for example cn-hangzhou")
    base = urllib.parse.urlsplit(result["SCREENLINE_UPDATE_BASE_URL"])
    if (base.scheme != "https" or not base.hostname or base.username or base.password
            or base.query or base.fragment or base.port not in (None, 443)
            or not base.path.endswith("/") or not re.fullmatch(r"/(?:[A-Za-z0-9_-]+/)*", base.path)):
        raise ValueError("Invalid HTTPS update BaseUrl or object prefix")
    result["prefix"] = base.path.lstrip("/")
    result["endpoint"] = "https://oss-" + region + ".aliyuncs.com"
    return result


def verify_manifest(data, public_key):
    """Verify the exact bytes signed by the PowerShell producer before parsing."""
    if len(data) > MAX_MANIFEST:
        raise ValueError("Manifest exceeds size limit")
    envelope = json.loads(data.decode("utf-8"))
    if not isinstance(envelope, dict) or set(envelope) != {"payload", "signature"}:
        raise ValueError("Invalid manifest envelope")
    payload = base64.b64decode(envelope["payload"], validate=True)
    signature = base64.b64decode(envelope["signature"], validate=True)
    if len(payload) > 64 * 1024 or len(public_key) > 16384:
        raise ValueError("Payload or public key exceeds client size limits")
    if "<!" in public_key:
        raise ValueError("Public key must not contain XML declarations or entities")
    key_xml = ET.fromstring(public_key)
    if key_xml.tag != "RSAKeyValue" or sorted(item.tag for item in key_xml) != ["Exponent", "Modulus"]:
        raise ValueError("Only public RSA Modulus and Exponent may be configured")
    n = int.from_bytes(base64.b64decode(key_xml.findtext("Modulus"), validate=True), "big")
    e = int.from_bytes(base64.b64decode(key_xml.findtext("Exponent"), validate=True), "big")
    if n.bit_length() < 2048 or n.bit_length() > 8192:
        raise ValueError("RSA key must be between 2048 and 8192 bits")
    from Crypto.Hash import SHA256
    from Crypto.PublicKey import RSA
    from Crypto.Signature import pkcs1_15
    pkcs1_15.new(RSA.construct((n, e))).verify(SHA256.new(payload), signature)
    manifest = json.loads(payload.decode("utf-8"))
    required = {"schema", "tag", "installer", "size", "sha256", "notes"}
    if not isinstance(manifest, dict) or set(manifest) != required or type(manifest["schema"]) is not int or manifest["schema"] != 1:
        raise ValueError("Unsupported manifest payload")
    tag = manifest["tag"]
    if not isinstance(tag, str) or len(tag) > 256 or not STABLE_TAG.fullmatch(tag):
        raise ValueError("Only stable version tags may update the domestic latest pointer")
    if manifest["installer"] != "Screenline-" + tag + "-windows-x64-setup.exe":
        raise ValueError("Unexpected installer asset name")
    if type(manifest["size"]) is not int or not 2 <= manifest["size"] <= 256 * 1024 * 1024:
        raise ValueError("Installer size is out of range")
    if not isinstance(manifest["sha256"], str) or not re.fullmatch(r"[0-9a-f]{64}", manifest["sha256"]):
        raise ValueError("Invalid installer SHA-256")
    if not isinstance(manifest["notes"], str) or len(manifest["notes"].encode("utf-16-le")) // 2 > 16384:
        raise ValueError("Invalid release notes")
    if any(unicodedata.category(character) == "Cc" and character not in "\r\n\t" for character in manifest["notes"]):
        raise ValueError("Release notes contain unsupported control characters")
    return manifest


def sha256_file(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def local_assets(directory, manifest):
    tag = manifest["tag"]
    names = [manifest["installer"], "Screenline-" + tag + "-windows-x64.zip",
             "Screenline-" + tag + "-source.zip", "SHA256SUMS.txt", "release-notes.txt"]
    assets = {}
    for name in names:
        path = directory / name
        if not path.is_file() or path.is_symlink() or path.stat().st_size <= 0:
            raise ValueError("A required release asset is missing or empty: " + name)
        assets[name] = (path, path.stat().st_size, sha256_file(path))
    expected_checksums = {name: digest for name, (_, _, digest) in assets.items() if name not in ("SHA256SUMS.txt", "release-notes.txt")}
    parsed = {}
    for line in (directory / "SHA256SUMS.txt").read_text(encoding="utf-8-sig").splitlines():
        match = re.fullmatch(r"([0-9a-f]{64})  ([A-Za-z0-9.+-]+)", line)
        if not match or match[2] in parsed:
            raise ValueError("Checksum file is malformed")
        parsed[match[2]] = match[1]
    if parsed != expected_checksums:
        raise ValueError("A release asset does not match SHA256SUMS.txt")
    _, size, digest = assets[manifest["installer"]]
    if size != manifest["size"] or digest != manifest["sha256"]:
        raise ValueError("Signed manifest does not match the installer")
    if (directory / "release-notes.txt").read_bytes() != (manifest["notes"] + "\n").encode("utf-8"):
        raise ValueError("Signed manifest does not match the release notes")
    return assets


class OssStorage:
    def __init__(self, config):
        import oss2
        names = ("ALIBABA_CLOUD_ACCESS_KEY_ID", "ALIBABA_CLOUD_ACCESS_KEY_SECRET", "ALIBABA_CLOUD_SECURITY_TOKEN")
        credentials = [os.environ.get(name, "") for name in names]
        if not all(credentials):
            raise ValueError("OIDC STS credentials are required; permanent AccessKeys are not accepted")
        auth = oss2.StsAuth(*credentials, auth_version="v4")
        self.bucket = oss2.Bucket(auth, config["endpoint"], config["OSS_BUCKET"], region=config["OSS_REGION"], connect_timeout=30)
        self.no_such_key = oss2.exceptions.NoSuchKey

    def read(self, key, maximum):
        try:
            response = self.bucket.get_object(key)
        except self.no_such_key:
            return None
        try:
            data = response.read(maximum + 1)
            if len(data) > maximum:
                raise ValueError("Remote object exceeds expected size")
            return data
        finally:
            response.close()

    def put(self, key, path, content_type, immutable):
        headers = {"Content-Type": content_type,
                   "Cache-Control": "public, max-age=31536000, immutable" if immutable else "no-cache, max-age=0, must-revalidate"}
        if immutable:
            headers["x-oss-forbid-overwrite"] = "true"
        self.bucket.put_object_from_file(key, str(path), headers=headers)


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, message, headers, new_url):
        raise ValueError("Public mirror URL redirected; use the final HTTPS origin")


def public_readback(url, expected_size, expected_hash):
    request = urllib.request.Request(url, headers={"User-Agent": "Screenline-Release-Verification/1", "Cache-Control": "no-cache"})
    digest = hashlib.sha256()
    total = 0
    with urllib.request.build_opener(NoRedirect).open(request, timeout=60) as response:
        if response.status != 200:
            raise ValueError("Public update object is not readable")
        for chunk in iter(lambda: response.read(1024 * 1024), b""):
            total += len(chunk)
            if total > expected_size:
                raise ValueError("Public mirror returned too much data")
            digest.update(chunk)
    if total != expected_size or digest.hexdigest() != expected_hash:
        raise ValueError("Public mirror object size or checksum mismatch")


def publish(config, directory, manifest_path, storage, readback=public_readback):
    signed_bytes = manifest_path.read_bytes()
    manifest = verify_manifest(signed_bytes, config["SCREENLINE_UPDATE_PUBLIC_KEY"])
    assets = local_assets(directory, manifest)
    latest_key = config["prefix"] + "stable/latest.json"
    old_bytes = storage.read(latest_key, MAX_MANIFEST)
    if old_bytes is not None:
        previous = verify_manifest(old_bytes, config["SCREENLINE_UPDATE_PUBLIC_KEY"])
        old_version = tuple(map(int, STABLE_TAG.fullmatch(previous["tag"]).groups()))
        new_version = tuple(map(int, STABLE_TAG.fullmatch(manifest["tag"]).groups()))
        if old_version > new_version:
            raise ValueError("Refusing to replace a newer stable update manifest")
        if old_version == new_version and previous != manifest:
            raise ValueError("Refusing to replace an existing version with different contents")
    release_prefix = "releases/" + manifest["tag"] + "/"
    for name, (path, size, digest) in assets.items():
        key = config["prefix"] + release_prefix + name
        existing = storage.read(key, size)
        if existing is not None:
            if len(existing) != size or hashlib.sha256(existing).hexdigest() != digest:
                raise ValueError("Refusing to overwrite an immutable version asset")
        else:
            content_type = "text/plain; charset=utf-8" if name.endswith(".txt") else "application/octet-stream"
            storage.put(key, path, content_type, immutable=True)
            received = storage.read(key, size)
            if received is None or len(received) != size or hashlib.sha256(received).hexdigest() != digest:
                raise ValueError("OSS readback verification failed")
        # Anonymous readback verifies the exact URL used by clients, including custom domain/CDN routing.
        readback(config["SCREENLINE_UPDATE_BASE_URL"] + release_prefix + urllib.parse.quote(name, safe=""), size, digest)
        print("Verified version asset: " + name)
    # All versioned objects now exist, are immutable and are publicly readable.
    # No exception above can modify the previous latest pointer.
    storage.put(latest_key, manifest_path, "application/json; charset=utf-8", immutable=False)
    if storage.read(latest_key, MAX_MANIFEST) != signed_bytes:
        raise ValueError("Latest manifest readback verification failed")
    readback(config["SCREENLINE_UPDATE_BASE_URL"] + "stable/latest.json", len(signed_bytes), hashlib.sha256(signed_bytes).hexdigest())
    print("Published signed stable update manifest: " + manifest["tag"])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifact-directory", type=Path, default=Path("dist"))
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--verify-only", action="store_true", help="Verify the signed local bundle without credentials or network requests")
    options = parser.parse_args()
    config = configuration()
    # Validate every local byte before obtaining credentials or issuing any cloud request.
    manifest = verify_manifest(options.manifest.read_bytes(), config["SCREENLINE_UPDATE_PUBLIC_KEY"])
    local_assets(options.artifact_directory, manifest)
    if options.verify_only:
        import oss2
        # Construct an SDK V4 signer with fixture credentials; constructors issue no requests.
        auth = oss2.StsAuth("offline-id", "offline-secret", "offline-token", auth_version="v4")
        oss2.Bucket(auth, config["endpoint"], config["OSS_BUCKET"], region=config["OSS_REGION"], connect_timeout=30)
        print("Signed local bundle and official OSS SDK V4 initialization verified; no cloud request issued.")
        return
    publish(config, options.artifact_directory, options.manifest, OssStorage(config))


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        # SDK exceptions can contain request details. Never print tokens or signed headers.
        print("OSS mirror publication failed (" + type(error).__name__ + "). Check configuration, permissions and object availability; GitHub Release is unaffected.", file=sys.stderr)
        sys.exit(1)
