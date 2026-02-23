# Contributing to AI Rules

This file documents how to maintain the `.claude/rules/` knowledge base. It is **not** auto-loaded by Claude Code (no frontmatter globs).

## When to add a rule

- Same feedback appeared on 2+ PRs from different reviewers
- A new codebase pattern was established (e.g. new DI module, new test base class)
- A major refactor changed how something should be done

## When to remove a rule

- The pattern it describes no longer applies (API changed, library removed)
- It contradicts a newer rule or codebase convention
- It was speculative and never matched real review feedback

## How to add

1. Identify which rules file the pattern fits (or create a new one if it's a new domain)
2. For project-scoped rules, add a YAML frontmatter with `paths:` globs
3. Keep rules concise — one bullet per convention, with a brief "why" if non-obvious
4. PR it like code — rules changes should be reviewed by the team

## File structure

```
.claude/rules/
├── before-you-code.md         ← always loaded (no paths: frontmatter)
├── common-review-feedback.md  ← always loaded
├── coding-style.md            ← always loaded
├── di-patterns.md             ← src/Nethermind/**/*.cs
├── test-infrastructure.md     ← **/*Test*/**/*.cs, **/*Benchmark*/**/*.cs
├── package-management.md      ← **/*.csproj, **/Directory.*.props
├── performance.md             ← src/Nethermind/**/*.cs
├── evm/evm-conventions.md     ← src/Nethermind/Nethermind.Evm*/**/*.cs
└── CONTRIBUTING.md            ← this file (not auto-loaded)
```

## Seeding from PR reviews

Use the `/learn-from-review <PR_NUMBER>` skill to mine review comments from a merged PR and propose rule additions. Run periodically on recent PRs to keep rules current.

## Cross-tool portability

`.claude/rules/` is Claude Code native. For other tools:
- **Cursor**: Generate `.cursorrules` from rules content
- **AGENTS.md**: Remains the tool-agnostic reference; points to `.claude/rules/` for details
- The knowledge is plain markdown — portable to any tool that reads convention files
