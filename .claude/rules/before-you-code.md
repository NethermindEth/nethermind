# Before You Code

Before implementing any new class, module, or significant feature:

1. **Search for existing implementations** of the same interface or concept. If a base class or helper exists, extend it rather than reimplementing.
2. **Check DI modules** in `Nethermind.Init/Modules/` and the relevant project's module registrations. Don't manually wire what a module already provides.
3. **Check test infrastructure** before writing test setup. `TestBlockchain` and `E2ESyncTests` provide pre-built environments with full DI wiring. Don't mock individual interfaces when an integration fixture exists.
4. **Look at similar code in the codebase** before creating new patterns. Search for how the same interface is used elsewhere.
5. **Present 2-3 approach options** with trade-offs rather than picking one silently. Explain what existing code you found and how your approach builds on it.
