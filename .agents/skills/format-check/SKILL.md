---
name: format-check
description: Run dotnet format and cspell on modified .cs files, auto-fix formatting, correct typos in source, and add unknown domain words to cspell.json. Use when asked to "format", "fix formatting", "check spelling", "run format-check", or "/format-check".
allowed-tools:
  [
    Bash(git diff*),
    Bash(git status*),
    Bash(bash scripts/dotnet-format.sh*),
    Bash(bash scripts/cspell.sh*),
    Read,
    Edit,
    Glob,
  ]
---

# format-check skill

Runs the same checks as `.github/workflows/code-formatting.yml` locally, then resolves every issue automatically.

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

## Phase 2 — Auto-fix whitespace formatting

Run:

```bash
bash scripts/dotnet-format.sh
```

This rewrites files in place. Report how many files were changed (compare `git diff --name-only` before and after).

---

## Phase 3 — Spell-check

Run cspell across all `.cs` files found in Phase 1:

```bash
bash scripts/cspell.sh <file1> <file2> ...
```

Capture every output line matching:
```
<file>:<line>:<col> - Unknown word (<word>) [fix: (<suggestion>)]
```

Group issues by file. If there are no issues, report "Spell check passed." and stop.

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

After all edits:

```bash
bash scripts/cspell.sh <previously-failing-files>
```

Every previously flagged file must now report 0 issues. If any issues remain, repeat Phase 4 for the residual words.

---

## Output format

```
## Format check

### Whitespace
- X file(s) reformatted: <list>  (or "No changes needed")

### Spelling — fixed typos
- `<file>:<line>` — replaced `<wrong>` → `<correct>`

### Spelling — added to cspell.json
- `<word>` (domain term in <file>)

### Spelling — no issues  (when clean)
```
