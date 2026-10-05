"""Offline fixtures for owned-marker and missing/unknown result gates."""
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import guard


class GuardTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup)
        self.shared=Path(self.temp.name).resolve()
        self.env=patch.dict(os.environ,{'GITHUB_RUN_ID':'123','GITHUB_RUN_ATTEMPT':'1'})
        self.env.start();self.addCleanup(self.env.stop)
        self.shared_patch=patch.object(guard,'SHARED',self.shared);self.shared_patch.start();self.addCleanup(self.shared_patch.stop)
        self.storage,self.lock=guard.paths()
        for path in (self.storage,self.lock):path.mkdir(mode=0o700)
        self.owner={'schema':1,'run_id':'123','attempt':'1','storage':str(self.storage),
                    'storage_identity':guard.directory(self.storage),'lock_identity':guard.directory(self.lock)}
        (self.lock/'owner.json').write_text(json.dumps(self.owner))

    def record(self,changes=None):
        value={'schema':1,'scope':'NATIVE_LIFECYCLE_ONLY','capability':'PASS','full_abba_quality':'FAIL',
               'timing_result':False,'pooling_eligibility':'UNKNOWN','raw_sources_retained':True,
               'archive_ready':True,'ownership':'COMMANDS_CLOSED',
               'outcomes':{name:1 if name=='finalize' else 0 for name in guard.REQUIRED_CALLS}}
        value.update(changes or {})
        (self.storage/'native-capability.json').write_text(json.dumps(value))

    def test_exact_owner_marker_required(self):
        self.assertEqual(guard.owned(self.storage,self.lock),self.owner)
        for changes in ({'run_id':'999'},{'storage_identity':[0,0]},{'lock_identity':[0,0]}):
            with self.subTest(changes=changes):
                (self.lock/'owner.json').write_text(json.dumps(self.owner|changes))
                with self.assertRaisesRegex(ValueError,'FOREIGN_OWNER_MARKER'):guard.owned(self.storage,self.lock)
                self.assertTrue(self.lock.exists())

    def test_missing_result_blocks_publication_and_release_without_mutation(self):
        for operation in (guard.publish,guard.release):
            with self.subTest(operation=operation.__name__):
                with self.assertRaises(FileNotFoundError):operation(self.storage,self.lock)
                self.assertTrue((self.lock/'owner.json').exists())
                self.assertEqual(list(self.storage.iterdir()),[])

    def test_hold_marker_dominates_nominal_pass(self):
        self.record();(self.storage/'native-ownership-hold.json').write_text('{}')
        with self.assertRaisesRegex(ValueError,'OWNERSHIP_HOLD'):guard.success(self.storage,self.lock)
        self.assertTrue(self.lock.exists())

    def test_unknown_failed_or_misreported_quality_never_opens_gate(self):
        for changes in ({'ownership':'UNKNOWN_HOLD_NO_CLEANUP_OR_NEXT_DISPATCH'},{'capability':'FAIL'},
                        {'full_abba_quality':'PASS'},{'timing_result':True},{'archive_ready':False}):
            with self.subTest(changes=changes):
                self.record(changes)
                with self.assertRaisesRegex(ValueError,'NATIVE_SUCCESS_REQUIRED'):guard.success(self.storage,self.lock)
                self.assertTrue(self.lock.exists())

    def test_partial_or_nonzero_child_outcomes_block_gate(self):
        for outcomes in ({},{name:0 for name in guard.REQUIRED_CALLS}):
            with self.subTest(outcomes=outcomes):
                self.record({'outcomes':outcomes})
                with self.assertRaisesRegex(ValueError,'CLOSED_SUCCESSFUL_CALLS_REQUIRED'):guard.success(self.storage,self.lock)



    def test_verified_success_publishes_only_summary_and_releases_own_lock(self):
        self.record()
        root=self.storage/'spin-diagnostic-123-1';root.mkdir(mode=0o700)
        integration={'expected_pins':{'provenance_kind':'PROSPECTIVE_FREEZE','corpus_sha256':'a'*64}}
        (self.storage/'integration-freeze.json').write_text(json.dumps(integration))
        (root/'integration-freeze.json').write_text(json.dumps(integration))
        (root/'prospective-expected-pins.json').write_text(json.dumps(integration['expected_pins']))
        public=self.storage/'rpc-private-public-123-1';public.mkdir(mode=0o700)
        encrypted=public/'encrypted';encrypted.mkdir(mode=0o700)
        quality={'quality':'FAIL','expected_arms':4,'observed_arms':1,'valid_arms':1,'pooling_eligibility':'UNKNOWN'}
        for folder in (root,public):(folder/'audit-status.json').write_text(json.dumps(quality))
        (encrypted/'raw.tar.gz.cms').write_bytes(b'ciphertext fixture')
        (encrypted/'recipient.crt').write_bytes(b'public certificate fixture')
        archive={'status':'ENCRYPTED_NOT_DECRYPTION_VERIFIED','run_id':123,'run_attempt':1,'raw_source_deleted':False,
                 'ciphertext':{'file':'raw.tar.gz.cms','bytes':18,'sha256':guard.digest(encrypted/'raw.tar.gz.cms')},
                 'certificate_pem_sha256':guard.digest(encrypted/'recipient.crt')}
        (encrypted/'archive.json').write_text(json.dumps(archive))
        output=self.shared/'step-output';output.write_text('')
        with patch.dict(os.environ,{'GITHUB_OUTPUT':str(output),'UPLOADED_ARTIFACT_ID':'321','UPLOADED_ARTIFACT_DIGEST':'a'*64}):
            guard.publish(self.storage,self.lock)
            self.assertEqual(guard.read(public/'native-capability.json')['full_abba_quality'],'FAIL')
            self.assertIn('ready=true',output.read_text())
            guard.release(self.storage,self.lock)
        self.assertFalse(self.lock.exists())
        self.assertTrue(root.exists())
        self.assertTrue((self.storage/'native-capability.json').exists())
        self.assertTrue((self.storage/'upload-receipt.json').exists())

if __name__=='__main__':unittest.main()

