---
name: council-review
description: Multi-model PR review "council" — gathers an independent review from Claude and from Codex (OpenAI) on the same diff, then synthesizes one consensus report with agreements, disagreements, and prioritized findings. Use when asked to "council review", "council-review", "multi-model review", "run the council", or "second-model review".
allowed-tools:
  [
    Bash(git diff*),
    Bash(git merge-base*),
    Bash(git log*),
    Bash(git status*),
    Bash(git rev-parse*),
    Bash(git ls-remote*),
    Bash(command -v*),
    Bash(codex*),
    Bash(gh:*),
    Read,
    Grep,
    Glob,
    SlashCommand,
  ]
---

# Multi-model review council

Run the same diff past **two independent reviewers — Claude and Codex (OpenAI)** — then have Claude act as chairman and synthesize a single report. The value is disagreement: findings only one model raises are the ones a single-model review would have missed.

**v1 scope (this file):** two models, run **locally**, CLI-based. Wiring it into the `pull_request` CI workflow is a deliberate follow-up (see "Running in CI" at the bottom) — do not attempt that here.

Only surface findings with **high confidence** (see the `review` skill's threshold). A noisy council is worse than a single good review.

---

## Preflight — is Codex available?

Codex runs through the allowlisted [`openai/codex-plugin-cc`](https://github.com/openai/codex-plugin-cc) plugin, which wraps the local **Codex CLI**. Before doing any work, verify it's installed:

- Run `command -v codex` (and, if the plugin is installed, `/codex:setup` or `/codex:status`).

**If Codex is not available**, stop and tell the user how to set it up, then ask whether to proceed Claude-only:

> Codex CLI not found. For the multi-model council, install it once:
> `npm install -g @openai/codex` then `codex login` (ChatGPT subscription or OpenAI API key).
> I can run a **Claude-only** review now if you'd rather not set that up yet — say the word.

Do **not** fabricate a Codex opinion. If Codex is unavailable and the user opts to continue, produce only Stage 1 and label the report clearly as single-model.

---

## Stage 0 — Establish the diff

Reuse the diff-base logic from the [`review` skill](../review/SKILL.md) verbatim (merge-base against the target branch, untracked-file warning, stale-ref detection). Do not diff against `master` directly.

- Default target: the review the user asked for — a branch/PR against its base, or uncommitted working-tree changes.
- Capture the base SHA and the changed-file list once; both reviewers must see the **same** diff.

---

## Stage 1 — Claude's independent review

Produce Claude's review by **following the [`review` skill](../review/SKILL.md)** on the established diff — same rigor, same Ethereum-correctness / security / performance / breaking-change categories, same confidence bar. Keep the structured findings (category tag, `file:line`, problem, fix); you'll need them for synthesis.

Do this **before** reading Codex's output, so Claude's opinion is genuinely independent (not anchored on Codex's).

---

## Stage 2 — Codex's independent review

**Keep it independent.** Codex reviews the diff **without** seeing Claude's Stage 1 findings — the whole point is two genuinely independent opinions. The reaction and reconciliation happen in Stage 3, not here. (Do not inject Claude's findings into Codex's prompt.)

Ask Codex to review the **same** diff. Preferred path is the plugin's own commands (they handle background execution and result retrieval):

- **`/codex:review`** — standard review of the branch/uncommitted changes.
- **`/codex:adversarial-review`** — steerable review that challenges design decisions; use this when the change is consensus-sensitive or architecturally significant.
- Run it in the background and wait for completion (`--background --wait`), or poll `/codex:status` and fetch with `/codex:result`.

**Fallback (headless / scripted — this is what an agent running the skill uses):** invoke Codex non-interactively via Bash. Write the diff to a temp file and point Codex at it:

```bash
git diff <base> HEAD -- <files> > /tmp/council.diff
codex exec --skip-git-repo-check \
  "First read .agents/skills/review/SKILL.md and apply its review criteria — this is the SAME rubric the first reviewer used, so both models start from identical context. Then STATICALLY review the diff at /tmp/council.diff (Nethermind, C# Ethereum client) — do NOT run builds or tests; the PR's own CI already runs them. Report only high-confidence issues in consensus/EVM correctness, security, performance regressions, and breaking API changes — each as: file:line — problem — fix. If clean, say so." < /dev/null
```

**Give Codex the same context.** Codex is a separate agent — it does not inherit Claude's loaded skills. But it *can read repo files*, so tell it to read `.agents/skills/review/SKILL.md` first (and it already reads `AGENTS.md` by its own convention). That way both models review against the identical Ethereum-specific rubric instead of Codex working from a thinner prompt.

**Model note.** Like the `review` skill, don't hardcode a model — let the environment decide. In **CI with `OPENAI_API_KEY`** the default model works, so **omit `-m`**. The *only* time you need `-m` is running locally on a **ChatGPT-login** Codex account, whose default model is refused (`model not supported when using Codex with a ChatGPT account`) — then pass a model from `~/.codex/models_cache.json` (e.g. `gpt-5.6-terra`).

Do not paste raw secrets or tokens into the prompt. Capture Codex's findings as a structured list mirroring Stage 1 (`file:line`, problem, fix). If Codex errors or times out, note it in the report rather than silently dropping the stage.

---

## Stage 3 — Chairman: react, then synthesize (Claude)

Now — and only now — Claude reads Codex's findings. This is the **single interaction point**: for each Codex finding, **confirm or challenge** it against your own Stage 1 review and the code (state which, with a one-line reason). A finding you now realize you missed → adopt it; one you think is wrong → say why. *Then* merge both reviews into **one** report, cross-referencing by `file:line` + issue:

- **Consensus** — both models flagged it. Highest priority; lead with these.
- **Claude only** / **Codex only** — one model caught it. Keep, but tag which model raised it — single-model findings are exactly what a council exists to surface.
- **Disagreement** — the models contradict each other (one flags, the other explicitly says it's fine, or they propose opposite fixes). Present both positions and give Claude's adjudication with a one-line rationale.

De-duplicate aggressively (same issue, different wording = one entry). Apply the `review` skill's "skip these — CI handles it" list to **both** models' output, and drop anything below the confidence bar.

**Post the report to the PR** as a top-level comment with `gh pr comment` — exactly like the regular `review` skill does — and print it in the run too. No scratch files. (When run locally with no PR target, just print it.)

---

## Report template

```markdown
## Council review — <branch/PR>

**Reviewers:** Claude + Codex (OpenAI) · **Base:** <short-sha> · **Files:** <n>

### ✅ Consensus (both models)
<numbered findings: file:line — problem — fix>

### 🔵 Claude only
<findings, or "none">

### 🟠 Codex only
<findings, or "none">

### ⚖️ Disagreements
<issue — Claude's position vs Codex's position — adjudication + rationale, or "none">

### Recommendation
<one paragraph: merge / merge-with-fixes / needs-work, and the single most important thing to address>

### Summary
- Consensus: N · Claude-only: N · Codex-only: N · Disagreements: N
```

If both models come back clean, say so plainly and state that two independent models found nothing above the confidence bar.

---

## Notes & caveats

- **Cost / latency:** this runs two models over the diff — roughly double a single review. Fine for meaningful PRs; overkill for a one-line change.
- **Independence matters:** always generate Claude's review before reading Codex's, or the "two independent opinions" premise collapses.
- **Running in CI:** wired via [`.github/workflows/claude-council-review.yml`](../../../.github/workflows/claude-council-review.yml) — triggered by an `@claude council-review` comment from a trusted maintainer. It checks out with `fetch-depth: 0`, installs the Codex CLI (`npm i -g @openai/codex`), and runs this skill with `OPENAI_API_KEY` in env (API-key auth → default model works, no `-m` needed) and Codex in **static-only** mode (the PR's own CI runs the tests). Requires the `OPENAI_API_KEY` repo secret.
