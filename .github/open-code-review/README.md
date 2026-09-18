AI Code Review automatically reviews non-draft PRs when opened, reopened, marked
ready for review, or updated with new commits. It publishes a summary and inline
findings using the model configured in Actions secrets. Each run reviews the full PR diff; a newer run
cancels an older run for the same PR. Reviews are advisory and do not change the
Claude review gate.

Automatic reviews start after this workflow is merged and its required model,
API base URL, and key settings are configured.
Existing open PRs are reviewed on their next matching event; merging this workflow
does not start a batch review of existing PRs. Manual dispatch remains available.
Dependabot-triggered runs are skipped because GitHub
[restricts their credentials](https://docs.github.com/en/code-security/reference/supply-chain-security/dependabot-on-actions).
Maintainers can still dispatch reviews for those PRs.

Configure it under **Settings → Secrets and variables → Actions** in the repository,
or use organization settings made available to this repository. These settings are
read for each run, so changing the model, API base URL, or key needs no code change or PR.

| Setting | Type | Default / purpose |
| --- | --- | --- |
| `OCR_MODEL` | Secret | Required. Exact model alias accepted by the gateway; no code default. |
| `OCR_API_BASE_URL` | Secret | Required. HTTPS API base URL, including any path prefix such as `/v1`; no code default. |
| `OCR_LITELLM_API_KEY` | Secret | API key used when no alternative secret name is configured. |
| `OCR_API_KEY_SECRET` | Variable | Optional name of another Actions secret containing the key. Defaults to `OCR_LITELLM_API_KEY`. |
| `OCR_EXTRA_BODY` | Secret | Optional JSON object of model-specific request options. Defaults to `{}`. |
| `OCR_AUTO_REVIEW` | Variable | Automatic reviews are enabled unless this is `false`. Manual runs remain available. |
| `OCR_AUTO_TOKEN_BUDGET` | Variable | Soft token budget for automatic runs: `500000`, `1000000`, or `2000000` (default). |

Create a dedicated LiteLLM virtual key allowing the selected model, with a spending
limit and rate limits appropriate for the pilot. Store its value only in the chosen
Actions secret. Rotate it by updating that secret; `OCR_API_KEY_SECRET` holds the
secret's name, never the key itself. The runner must reach the configured endpoint,
which must support the OpenAI-compatible chat completions API with tool calls.

Enter the model, API base URL, and key at the CLI prompts:

```sh
gh secret set OCR_MODEL --repo NethermindEth/nethermind
gh secret set OCR_API_BASE_URL --repo NethermindEth/nethermind
gh secret set OCR_LITELLM_API_KEY --repo NethermindEth/nethermind
```

Missing or blank `OCR_MODEL` or `OCR_API_BASE_URL` values fail configuration before
any model request. The checked-in `config.json` contains only shared review
settings. Runtime configuration gets its model and endpoint exclusively from the
Actions secrets. Move any existing `OCR_MODEL`, `OCR_API_BASE_URL`, and
`OCR_EXTRA_BODY` variables to secrets with the same names before using this workflow.
Secrets also mask these settings in Actions logs; artifact contents are not masked.

Set `OCR_EXTRA_BODY` only when the selected model needs additional request options.
When changing models, update or clear this secret to match the new model's API.
Unset, empty, or `{}` values send no additional options. The options cannot override
`model`, `messages`, or `tools`.

Once the workflow exists on the default branch, dispatch it from a trusted ref:

```sh
gh workflow run open-code-review.yml --repo NethermindEth/nethermind \
  --ref master -f pr_number=12345 -f publish=false
```

Manual dispatch produces artifacts and a workflow summary by default. Set
`publish=true` to publish findings to the PR; automatic runs always publish.
GitHub requires the workflow to exist on the default
branch before dispatching it; a new workflow cannot be exercised in Actions
solely by pushing this feature branch.

Automatic runs use GitHub's
[`pull_request_target` event](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#pull_request_target)
so the workflow and its checkout come from trusted repository code.
The workflow captures the PR's base and head commits, fetches their git objects,
and reviews the full merge-base-to-head range. It keeps the trusted workflow
checkout on disk, including all scripts and review instructions. This also works
for fork PRs: no PR code, build, install script, or PR-supplied workflow executes
with the LiteLLM key. Fork PRs can therefore trigger automatic reviews and consume
the dedicated key's budget; enforce spending and rate limits in LiteLLM.

Configuration uses OCR's native LiteLLM provider with the selected model and endpoint.
The pinned OCR tool loop preserves reasoning across tool calls and uses provider
default tool selection. The initial limits are two concurrent review tasks,
low OCR effort (one review pass), a 64,000-token prompt ceiling per group, and a 2,000,000-token
aggregate budget. OCR's aggregate budget is soft: active requests and final
submission rounds can exceed it. Enforce the spending ceiling in LiteLLM.
Set `OCR_AUTO_TOKEN_BUDGET` to lower the limit for automatic reviews. The dispatch
form selects the budget for manual runs.
Local trials with medium effort on PRs #13478 and #13535 exhausted 500,000 tokens;
the latter also exhausted 2,000,000. The pilot uses a single pass with instructions
to limit context reads to concrete hypotheses about changed behavior. Cached input
counts toward the limit. Treat budget-exhausted runs as incomplete when comparing
review quality.
The single-pass trial on PR #13535 completed both files in 2m56s with 1,010,251
total tokens (925,440 cached input tokens) and no tool errors. This checks the
integration; it is not a review-quality benchmark or a billed-cost measurement.
The review process has an 18-minute limit; the job has a 25-minute limit.

`scripts/ci/open-code-review.cjs` generates rules from the trusted
`.agents/rules/` files on each run, so the ArrayPool.Shared exception and other
project guidance stay current. C# tests and MSBuild/solution files are included.
OCR still excludes binary, deleted, secret, unsupported, and oversized files;
the public summary discloses exclusions. Vendored test suites under
`src/tests` and `src/bench_precompiles` are excluded.

Each run captures its effective configuration on the runner and validates against that model.
Before posting, the wrapper checks the reviewed commits, model, selected-file
coverage, token-budget status, and current PR commits. Incomplete reviews publish
an explicit incomplete summary; their findings are not published. Stale runs
publish nothing; automatic runs also suppress publication if the PR has returned
to draft. Completed reviews use OCR's upstream publisher for one updated
summary and deduplicated inline findings. Low-severity and style/documentation
findings go in the summary with an informational routing notice. Actual inline
posting failures retain a warning. The bot does not approve PRs or resolve discussions.
Keep `Advisory AI review` out of required branch-protection checks.

Artifacts expire after 14 days and contain only the public `summary.md`: reviewed
commit, coverage, exclusions, finding count, token usage, elapsed time, and run link.
Runtime configuration, the model identifier, raw OCR output, selection preview,
and stderr are not uploaded. Configuration and raw evidence exist only on the
ephemeral runner; debug a failed review locally when those details are needed.
OCR does not expose LiteLLM's billed cost, so use the dedicated key's spend logs
for cost per run. Compare roughly 20 PRs across networking, EVM/state, tests, and
CI: record accepted findings, false positives, missed defects, cost, and latency.

OCR v1.12.5 is pinned by binary SHA-256. The publisher comes from the same release
commit `189be5b024d3309dd10fdc8cd8ee31b2530c210b` and has its own SHA-256.
When upgrading, update both pins and run the offline suite plus an actual OCR
review through LiteLLM. Changes to output schemas must preserve the coverage
checks.

For local configuration generation, export `OCR_MODEL` and `OCR_API_BASE_URL` first;
also export `OCR_EXTRA_BODY` if the model needs it.

```sh
node --test scripts/ci/test-open-code-review.cjs
node scripts/ci/open-code-review.cjs prepare . /tmp/nethermind-review-rules
```
