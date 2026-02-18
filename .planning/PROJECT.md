# Remove IScopedTrieStore.GetStorageTrieNodeResolver

## What This Is

Remove the `GetStorageTrieNodeResolver` method from the trie store hierarchy in the Nethermind Ethereum client. This method is inherited from `ITrieNodeResolver` interface and is marked as technical debt (TODO: "Find a way to not have this. PatriciaTrie on its own does not need the concept of storage.").

## Core Value

Remove dead/abstraction-leaking method from the trie store interfaces to simplify the architecture and eliminate technical debt.

## Requirements

### Validated

(None yet — ship to validate)

### Active

- [ ] Remove `GetStorageTrieNodeResolver` from `ITrieNodeResolver` interface
- [ ] Remove implementations from all implementing classes
- [ ] Remove all call sites that use this method
- [ ] Verify build passes after removal
- [ ] Run existing tests to ensure no regressions

### Out of Scope

- [Full PatriciaTree refactoring] — Beyond scope of this specific removal
- [StorageTrie architecture changes] — Not part of this task

## Context

The Nethermind trie store has a hierarchy:
- `ITrieNodeResolver` - base interface with node resolution methods
- `IScopedTrieStore : ITrieNodeResolver` - scoped view for account/storage tries  
- `ITrieStore : IScopableTrieStore` - full trie store

The `GetStorageTrieNodeResolver` method exists because historically the trie needed to switch between state trie and storage tries during traversal. The TODO comment suggests this is a leaky abstraction that should be removed.

Usage locations found:
- `Nethermind.Trie/TrieNode.cs` (lines 989, 1101, 1203)
- `Nethermind.Trie/PatriciaTree.cs` (line 958)
- `Nethermind.Trie/TrieNode.Visitor.cs` (line 141)
- Multiple test files

## Constraints

- **[Compatibility]**: Must not break existing functionality — tests must pass
- **[Scope]**: Single focused refactoring — remove method only, no broader changes

## Key Decisions

| Decision | Rationale | Outcome |
|----------|-----------|---------|
| Remove method entirely | The TODO explicitly asks to find a way to not have this | — Pending |

---
*Last updated: 2026-02-19 after initialization*