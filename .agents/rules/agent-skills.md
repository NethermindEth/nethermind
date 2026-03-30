# Skills

Canonical skill definitions live in `.agents/skills/`. Tool-specific directories (`.claude/skills/`, `.cursor/skills/`) contain **symlinks** to the canonical files — never independent copies.

## Rules

- **Single source of truth**: Always create and edit skills in `.agents/skills/<name>/SKILL.md`. Never place standalone skill files directly in `.claude/skills/` or `.cursor/skills/`.
- **Symlink per skill, not the directory**: Symlink individual skill subdirectories, not the entire `skills/` folder — this avoids overriding tool-specific skills that other contributors may have.
- **Relative paths**: Symlinks must use the relative path `../../.agents/skills/<name>` (relative to `.claude/skills/` or `.cursor/skills/`).
- **Preserve on copy**: When copying `.agents/`, `.claude/`, or `.cursor/` to another directory, use `cp -a` to preserve symlinks.

## Adding a new skill

```bash
# From repo root
mkdir -p .agents/skills/<name>
# ... add SKILL.md there
ln -s ../../.agents/skills/<name> .claude/skills/<name>
ln -s ../../.agents/skills/<name> .cursor/skills/<name>
```
