# Contributing to Nethermind

The Nethermind team maintains guidelines for contributing to the Nethermind repos. Check out our [docs page](https://docs.nethermind.io/nethermind/) for more info about us.

### Code of Conduct

Have you read the [code of conduct](https://github.com/NethermindEth/nethermind/blob/master/CODE_OF_CONDUCT.md)?

## Bugs and Feature Request

Before you make your changes, check to see if an [issue](https://github.com/NethermindEth/nethermind/issues) exists already for the change you want to make.

### Don't see your issue? Open one

If you spot something new, open an issue using a [template](https://github.com/NethermindEth/nethermind/issues/new/choose). We'll use the issue to have a conversation about the problem you want to fix.

### Open a Pull Request

When you're done making changes and you'd like to propose them for review, use the pull request template to open your PR (pull request).

If your PR is not ready for review and merge because you are still working on it, please convert it to draft and add to it the label `wip` (work in progress). This label allows to filter correctly the rest of PR not `wip`.

### Do you intend to add a new feature or change an existing one?

Suggest your change by opening an issue and starting a discussion.

### Improving Issues and PR

Please add, if possible, a reviewer, assignees and labels to your issue and PR.

## DOs and DON'Ts

Please do:

-   **DO** give priority to the current style of the project or file you're changing even if it diverges from the general guidelines.
-   **DO** include tests when adding new features. When fixing bugs, start with adding a test that highlights how the current behavior is broken.
-   **DO** especially follow our rules in the [Contributing](https://github.com/NethermindEth/nethermind/blob/master/CODE_OF_CONDUCT.md#contributing) section of our code of conduct.

Please do not:

-   **DON'T** create a new file without the proper file header.
-   **DON'T** fill the issues and PR descriptions vaguely. The elements in the templates are there for a good reason. Help the team.
-   **DON'T** surprise us with big pull requests. Instead, file an issue and start a discussion so we can agree on a direction before you invest a large amount of time.

## Branch Naming

Branch names must follow `snake_case` pattern. Follow the pattern `<projectIfAny>/<typeOfTheChange>/<issueNo>_<title>` when it is possible and add issue reference if applicable. For example:

-   feature/1234_issue_title
-   shanghai/feature/1234_issue_title
-   fix/2345_bug_title
-   shanghai/refactor/4567_title

## File Headers

The following notice must be included as a header in all source files if possible.

```
// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
```

The `//` should be replaced depending on the file. For example, for Linux shell scripts, it is `#`.
