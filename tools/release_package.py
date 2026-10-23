"""Publish verified legacy 1.x packages without replacing assets or moving tags."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import tempfile

REPOSITORY = "droidbin/SMB-Manager-for-KCAA"


def sha256(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def run(*args):
    result = subprocess.run(args, capture_output=True, text=True, encoding="utf-8")
    if result.returncode:
        raise RuntimeError(result.stderr.strip() or f"Command failed: {args[0]}")
    return result.stdout.strip()


class GitHub:
    def api(self, path):
        result = subprocess.run(["gh", "api", path], capture_output=True, text=True, encoding="utf-8")
        if result.returncode:
            if "(HTTP 404)" in result.stderr:
                return None
            raise RuntimeError(result.stderr.strip())
        return json.loads(result.stdout)

    def release(self, version):
        return self.api(f"repos/{REPOSITORY}/releases/tags/{version}")

    def tag_commit(self, version):
        ref = self.api(f"repos/{REPOSITORY}/git/ref/tags/{version}")
        if ref is None:
            return None
        obj = ref["object"]
        for _ in range(10):
            if obj["type"] == "commit":
                return obj["sha"]
            if obj["type"] != "tag":
                raise ValueError("Tag does not resolve to a commit")
            obj = self.api(f"repos/{REPOSITORY}/git/tags/{obj['sha']}")["object"]
        raise ValueError("Too many annotated tag layers")

    def download(self, version, name, directory):
        run("gh", "release", "download", version, "--repo", REPOSITORY,
            "--pattern", name, "--dir", str(directory))

    def create(self, version, commit, files, notes):
        run("gh", "release", "create", version, *(str(f) for f in files),
            "--repo", REPOSITORY, "--target", commit, "--latest=false",
            "--title", f"SMB Manager {version} · 정식", "--notes-file", str(notes))


def publish(version, package, expected, commit, notes, *, github=None, execute=False):
    if not re.fullmatch(r"V1\.\d+\.\d+", version):
        raise ValueError("Only legacy 1.x is supported; 2.x requires its separate QA gates")
    if not re.fullmatch(r"[a-fA-F0-9]{40}", commit):
        raise ValueError("An exact source commit is required")
    if not re.fullmatch(r"[a-fA-F0-9]{64}", expected):
        raise ValueError("The independently verified package SHA256 is required")
    package = Path(package)
    if package.suffix.lower() != ".zip" or sha256(package) != expected.lower():
        raise ValueError("Verified package hash mismatch")
    if not Path(notes).is_file():
        raise ValueError("Release notes are required")
    github = github or GitHub()
    asset_name = f"SMB.Manager.{version}.zip"
    existing = github.release(version)
    if existing is not None:
        if existing.get("draft") or existing.get("prerelease"):
            raise ValueError("Existing release is not a published stable release")
        assets = [a for a in existing["assets"] if a["name"] == asset_name]
        if len(assets) != 1:
            raise ValueError("Expected existing asset missing or duplicated; preserve release")
        digest = assets[0].get("digest")
        if digest and digest != "sha256:" + expected.lower():
            raise ValueError("Existing asset has different bytes; use a new version")
        with tempfile.TemporaryDirectory() as tmp:
            github.download(version, asset_name, tmp)
            if sha256(Path(tmp) / asset_name) != expected.lower():
                raise ValueError("Existing download differs; use a new version")
        return "unchanged: existing package download verified"
    tag_commit = github.tag_commit(version)
    if tag_commit is not None and tag_commit.lower() != commit.lower():
        raise ValueError("Existing tag points to another source commit; never move it")
    if not execute:
        return "validated: no release created (add --publish to publish)"
    with tempfile.TemporaryDirectory() as tmp:
        asset = Path(tmp) / asset_name
        shutil.copyfile(package, asset)
        metadata = Path(tmp) / f"{version}-release.json"
        metadata.write_text(json.dumps({"schema": 1, "version": version,
            "repository": REPOSITORY, "sourceCommit": commit,
            "artifact": {"name": asset_name, "sha256": expected.lower()},
            "scope": "legacy-1.x", "latestPromotion": False}, indent=2) + "\n", encoding="utf-8")
        sums = Path(tmp) / f"{version}-SHA256SUMS.txt"
        sums.write_text(f"{expected.lower()}  {asset.name}\n{sha256(metadata)}  {metadata.name}\n", encoding="utf-8")
        github.create(version, commit, [asset, metadata, sums], notes)
        download = Path(tmp) / "download"
        download.mkdir()
        for file in (asset, metadata, sums):
            github.download(version, file.name, download)
            if sha256(download / file.name) != sha256(file):
                raise ValueError("Published download hash mismatch; do not promote")
    return "published and downloaded hashes verified; Latest and catalogs unchanged"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", required=True)
    parser.add_argument("--package", required=True)
    parser.add_argument("--expected-sha256", required=True)
    parser.add_argument("--source-commit", required=True)
    parser.add_argument("--notes-file", required=True)
    parser.add_argument("--publish", action="store_true")
    args = parser.parse_args()
    if run("git", "rev-parse", "HEAD").lower() != args.source_commit.lower():
        raise ValueError("Check out the exact source commit before publishing")
    source = f"src/legacy/SMB Manager {args.version}/source"
    run("git", "cat-file", "-e", f"{args.source_commit}:{source}/Program.cs")
    version_ini = run("git", "show", f"{args.source_commit}:src/legacy/SMB Manager {args.version}/version.ini")
    program = run("git", "show", f"{args.source_commit}:{source}/Program.cs")
    if not re.search(r"(?m)^Version=" + re.escape(args.version) + r"\s*$", version_ini):
        raise ValueError("Historical source folder does not match its recorded version")
    if f'CurrentVersionText = "{args.version}"' not in program:
        raise ValueError("Application source version does not match the requested release")
    run("git", "diff", "--exit-code", "HEAD", "--", source,
        f"src/legacy/SMB Manager {args.version}/version.ini", "assets/fonts")
    print(publish(args.version, args.package, args.expected_sha256, args.source_commit,
                  args.notes_file, execute=args.publish))


if __name__ == "__main__":
    main()
