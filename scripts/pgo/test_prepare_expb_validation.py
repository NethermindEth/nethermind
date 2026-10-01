# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
import hashlib
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from prepare_expb_validation import prepare


class ExPBValidationPreparationTests(unittest.TestCase):
    def test_patch_is_isolated_and_replaces_shared_cache_hardlinks(self):
        source = ("const discardResponses = (__ENV.EXPB_DISCARD_RESPONSES || '1') === '1';\n"
                  "    check(r, { 'status_200': (x) => x.status === 200 }, tags);\n" * 2).encode()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            cache = root / "cache"; cache.write_bytes(source)
            env = root / "env"; env.mkdir()
            template = env / "template"; os.link(cache, template)
            with patch("prepare_expb_validation.TEMPLATE_SHA256", hashlib.sha256(source).hexdigest()):
                prepare(template, env, root / "manifest.json")
            text = template.read_text()
            self.assertIn("verifyEngineResponse(r, JSON.parse(pair.rawPayload))", text)
            self.assertIn("verifyEngineResponse(r, JSON.parse(pair.rawFcu))", text)
            self.assertEqual(text.count("EXPB_ENGINE_RESULT"), 2)
            self.assertEqual(cache.read_bytes(), source)
            self.assertEqual(json.loads((root / "manifest.json").read_text())["patched_sha256"],
                             hashlib.sha256(template.read_bytes()).hexdigest())
            with self.assertRaisesRegex(ValueError, "isolated environment"):
                prepare(cache, env, root / "bad.json")
            with self.assertRaisesRegex(ValueError, "pinned source"):
                prepare(template, env, root / "bad.json")
