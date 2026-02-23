---
name: pre-check
description: Explore existing patterns before implementing. Use proactively before writing new classes, modules, or significant features.
context: fork
agent: Explore
---

Before implementing, research the Nethermind codebase:

1. **Search for existing implementations** of the target interface or concept. Use Grep and Glob to find classes implementing the same interface.
2. **Check DI modules** in `src/Nethermind/Nethermind.Init/Modules/` for modules that already wire the needed components.
3. **Check test infrastructure** — does `TestBlockchain` (in `Nethermind.Core.Test/Blockchain/`) or `E2ESyncTests` already provide the test context needed?
4. **Check for base classes** — search for `abstract class` in the relevant project to find existing base classes to extend.
5. **Report findings** with exact file paths and line numbers.
6. **Propose 2-3 approaches** with trade-offs based on what you found.

Focus on: what already exists that can be reused, extended, or composed — rather than building from scratch.
