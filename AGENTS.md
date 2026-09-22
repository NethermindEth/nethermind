# AGENTS instructions

This guide helps to get started with the Nethermind Ethereum execution client repository. It covers the repo layout, coding guidelines, testing, and the PR workflow.

## Repo structure

- [src/Nethermind](./src/Nethermind/): The Nethermind codebase
- [tools](./tools/): Various servicing tools for testing, monitoring, etc.
- [scripts](./scripts/): The build scripts and stuff used by GitHub Actions workflows
- See [README.md](./README.md) for more info

## Coding guidelines and style

- Follow [CONTRIBUTING.md](./CONTRIBUTING.md) and [.editorconfig](./.editorconfig)
- An agent's primary concern is correctness. Next after that is reviewer fatigue.
- Keep changes minimal and focused — don't touch unrelated code. Try to minimise the diff from the base branch, for example, not reordering code or making stylistic changes unless they improve code clarity.
- On unrelated code, be even more conservative: do not rephrase comments, and do not even fix typos. That is the responsibility of a linter. Keep the code unchanged verbatim.
- When designing a solution, try to design as a plugin, altering behavior through module registration without modifying existing code (see [di-patterns.md](./.agents/rules/di-patterns.md)). Even if not a plugin, it's generally a good idea to alter behavior without changing current code:
  - Where possible, do not add additional interfaces or public methods — this tends to break plugins, cause unnecessarily tight coupling, and make implications harder to reason about.
  - Prefer composition over inheritance — inheritance has caused many extensibility issues in this code base.
- When multiple solutions are viable, prefer them in this order: one that removes code, then one that adds code without adding surface area (new interfaces or public methods) or touching existing code, and last, one that modifies existing code. Removing code removes failure points; additive changes generally don't regress existing behavior and are the easiest to review. This ranks viable designs — a bug in existing code should still be fixed in place, not wrapped. If a change makes existing code unused, remove it.
- When fixing a bug, always add a regression test
- Do not alter [src/bench_precompiles](./src/bench_precompiles/) or [src/tests](./src/tests/)
- Prefer self-documenting code — clear names and structure should remove the need for most comments. Emit a comment only when it captures context that is not obvious from the code itself: the _why_ behind a non-obvious choice, an invariant, a workaround, an EIP/Yellow-Paper reference, a subtle edge case, etc. Comments that merely restate the code are noise — don't add them, and remove them when you encounter them. Keep comments concise and ensure that they make sense in the context of the master branch, not referencing the specifics of the current session.
- When in doubt, do not add a comment. An unnecessary comment contributes to reviewer fatigue.
- For member-level documentation (methods, constructors, properties, types), prefer XML doc comments over in-line comments whenever the explanation applies to the member as a whole:
  - `<summary>` — one or two sentences describing _what_ the member does from the caller's perspective: its contract, purpose, and what it returns/represents. Keep it short enough to be useful in IntelliSense; do not describe implementation details or rationale here.
  - `<remarks>` — the longer-form explanation that does not belong in the summary. Use it for any of: algorithmic approach, design rationale, pre/postconditions and invariants, thread-safety guarantees, performance characteristics, side effects, edge cases, EIP / Yellow-Paper / spec references, and notable caveats for callers.
  - Use `<param>`, `<returns>`, `<exception>`, and `<typeparam>` for parameter/return/exception/type-parameter specifics rather than stuffing them into `<summary>` or `<remarks>`.
  - For interface implementations and overrides, prefer `<inheritdoc/>` (optionally with `cref=`) to propagate the contract from the base/interface instead of duplicating it. Add `<remarks>` only when the implementation introduces caller-visible behavior beyond the inherited contract.
  - Reserve in-line comments for implementation-specific details that cannot reasonably live on the member header — e.g. why a particular branch is taken, why a value is computed this way at this exact spot, or a local workaround for a bug elsewhere.
- Avoid code duplication, especially in tests:
  - When tests differ only by inputs and expected outputs, parameterize a single test rather than copy-pasting the body. Before adding a new test, check whether an existing one can be extended with another value or case.
  - Prefer the parameter attributes for a run of positional-only cases: `[Values(...)]` (plus the `[Test]` that NUnit then needs for discovery), a bare `[Values]` on a `bool`, `[Range(from, to)]` for a contiguous integer run, or `[ValueSource(...)]` when the values are not attribute constants or are shared by several methods. `[Values]` on more than one parameter generates the cartesian product, so use it only when every combination is worth running.
  - Keep `[TestCase(...)]` / `[TestCaseSource(...)]` where the parameter attributes cannot express the cases: sets that are not a product of independent values, array-valued arguments, per-case `ExpectedResult`/`TestName`/`Ignore`/`Explicit`, or a case that needs a comment of its own.
  - When only _parts_ of tests are similar (shared setup, common assertions, recurring scenarios), factor those parts into helper methods or helper types (e.g. a builder, a shared static helper, a test fixture base). Keep each test body focused on what makes the case unique.
  - See [`.agents/rules/test-infrastructure.md`](./.agents/rules/test-infrastructure.md) "Test guidelines" for details.

---

## Codebase Rules

Detailed rules live in [`.agents/rules/`](./.agents/rules/). **You MUST read the relevant files before answering any query, reasoning, writing, reviewing, planning, or debugging any code read load additional files as soon as the task touches their domain. Do NOT skip loading a file because you think you already know the rules — always read from disk.**

- [coding-style.md](./.agents/rules/coding-style.md) — Almost always. Load for any task requiring C#-specific reasoning. Covers syntax, coding patterns, documentation, and code quality.
- [di-patterns.md](./.agents/rules/di-patterns.md) — Core dependency injection patterns. Load when working with DI registration, service wiring, or component architecture. Covers Autofac modules, WorldState architecture, lifetimes, and the custom DSL.
- [test-infrastructure.md](./.agents/rules/test-infrastructure.md) — Load when working with tests, benchmarks, or designing components that need to be testable. Covers TestBlockchain, benchmark setup, DI anti-patterns, and test guidelines.
- [robustness.md](./.agents/rules/robustness.md) — Almost always. Load for any task requiring C#-specific reasoning. Covers async pitfalls, resource management, thread safety, input validation, and unsafe blocks.
- [performance.md](./.agents/rules/performance.md) — Load when working on hot paths in the codebase. Covers ref structs, Span, SIMD, function pointers, and zero-allocation patterns.
- [package-management.md](./.agents/rules/package-management.md) — Load when working with NuGet dependencies. Covers Central Package Management (CPM) rules.
- [github-workflows.md](./.agents/rules/github-workflows.md) — Load when working with GitHub Actions, CODEOWNERS, or PR templates. Covers workflow conventions and automation patterns.
- [git.md](./.agents/rules/git.md) — Load when interacting with git version control. Covers merging, rebasing, pushing, and more.
- [agent-skills.md](./.agents/rules/agent-skills.md) — Load when working with agentic skills. Covers the symlink convention.

## Pull request guidelines

Before creating a pull request:

- Ensure the code compiles
- Add tests covering your changes and ensure they pass:
  ```bash
  dotnet test --project path/to/.csproj -c release -- --filter FullyQualifiedName~TestName
  ```
- Ensure the code is well-formatted:
  ```bash
  dotnet format whitespace src/Nethermind/ --folder
  ```
- Follow the [pull_request_template.md](.github/pull_request_template.md) format: fill in the changes section, tick the appropriate type-of-change checkboxes, and complete the testing/documentation sections. The checkboxes drive automatic PR labeling.

## Agent declaration

- When creating a PR or commenting on GitHub under a human account, state that you are an AI agent acting on behalf of the user and name the harness and model, e.g. `🤖 AI agent (Claude Code / Opus 5) on behalf of @user` — this makes it easy to trace which configuration produced which behavior. Put it in the `Remarks` section of the PR body, or at the end of a comment. This does not apply to commit messages, nor to comments posted under a bot account, whose identity already discloses the agent.

## Benchmark workflows

- [expb-benchmark](./.agents/skills/expb-benchmark/SKILL.md) — reproducible payload benchmarks (`run-expb-reproducible-benchmarks.yml`), profiling, and run-log checks.
- [rpc-benchmark](./.agents/skills/rpc-benchmark/SKILL.md) — JSON-RPC benchmarks (`run-rpc-benchmarks.yml`).
