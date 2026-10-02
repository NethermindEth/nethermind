"""Constrain the diagnostic branch to the approved single-image realblocks replay."""
import copy
import hashlib
import json
import os
from pathlib import Path
import stat

from export_corpus_identity import export

EXPB_SHA = '797db21d602901e35dad6c2c5aa7b570f614d7ff'
IMAGE = 'nethermindeth/nethermind@sha256:4d2c2d915cb960efc0f0e07bcdf25d268c515e0423aa182ec67ac07ac196fa6e'
FLAGS = 'EXPB_SCHED_CAPTURE=1,EXPB_DIAGNOSTIC_RUNTIME_TRACE=1,EXPB_DIAGNOSTIC_REQUESTS=1,EXPB_DIAGNOSTIC_CLIENT_IDENTITY=1'


def prepare(config, scenario, sample_directory):
    config = copy.deepcopy(config)
    settings = config['scenarios'][scenario]
    if settings['image'] != IMAGE or int(settings['amount']) != 1000:
        raise ValueError('diagnostic replay requires the pinned image and 1000 realblocks')
    if settings.get('client') != 'nethermind' or settings.get('snapshot_backend') != 'overlay':
        raise ValueError('diagnostic replay requires Nethermind on an isolated snapshot overlay')
    if int(settings.get('warmup', -1)) != 3 or float(settings.get('delay', -1)) != 0:
        raise ValueError('diagnostic replay requires warmup3/delay0')
    if settings.get('extra_env', {}) not in ({}, {'DOTNET_ThreadPool_UnfairSemaphoreSpinLimit': '0'}):
        raise ValueError('unexpected client env for diagnostic replay')
    if os.environ.get('EXPB_ENV_PASSTHROUGH') != FLAGS:
        raise ValueError('unexpected diagnostic collectors')
    if os.environ.get('DOTTRACE') != 'false' or os.environ.get('PERF') != 'false':
        raise ValueError('additional profiling not enabled in this diagnostic')
    if os.environ.get('MEASUREMENT_MODE') != 'standard':
        raise ValueError('diagnostic requires standard replay')
    resources = config['resources']
    if resources['cpu'] != 0 or resources['cpuset'] != '2,3,4,5,10,11,12,13' or resources['infra_cpuset'] != '1,9':
        raise ValueError('diagnostic CPU budget differs')
    root = Path(os.environ['RUNNER_TEMP']).resolve(strict=True) / ('spin-diagnostic-' + os.environ['GITHUB_RUN_ID'] + '-' + os.environ['GITHUB_RUN_ATTEMPT'])
    root.mkdir(mode=0o700, exist_ok=False)
    if stat.S_IMODE(root.stat().st_mode) != 0o700:
        raise ValueError('diagnostic root is not private')
    outputs = root / 'outputs'
    outputs.mkdir(mode=0o700)
    (root / 'preparation.json').write_text(json.dumps({'status': 'PREPARED_NOT_MEASURED',
        'phase': 'INITIALIZING', 'outputs': str(outputs)}) + '\n', encoding='utf-8')
    config['paths']['outputs'] = str(outputs)
    source_map = root / 'source-map'
    identity = export(settings['payloads'], settings['fcus'], source_map, 3, 1000, EXPB_SHA)
    snapshot = Path(settings['snapshot_source']).resolve(strict=True)
    if snapshot != Path('/mnt/sda/nethermind-flat-snapshot'):
        raise ValueError('unexpected snapshot source')
    rows = []
    for directory, folders, files in os.walk(snapshot, followlinks=False):
        if any((Path(directory) / name).is_symlink() for name in folders + files):
            raise ValueError('snapshot symlink requires explicit provenance review')
        for name in files:
            file = Path(directory) / name
            info = file.stat()
            rows.append((str(file.relative_to(snapshot)), info.st_size, info.st_mtime_ns))
            if len(rows) > 100000:
                raise ValueError('snapshot manifest count bound exceeded')
    rows.sort()
    if not rows:
        raise ValueError('empty snapshot')
    snapshot_data = json.dumps(rows, separators=(',', ':')).encode()
    (root / 'snapshot-files.json').write_bytes(snapshot_data)
    metadata = {'status': 'PREPARED_NOT_MEASURED', 'phase': 'READY', 'source_map': identity['map'],
                'expb_base': EXPB_SHA, 'client_image': IMAGE, 'snapshot_source': str(snapshot),
                'snapshot_metadata_sha256': hashlib.sha256(snapshot_data).hexdigest(),
                'snapshot_metadata_only': True, 'snapshot_files': len(rows),
                'outputs': str(outputs), 'sample_directory': str(sample_directory),
                'collectors': FLAGS, 'timing_is_profiled_diagnostic_not_performance': True}
    (root / 'preparation.json').write_text(json.dumps(metadata, indent=2) + '\n', encoding='utf-8')
    return config
