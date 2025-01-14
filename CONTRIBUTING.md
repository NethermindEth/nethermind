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

If your PR is not ready for review and merge because you are still working on it, please convert it to draft and label it with `wip` (work in progress).

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

### Branch naming

Branch names must follow the `kebab-case` or `snake_case` pattern and be all lowercase. When possible, Follow the pattern `project-if-any/type-of-change/issue-title` and add an issue reference if applicable. For example:

- `feature/1234-issue-title`
- `shanghai/feature/1234-issue-title`
- `fix/1234-bug-description`
- `shanghai/refactor/title`

### File headers

The following notice must be included as a header in all source files if possible.

```
// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
```

The `//` should be replaced according to the language. For example, for Linux shell scripts, it is `#`.
