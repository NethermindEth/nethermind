import os
from pathlib import Path
import unittest
from unittest.mock import patch
import launch


class RunnerPinTests(unittest.TestCase):
    def test_other_or_missing_runner_refused_before_identity_or_paths(self):
        for value in (None, '', 'different-runner'):
            with self.subTest(value=value), patch.object(launch.sys, 'platform', 'linux'), patch.dict(os.environ, {}, clear=True), patch.object(launch.guard, 'run_identity') as identity:
                if value is not None:
                    os.environ['RUNNER_NAME'] = value
                with self.assertRaisesRegex(ValueError, 'EXACT_RUNNER_REQUIRED'):
                    launch.launch(Path('/unaccessed-operator'), Path('/unaccessed-harness'))
                identity.assert_not_called()

    def test_exact_runner_reaches_existing_identity_guard(self):
        with patch.object(launch.sys, 'platform', 'linux'), patch.dict(os.environ, {'RUNNER_NAME': 'reproducible-benchmarks'}, clear=True), patch.object(launch.guard, 'run_identity', side_effect=RuntimeError('IDENTITY_SENTINEL')) as identity:
            with self.assertRaisesRegex(RuntimeError, 'IDENTITY_SENTINEL'):
                launch.launch(Path('/unaccessed-operator'), Path('/unaccessed-harness'))
            identity.assert_called_once_with()


if __name__ == '__main__':
    unittest.main()
