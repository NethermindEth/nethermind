# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Enable strict Engine API response evidence in an isolated collection-only EXPB install."""
import argparse
import hashlib
import json
from pathlib import Path


TEMPLATE_SHA256 = "27236a4a30279d131927d60f9c0bcd45d948ba9ebfc0943e05a8311086b7982e"


def prepare(template, environment, manifest):
    template = Path(template).resolve(strict=True)
    environment = Path(environment).resolve(strict=True)
    if not template.is_relative_to(environment):
        raise ValueError("EXPB template must belong to this run's isolated environment")
    original = template.read_bytes()
    if hashlib.sha256(original).hexdigest() != TEMPLATE_SHA256:
        raise ValueError("EXPB template differs from the pinned source")
    helper = Path(__file__).with_name("engine_response.mjs").read_text(encoding="utf-8")
    text = original.decode("utf-8")
    text = text.replace("const discardResponses = (__ENV.EXPB_DISCARD_RESPONSES || '1') === '1';",
                        "const discardResponses = false;")
    needle = "    check(r, { 'status_200': (x) => x.status === 200 }, tags);"
    if text.count(needle) != 2:
        raise ValueError("expected the pinned payload and forkchoice response sites")
    offset = 0
    for kind, raw in (("newPayload", "rawPayload"), ("forkchoiceUpdated", "rawFcu")):
        replacement = needle + "\n" + f"""    try {{
      const hash = verifyEngineResponse(r, JSON.parse(pair.{raw}));
      console.log(`EXPB_ENGINE_RESULT idx=${{idx}} warmup=${{warmup ? 1 : 0}} kind={kind} status=VALID latest_valid_hash=${{hash}}`);
    }} catch (_) {{
      exec.test.abort('Engine API validation failed: {kind}');
    }}"""
        start = text.index(needle, offset)
        text = text[:start] + text[start:].replace(needle, replacement, 1)
        offset = start + len(replacement)
    text += "\n" + helper.replace("export function", "function")
    staged = template.with_name(template.name + ".pgo-staged")
    staged.write_text(text, encoding="utf-8", newline="\n")
    staged.chmod(template.stat().st_mode)
    staged.replace(template)
    Path(manifest).write_text(json.dumps({"original_sha256": TEMPLATE_SHA256,
        "patched_sha256": hashlib.sha256(template.read_bytes()).hexdigest(),
        "helper_sha256": hashlib.sha256(helper.encode()).hexdigest(),
        "scope": "isolated collection only; VALID/hash checks for warmup and measured requests"}, indent=2) + "\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--template", type=Path, required=True)
    parser.add_argument("--environment", type=Path, required=True)
    parser.add_argument("--manifest", type=Path, required=True)
    args = parser.parse_args()
    prepare(args.template, args.environment, args.manifest)
