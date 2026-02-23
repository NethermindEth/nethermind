---
name: learn-from-review
description: Extract patterns from PR review feedback and propose rule additions.
disable-model-invocation: true
allowed-tools: Bash(gh *), Read, Write, Grep, Glob
---

Given a PR number, extract actionable patterns from review feedback:

1. **Fetch review comments**:
   ```
   gh api repos/NethermindEth/nethermind/pulls/$ARGUMENTS/comments --jq '.[] | "\(.user.login): \(.body)\nFile: \(.path):\(.line)\n---"'
   ```

2. **Fetch review bodies**:
   ```
   gh api repos/NethermindEth/nethermind/pulls/$ARGUMENTS/reviews --jq '.[] | select(.body != "") | "\(.author.login) (\(.state)): \(.body)\n---"'
   ```

3. **Extract actionable patterns** from the feedback — things that are general conventions, not one-off fixes. Look for:
   - Architectural feedback (DI patterns, module usage)
   - Style feedback (naming, structure)
   - Performance feedback (allocation, LINQ usage)
   - Test feedback (infrastructure usage, test structure)

4. **Check existing rules**: Read `.claude/rules/` files and check if each extracted pattern is already documented.

5. **Propose additions**: For new patterns not in existing rules, propose the specific text to add to the relevant rules file. Show the diff.

6. **Present for approval** before writing any changes. List what's new vs already covered.
