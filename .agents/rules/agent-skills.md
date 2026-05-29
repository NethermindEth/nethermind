# Skills

Canonical skill definitions live in `.agents/skills/`. Tool-specific
directories contain **real copies** generated from the canonical files —
never independent edits, and never symlinks.

## Why copies, not symlinks

Git symlinks are not reliably checked out on Windows: without
`core.symlinks=true` *and* OS-level symlink privilege (Developer Mode or
admin), Git writes the link as a plain text stub containing the target path.
The tool then sees a ~30-byte text file instead of a skill and silently
ignores it. To keep skills working on every OS with zero Git configuration,
the tool directories hold real copies kept in sync by a script and enforced
by CI.

## Where each tool looks

| Tool | Skill directory | How it's populated |
|------|-----------------|--------------------|
| Claude Code | `.claude/skills/` | real copy (synced) |
| Cursor | `.cursor/skills/` | real copy (synced) |
| GitHub Copilot | `.agents/skills/` | read natively — no copy |
| Other AGENTS.md tools | `.agents/skills/` | read natively — no copy |

GitHub Copilot's coding agent and VS Code read `.agents/skills/` directly,
so the canonical directory doubles as their source. Claude Code and Cursor
read only their own directories, which is why they get copies.

## Rules

- **Single source of truth**: Always create and edit skills in
  `.agents/skills/<name>/SKILL.md` (plus any referenced resources). Never edit
  files under `.claude/skills/` or `.cursor/skills/` by hand — those are
  generated and will be overwritten.
- **Regenerate after editing**: Run the sync script (below) and commit the
  result. CI fails if the copies drift from `.agents/skills/`.

## Adding or editing a skill

```bash
# From repo root: create/edit the canonical skill
mkdir -p .agents/skills/<name>
# ... add SKILL.md there

# Regenerate the tool copies, then commit everything together
scripts/sync-skills.sh            # bash / macOS / Linux / Git Bash
# or on Windows PowerShell:
pwsh scripts/sync-skills.ps1
```

The CI workflow `.github/workflows/sync-skills.yml` runs
`scripts/sync-skills.sh --check` on every PR touching a skill directory and
fails if `.claude/skills/` or `.cursor/skills/` differ from the canonical
source.
