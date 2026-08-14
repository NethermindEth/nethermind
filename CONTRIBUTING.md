# Contributing to Nethermind

Welcome, and thank you for your interest in contributing to Nethermind!

There are several ways to contribute beyond writing code. This guide provides a high-level overview of how you can get involved.

We expect project participants to adhere to our [code of conduct](./CODE_OF_CONDUCT.md). Please read the full text so that you can understand what actions will and will not be tolerated.

## Asking questions and providing feedback

Have a question? Instead of opening an issue, please ask on our [Discord channel](https://discord.gg/GXJFaYk).

Our active community will be keen to assist you. Your well-worded questions will also serve as a resource to others searching for help.

Your comments and feedback are welcome, and the development team is almost always available on [Discord](https://discord.gg/GXJFaYk).

## Reporting issues

Have you identified a reproducible problem in Nethermind? Do you have a feature request? We want to hear about it!

Before filing a new issue, please search our [open issues](https://github.com/NethermindEth/nethermind/issues) to check if it already exists.

If you find an existing issue, please include your feedback in the discussion. Do consider upvoting (👍 reaction) the original post instead of a "+1" comment, as this helps us prioritize popular issues in our backlog.

## Contributing changes

The project maintainers will accept changes that improve the product significantly at their discretion.

### Pull request process

To maintain consistent code quality, pull requests should follow this process:

1. Create a feature branch following the [branch naming](#branch-naming) convention.
2. Push your changes and open the PR. If the work is still in progress, open it as a **Draft** and label it `wip`.
3. When the PR is opened (including as a Draft) or moved from Draft to Ready for Review, an automated review runs and posts findings as a comment. PRs carrying the `wip` label (or `WIP` / `[WIP]` in the title) are skipped; removing the `wip` label triggers the review.
4. Address the findings:
   - Critical, High, and Medium findings must be either fixed or explicitly acknowledged in a PR comment with rationale.
   - Low-severity suggestions and nits should be addressed when reasonable.
5. Re-run the automated review after fixes by commenting `@claude review`; repeat until no significant unresolved findings remain. New commits pushed after a review invalidate the `claude-review/reviewed` status on the previous commit — re-run `@claude review` to re-enable merging.
6. Perform a thorough self-review of the full diff.
7. Mark the PR **Ready for Review**; [CODEOWNERS](.github/CODEOWNERS) will auto-assign reviewers where applicable.

> **Note for external contributors:** the automated review does not run on PRs from forks. A maintainer will trigger it manually by commenting `@claude review` during the review process.

**Enforcement.** Merges to `master` are gated by a required `claude-review/reviewed` status check; the PR cannot be merged until Claude's latest review on the head commit reports no unresolved Critical, High, or Medium findings. Authors are responsible for not marking a PR Ready for Review while such findings remain unresolved and unexplained.

### DOs and DON'Ts

Please do:

- **DO** prioritize the current style of the project or file you're changing, even if it diverges from the general guidelines.
- **DO** include tests when adding new features. When fixing bugs, add a test highlighting how the current behavior is broken.
- **DO** fill out the issues and PR descriptions according to their templates. The elements in the templates are there for a good reason. Help the team.
- **DO** especially follow our rules in the [Contributing](https://github.com/NethermindEth/nethermind/blob/master/CODE_OF_CONDUCT.md#contributing) section of our code of conduct.

Please do not:

- **DON'T** make PRs for style changes or grammar fixes.
- **DON'T** create a new file without the proper file header.
- **DON'T** surprise us with big pull requests. Instead, file an issue and start a discussion so we can agree on a direction before you invest a significant amount of time.
- **DON'T** commit code that you didn't write. If you find code you think is a good fit to add to Nethermind, file an issue and start a discussion before proceeding.
- **DON'T** submit PRs that alter licensing-related files or headers. If you believe there's a problem with them, file an issue, and we'll be happy to discuss it.
- **DON'T** submit PRs that modify infrastructure components (e.g., GitHub Actions workflows or CI/CD scripts). If such changes are required to support your main contribution, include a brief justification, and they will be considered during review.

### Branch naming

Branch names must follow the `kebab-case` or `snake_case` pattern and be all lowercase. When possible, Follow the pattern `project-if-any/type-of-change/issue-title` and add an issue reference if applicable. For example:

- `feature/1234-issue-title`
- `shanghai/feature/1234-issue-title`
- `fix/1234-bug-description`
- `shanghai/refactor/title`

### File headers

The following notice must be included as a header in all source files if possible.

```
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
```

The `//` should be replaced according to the language. For example, for Linux shell scripts, it is `#`.
