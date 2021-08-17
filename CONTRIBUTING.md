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

* **DO** give priority to the current style of the project or file you're changing even if it diverges from the general guidelines.
* **DO** include tests when adding new features. When fixing bugs, start with adding a test that highlights how the current behavior is broken.
* **DO** especially follow our rules in the [Contributing](https://github.com/NethermindEth/nethermind/blob/master/CODE_OF_CONDUCT.md#contributing) section of our code of conduct.
  
Please do not:

* **DON'T** create a new file without the proper file header.
* **DON'T** fill the issues and PR descriptions vaguely. The elements in the templates are there for a good reason. Help the team. 
* **DON'T** surprise us with big pull requests. Instead, file an issue and start a discussion so we can agree on a direction before you invest a large amount of time.

## Branch Naming

Branch names must follow `snake_case` pattern. Follow the pattern `feature/<name>` or `fix/<name>` `(folder/<name>)` when it is possible and add issue reference if applicable.

## File Headers

The following file header is the used for Nethermind. Please use it for new files.

```
//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
```
