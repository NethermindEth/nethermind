---
name: format-fix
description: Run dotnet format and cspell on modified .cs files, auto-fix formatting, correct typos in source, and add unknown domain words to cspell.json. Use when asked to "format", "fix formatting", "check spelling", "run format-fix", or "/format-fix".
allowed-tools:
  [
    Bash(git diff*),
    Bash(git status*),
    Bash(echo*),
    Bash(bash .claude/hooks/format-check.sh*),
    Bash(bash scripts/cspell.sh*),
    Read,
    Edit,
    Glob,
  ]
---

# format-fix skill

Runs the same checks as `.github/workflows/code-formatting.yml` locally by invoking `.claude/hooks/format-check.sh` — the single source of truth for both dotnet format and cspell. Resolves every issue automatically.

---

## Phase 1 — Discover files

Run both commands in parallel:

```bash
git diff --name-only HEAD
git status --short
```

Collect every `.cs` file that is modified, staged, or untracked (excluding `bin/`, `obj/`, `artifacts/`).
If no `.cs` files are found, report "Nothing to check." and stop.

---

## Phase 2 — Run the hook on each file

The hook `.claude/hooks/format-check.sh` orchestrates both checks (whitespace formatting and spell-check). Call it once per file, piping the file path as JSON on stdin — exactly as Claude Code's PostToolUse event does:

```bash
echo '{"tool_input":{"file_path":"<absolute-path-to-file>"}}' | bash .claude/hooks/format-check.sh
```

Run all files sequentially (the hook invokes dotnet, which cannot run in parallel safely).
Capture stdout/stderr for each invocation.

---

## Phase 3 — Parse cspell output

From the captured output, collect every line matching:
```
<file>:<line>:<col> - Unknown word (<word>) [fix: (<suggestion>)]
```

Group issues by file. If there are none across all files, report "All checks passed." and stop.

---

## Phase 4 — Resolve each cspell issue

Process issues one file at a time. For each flagged word apply **exactly one** of the two actions below:

### Action A — Fix the typo (cspell provided a suggestion)

Condition: the output line contains `fix: (<suggestion>)`.

1. Read the file at the reported line.
2. Verify the word in context — confirm it really is a misspelling and the suggestion fits.
3. Use `Edit` to replace the misspelled word with the suggestion **in that line only** (do not do a global replace — the same string may be correct elsewhere, e.g. in a hex literal or test name).
4. If the suggestion does not fit the context (e.g. the word is intentional jargon), fall through to Action B instead.

### Action B — Add to cspell.json allowlist (no suggestion, or suggestion doesn't fit)

Condition: no `fix:` suggestion, or the suggestion was rejected in Action A.

1. Read `cspell.json`.
2. Confirm the word is a genuine domain term (Ethereum/blockchain jargon, a library name, an acronym, an author name, etc.) and not simply a misspelling. If genuinely ambiguous, prefer Action A and ask the user.
3. Insert the word (lowercase) into the `"words"` array in `cspell.json`, maintaining alphabetical order.
4. Use `Edit` — a single edit per new word, not one bulk replacement.

---

## Phase 5 — Verify

Re-run the hook on every file that had issues:

```bash
echo '{"tool_input":{"file_path":"<absolute-path>"}}' | bash .claude/hooks/format-check.sh
```

Every previously flagged file must now produce no cspell errors. If any remain, repeat Phase 4.

---

## Output format

```
## Format fix

### Whitespace
- X file(s) reformatted: <list>  (or "No changes needed")

### Spelling — fixed typos
- `<file>:<line>` — replaced `<wrong>` → `<correct>`

### Spelling — added to cspell.json
- `<word>` (domain term in <file>)

### Spelling — no issues  (when clean)
```
