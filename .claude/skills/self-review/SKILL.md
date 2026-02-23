---
name: self-review
description: Review changes against Nethermind conventions before creating a PR.
disable-model-invocation: true
allowed-tools: Bash(dotnet *), Bash(git *), Read, Grep, Glob
---

Review the current branch's changes against Nethermind conventions.

**First**: determine the diff to review:
```bash
BASE=$(git merge-base HEAD origin/master)
git diff $BASE...HEAD --stat
```
Use `git diff $BASE...HEAD` (not plain `git diff`) for all checks below. This covers all committed + uncommitted changes on the branch.

If the argument `$ARGUMENTS` is a PR number, fetch it first:
```bash
gh pr diff $ARGUMENTS -- '*.cs' '*.csproj'
```

**Checks**:

1. **Build check**: Run `dotnet build src/Nethermind/Nethermind.slnx` to verify compilation
2. **Format check**: Run `dotnet format whitespace src/Nethermind/ --folder --verify-no-changes` to check formatting
3. **DI anti-patterns**: In the diff, look for manual component wiring — chains of `new Foo(new Bar(...))` that should use Autofac modules. Check `Nethermind.Init/Modules/` for existing modules that wire those components. Flag any `new BlockProcessor(`, `new TransactionProcessor(`, `new BranchProcessor(` etc. that bypass DI.
4. **Style violations**: In changed `.cs` files, check for `var` usage, LINQ in hot paths (`.Select(`, `.Where(`, `.Any(`), `== null` instead of `is null`
5. **CPM compliance**: Check any modified `.csproj` files for `Version=` in PackageReference (versions must go in `Directory.Packages.props`)
6. **Test patterns**: Check if tests manually mock interfaces that `TestBlockchain` already provides (IWorldState, IBlockProcessor, IStateReader, etc.)
7. **Missing tests**: For bug fixes, verify a regression test exists

Report all findings with file paths and line numbers. Group by severity: **critical** (must fix) vs **suggestion** (consider fixing).
