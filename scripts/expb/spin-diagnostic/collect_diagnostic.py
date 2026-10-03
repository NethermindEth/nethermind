"""Preserve only this dispatch's raw observations encrypted; publish numeric projections."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

from export_requests import project
from export_target_numeric import export as export_scheduler
from scheduler_collector import decode_capture


def main():
    bundle = Path(__file__).resolve().parent
    temporary = Path(os.environ['RUNNER_TEMP']).resolve(strict=True)
    suffix = os.environ['GITHUB_RUN_ID'] + '-' + os.environ['GITHUB_RUN_ATTEMPT']
    if not all(part.isdecimal() for part in suffix.split('-')):
        raise ValueError('invalid run identity')
    root = temporary / ('spin-diagnostic-' + suffix)
    public = temporary / ('spin-public-' + suffix)
    if root.is_symlink() or not root.is_dir():
        raise ValueError('owned diagnostic directory missing')
    public.mkdir(mode=0o700, exist_ok=False)
    steps = {}
    try:
        children = list((root / 'outputs').iterdir())
        if len(children) != 1 or not children[0].is_dir() or children[0].is_symlink():
            raise ValueError('expected exactly one diagnostic output directory')
        output = children[0]
        for label, operation in (
            ('scheduler_decode', lambda: decode_capture(output / 'scheduler-private')),
            ('scheduler_projection', lambda: export_scheduler(output / 'scheduler-private', public / 'scheduler')),
        ):
            try:
                result = operation()
                expected = ('REQUIRES_CAPABILITY_AND_WINDOW_REVIEW' if label == 'scheduler_decode'
                            else 'PROJECTION_COMPLETE_REQUIRES_CAPABILITY_REVIEW')
                steps[label] = {'completed': result.get('status') == expected,
                                'status': result.get('status', 'unknown')}
            except Exception as error:
                steps[label] = {'completed': False, 'error_type': type(error).__name__}
        try:
            rows, summary = project(output / 'diagnostic-requests.jsonl', 3, 1000)
            request_dir = public / 'requests'
            request_dir.mkdir(mode=0o700)
            with (request_dir / 'requests.jsonl').open('x', encoding='utf-8') as stream:
                for row in rows:
                    stream.write(json.dumps(row, separators=(',', ':')) + '\n')
            (request_dir / 'summary.json').write_text(json.dumps(summary, indent=2) + '\n', encoding='utf-8')
            steps['requests'] = {'completed': True, 'status': summary['status']}
        except Exception as error:
            steps['requests'] = {'completed': False, 'error_type': type(error).__name__}
        for name in ('identity-map.jsonl', 'corpus-identity.json'):
            source = root / 'source-map' / name
            if source.is_symlink() or source.stat().st_size > 1024 * 1024:
                raise ValueError('source map bounds violated')
            shutil.copyfile(source, public / name)
        steps['source_map'] = {'completed': True}
    except Exception as error:
        steps['collection'] = {'completed': False, 'error_type': type(error).__name__}
    # Encryption is attempted even when collection/decoding failed, after workload cleanup.
    (root / 'collection-status.json').write_text(json.dumps(steps, indent=2) + '\n', encoding='utf-8')
    command = [sys.executable, str(bundle / 'archive_private.py'),
               '--input-dir', str(root), '--output-dir', str(public / 'encrypted'),
               '--certificate', str(bundle / 'recipient.crt')]
    try:
        code = subprocess.run(command, check=False, timeout=600).returncode
        steps['encrypted_archive'] = {'completed': code == 0, 'returncode': code}
    except Exception as error:
        steps['encrypted_archive'] = {'completed': False, 'error_type': type(error).__name__}
    status = {'run_id': os.environ['GITHUB_RUN_ID'], 'run_attempt': os.environ['GITHUB_RUN_ATTEMPT'],
              'steps': steps, 'inference': 'Capability and full clock/window validation still required.',
              'raw_retained_privately': True}
    (public / 'collection-status.json').write_text(json.dumps(status, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(status))
    return 0 if all(value['completed'] for value in steps.values()) else 1


if __name__ == '__main__':
    raise SystemExit(main())
