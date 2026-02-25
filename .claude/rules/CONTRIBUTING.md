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
2. For project-scoped rules, add a YAML frontmatter with `paths:` globs (see `src-nethermind-index.md` for the list of path-scoped rules)
3. Keep rules concise — one bullet per convention, with a brief "why" if non-obvious
4. PR it like code — rules changes should be reviewed by the team

## File structure

```
.claude/rules/
├── common-review-feedback.md  ← always loaded
├── coding-style.md            ← always loaded
├── concurrency.md             ← src/Nethermind/**/*.cs (global — shared state patterns appear everywhere)
├── di-patterns.md             ← src/Nethermind/**/*.cs
├── performance.md             ← src/Nethermind/**/*.cs
├── test-infrastructure.md     ← **/*Test*/**/*.cs, **/*Benchmark*/**/*.cs (single rule for tests)
├── package-management.md      ← **/*.csproj, **/Directory.*.props
├── evm/evm-conventions.md     ← src/Nethermind/Nethermind.Evm*/**/*.cs
├── serialization.md           ← src/Nethermind/Nethermind.Serialization.*/**/*.cs
├── blockchain.md              ← src/Nethermind/Nethermind.Blockchain/**/*.cs
├── state.md                   ← src/Nethermind/Nethermind.State*/**/*.cs
├── txpool.md                  ← src/Nethermind/Nethermind.TxPool/**/*.cs
├── network.md                 ← src/Nethermind/Nethermind.Network*/**/*.cs
├── specs.md                   ← src/Nethermind/Nethermind.Specs/**/*.cs
├── init.md                    ← src/Nethermind/Nethermind.Init/**/*.cs
├── robustness.md              ← always loaded (async, dispose, unsafe, catch patterns)
├── github-workflows.md        ← .github/** (workflows, actions, CODEOWNERS, PR template)
├── src-nethermind-index.md    ← index of folder-scoped rules (not path-loaded, documentation only)
└── CONTRIBUTING.md            ← this file (not auto-loaded)
```

## Skills

Two review skills live in `.claude/skills/`:

- **`/review`** — Deep review for consensus correctness, security, robustness, performance, breaking changes, and observability. Read-only (no shell commands). Lives in `.claude/skills/review/`.
- **`/self-review`** — Pre-PR convention check: format verification, SPDX headers, forbidden directories, and rule-based convention checks against `.claude/rules/`. Lives in `.claude/skills/self-review/`.

## Cross-tool portability

`.claude/rules/` is Claude Code native. For other tools:
- **AGENTS.md**: Remains the tool-agnostic reference; points to `.claude/rules/` for details
- The knowledge is plain markdown — portable to any tool that reads convention files
