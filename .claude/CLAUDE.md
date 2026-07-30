@../AGENTS.md
@../.agents/rules/coding-style.md
@../.agents/rules/robustness.md

The two rule files above (coding-style, robustness) are already loaded - do not Read them again. Load the other `.agents/rules/` files on demand per the index in AGENTS.md.

Skills: the entries under `.claude/skills/` are git symlinks into `.agents/skills/`. If those skills (fix-nethtest, gas-benchmark, resource-leak-audit, review) are missing from your available-skills list on Windows, the checkout has `core.symlinks=false` and the entries are text stubs (`Get-Item` shows an empty LinkType). Fix: `git config core.symlinks true`, delete the stub files, then `git checkout -- .claude/skills` (requires Windows Developer Mode or an elevated shell).
