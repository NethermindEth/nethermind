---
name: review-pr
description: >
  Reviews a Nethermind GitHub pull request. Use when the user runs /review-pr with a PR
  number, or asks to "review PR #NNN" or "review this PR". Fetches the PR diff and
  metadata via the gh CLI, then produces a structured Nethermind-specific code review
  covering: summary, issues/nitpicks (C# style, architecture, test coverage), and a PR
  template checklist. This skill is specific to the Nethermind Ethereum client codebase.
---

# PR Review -- Nethermind

## Workflow

### 1. Fetch PR data

Run both commands in parallel:

```bash
gh pr view <number> --json number,title,body,author,baseRefName,headRefName,additions,deletions,changedFiles,state
gh pr diff <number>
```

If the diff is very large (>1000 lines), also fetch the file list to prioritize:

```bash
gh pr view <number> --json files
```

### 2. Load conventions reference

Read `references/nethermind-conventions.md` before writing the review -- it contains the specific rules to check for.

### 3. Deep-dive on suspicious files (optional)

If the diff is ambiguous or a file seems central to the change, read the full file from the repo:

```bash
gh pr checkout <number>   # only if needed for full file context
```

Or read specific files directly if the branch is already checked out.

### 4. Post inline comments on the diff

For every issue found, post it as an **inline comment on the exact line** in the PR using the GitHub API.
Do NOT write a separate summary of issues -- the inline comments ARE the review.

**Tone:**
- Terse and direct. Every sentence carries information.
- Only comment on things that need to change. No praise for correct code.
- "This allocates on every call" not "This may potentially cause allocation issues."

**Getting the head commit SHA** (needed for the API call):

```bash
gh pr view <number> --json headRefOid --jq '.headRefOid'
```

**Posting inline comments** -- batch all comments into a single API call:

```bash
gh api repos/NethermindEth/nethermind/pulls/<number>/reviews \
  --method POST \
  --field commit_id="<sha>" \
  --field event="COMMENT" \
  --field body="<optional top-level summary, 1 sentence max, or empty string>" \
  --field "comments[][path]=src/Nethermind/Some.File.cs" \
  --field "comments[][line]=<line number in the NEW file>" \
  --field "comments[][side]=RIGHT" \
  --field "comments[][body]=<comment text>" \
  --field "comments[][path]=src/Nethermind/Another.File.cs" \
  --field "comments[][line]=<line number>" \
  --field "comments[][side]=RIGHT" \
  --field "comments[][body]=<comment text>" \
  # ... repeat for each issue
```

**Line number rules:**
- Use the line number in the **new (right) side** of the diff, not the diff position.
- Count from the hunk header: `@@ -old,n +new,m @@` -- `new` is the starting line in the new file. Count context lines down to the target line.
- For new files, line numbers start at 1.
- `side` is always `RIGHT` (the new version).

**If a comment fails** (line not in diff, wrong path, etc.), fall back to posting it as a top-level review comment in the `body` field with the file:line reference.

### 5. Post the verdict

After posting inline comments, submit the final verdict as a separate review:

```bash
# If requesting changes:
gh pr review <number> --request-changes --body "<1-2 sentence summary of what must change>"

# If approving:
gh pr review <number> --approve --body ""

# If commenting only:
gh pr review <number> --comment --body "<1-2 sentence summary>"
```

The top-level verdict body should only mention:
- The overall verdict rationale (1 sentence)
- Any issues that could NOT be posted inline (with file:line)
- PR template problems (missing sections in the description)

Skip the verdict body entirely if everything was captured in inline comments.

---

## Tips

- Batch all inline comments into ONE API call -- do not make one call per comment
- Count line numbers carefully from the `@@` hunk header
- Suggest the concrete fix in every comment, not just the problem
- Nethermind disallows LINQ -- common violation to check
- Check test coverage for bug fixes
- If the diff is clean, post no inline comments and approve
