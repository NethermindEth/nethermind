# Maintaining repository rules

[AI review instructions](../../.github/workflows/ai-review-instructions.yml) consume these files from the immutable PR base commit. The workflow selects DI and test-infrastructure sections by their exact `##` headings. When renaming those headings, update the workflow selectors and their regression checks together.

Prepared instructions have a 16 KiB budget, and each selected source file has a 32 KiB limit. The regression checks also measure ordinary source and source-plus-test prompts. Keep guidance scoped so rule additions preserve review prompt headroom.

After changing consumed rules, run:

```bash
python3 -m unittest scripts/ci/test_ai_review_command.py
```
