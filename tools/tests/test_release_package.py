import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from release_package import GitHub, publish


class FakeGitHub:
    def __init__(self, existing=None, tag=None):
        self.existing, self.tag, self.created = existing, tag, []
        self.content = {}

    def release(self, version):
        return self.existing

    def tag_commit(self, version):
        return self.tag

    def download(self, version, name, directory):
        (Path(directory) / name).write_bytes(self.content[name])

    def create(self, version, commit, files, notes):
        self.created.append((version, commit))
        self.content = {p.name: p.read_bytes() for p in files}


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.package = Path(self.tmp.name) / 'verified.zip'
        self.package.write_bytes(b'independently verified package fixture')
        self.notes = Path(self.tmp.name) / 'notes.md'
        self.notes.write_text('QA scope', encoding='utf-8')
        self.digest = hashlib.sha256(self.package.read_bytes()).hexdigest()
        self.commit = 'a' * 40

    def call(self, gh, **kwargs):
        return publish('V1.7.12', self.package, self.digest, self.commit,
                       self.notes, github=gh, **kwargs)

    def existing(self, digest=True):
        asset = {'name': 'SMB.Manager.V1.7.12.zip'}
        if digest:
            asset['digest'] = 'sha256:' + self.digest
        gh = FakeGitHub({'assets': [asset], 'draft': False, 'prerelease': False})
        gh.content[asset['name']] = self.package.read_bytes()
        return gh

    def test_identical_asset_is_downloaded_and_never_reuploaded(self):
        gh = self.existing()
        self.assertIn('unchanged', self.call(gh, execute=True))
        self.assertEqual(gh.created, [])

    def test_conflicting_remote_digest_cannot_be_overwritten(self):
        gh = self.existing()
        gh.existing['assets'][0]['digest'] = 'sha256:' + '0' * 64
        with self.assertRaisesRegex(ValueError, 'different bytes'):
            self.call(gh, execute=True)
        self.assertEqual(gh.created, [])

    def test_download_checked_when_remote_digest_missing(self):
        gh = self.existing(digest=False)
        gh.content['SMB.Manager.V1.7.12.zip'] = b'tampered'
        with self.assertRaisesRegex(ValueError, 'download differs'):
            self.call(gh, execute=True)
        self.assertEqual(gh.created, [])

    def test_moved_source_tag_is_rejected(self):
        gh = FakeGitHub(tag='b' * 40)
        with self.assertRaisesRegex(ValueError, 'never move'):
            self.call(gh, execute=True)
        self.assertEqual(gh.created, [])

    def test_dry_run_cannot_create_release(self):
        gh = FakeGitHub()
        self.assertIn('no release created', self.call(gh))
        self.assertEqual(gh.created, [])

    def test_new_release_has_source_binding_and_verified_downloads(self):
        gh = FakeGitHub()
        self.assertIn('hashes verified', self.call(gh, execute=True))
        self.assertEqual(gh.created, [('V1.7.12', self.commit)])
        meta = json.loads(gh.content['V1.7.12-release.json'])
        self.assertEqual(meta['sourceCommit'], self.commit)
        self.assertFalse(meta['latestPromotion'])
        self.assertEqual(meta['artifact']['sha256'], self.digest)

    def test_bad_local_hash_blocks_before_any_write(self):
        gh = FakeGitHub()
        self.package.write_bytes(b'changed after QA')
        with self.assertRaisesRegex(ValueError, 'hash mismatch'):
            self.call(gh, execute=True)
        self.assertEqual(gh.created, [])

    def test_2x_cannot_bypass_its_qa_gates(self):
        with self.assertRaisesRegex(ValueError, 'separate QA gates'):
            publish('v2.0.0-beta.5-build26', self.package, self.digest,
                    self.commit, self.notes, github=FakeGitHub(), execute=True)

    def test_transport_error_is_not_a_missing_release(self):
        result = type('Result', (), {'returncode': 1, 'stderr': 'HTTP 403 forbidden'})()
        with patch('release_package.subprocess.run', return_value=result):
            with self.assertRaises(RuntimeError):
                GitHub().release('V1.7.12')

    def test_prerelease_cannot_be_replaced_with_stable(self):
        gh = self.existing()
        gh.existing['prerelease'] = True
        with self.assertRaisesRegex(ValueError, 'stable release'):
            self.call(gh, execute=True)
        self.assertEqual(gh.created, [])


if __name__ == '__main__':
    unittest.main()
