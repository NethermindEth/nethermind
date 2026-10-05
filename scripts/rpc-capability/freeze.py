"""Freeze an independently accepted read-only inventory before capability dispatch."""
import argparse
import hashlib
import json
from pathlib import Path
import re

HERE=Path(__file__).resolve().parent
HARNESS='420c992e6d8c8e2b1abae899cece932da7e9e47b'
INVENTORY_HEAD='99a991c2793da89cc617b8541b9d7d806075f428'
HEAD_HASH='0x1bcc8cd8ce5471e5c25f9a4b5c711ce11d37445eb24be3e0b7ee57b048000933'


def freeze(source, approved_digest):
    data=source.read_bytes()
    if not re.fullmatch('[0-9a-f]{64}',approved_digest) or hashlib.sha256(data).hexdigest()!=approved_digest:
        raise ValueError('INDEPENDENTLY_APPROVED_INVENTORY_REQUIRED')
    inventory=json.loads(data)
    if (inventory.get('schema')!=1 or inventory.get('status')!='PROSPECTIVE_INVENTORY_NOT_MEASUREMENT'
            or inventory.get('source_head')!=INVENTORY_HEAD or inventory.get('snapshot_read_stable') is not True
            or inventory.get('resources_clear_before_after') is not True or inventory.get('expected_head_hash')!=HEAD_HASH
            or inventory.get('head_verified_from_snapshot') is not False
            or inventory.get('historical_corpus_equivalence_claimed') is not False
            or inventory.get('historical_fingerprint_equivalence_claimed') is not False
            or inventory.get('corpus',{}).get('records')!=497):
        raise ValueError('COMPLETE_PROSPECTIVE_INVENTORY_REQUIRED')
    for value in (inventory['corpus']['sha256'],inventory['snapshot_fingerprint_sha256']):
        if not re.fullmatch('[0-9a-f]{64}',value):raise ValueError('EXACT_INPUT_DIGEST_REQUIRED')
    expected={'schema':1,'provenance_kind':'PROSPECTIVE_FREEZE','inventory_sha256':approved_digest,
              'inventory_run_id':inventory['run_id'],'inventory_attempt':inventory['attempt'],
              'inventory_head':inventory['source_head'],'existing_anchor_matched':inventory['existing_anchor_matched'],
              'baseline_image':'nethermindeth/nethermind@sha256:a6e80b5688b383d2b60bc5c45ba2b4b200f80d3624ad9b7eddd662496bb8909b',
              'baseline_source':'9bb024e1e84d370a3a27fad38c3b19717e6f67ea','runtime_version':'10.0.12',
              'corpus_sha256':inventory['corpus']['sha256'],'snapshot_fingerprint_sha256':inventory['snapshot_fingerprint_sha256'],
              'snapshot_head_hash':HEAD_HASH,'historical_byte_equivalence_claimed':False}
    # Exclusive creation prevents replacing an accepted freeze with observations from a later attempt.
    with (HERE/'expected-pins.json').open('x',encoding='utf-8',newline='\n') as target:json.dump(expected,target,indent=2)
    names=['guard.py','launch.py','freeze.py','test_guard.py','workflow.yml','expected-pins.json',
           'native/native_capability.py','native/pidfd_compat.py','native/source-pins.json',
           'native/harness-pin.json','native/test_native_capability.py']
    files={name:hashlib.sha256((HERE/name).read_bytes()).hexdigest() for name in names}
    with (HERE/'release-freeze.json').open('x',encoding='utf-8',newline='\n') as target:
        json.dump({'schema':1,'status':'READY_FOR_REVIEWED_PROSPECTIVE_CAPABILITY','harness_head':HARNESS,
                   'inventory_sha256':approved_digest,'files':files},target,indent=2)


if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--inventory',type=Path,required=True)
    parser.add_argument('--approved-inventory-sha256',required=True);args=parser.parse_args()
    freeze(args.inventory,args.approved_inventory_sha256)
