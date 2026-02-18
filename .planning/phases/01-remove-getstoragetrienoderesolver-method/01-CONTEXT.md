# Phase 1: Remove GetStorageTrieNodeResolver Method - Context

**Gathered:** 2026-02-19
**Status:** Ready for planning

<domain>
## Phase Boundary

Remove `GetStorageTrieNodeResolver` from `ITrieNodeResolver` interface and replace with a factory interface. This is a refactoring to separate concerns - the resolver doesn't need to know how to create child resolvers.

</domain>

<decisions>
## Implementation Decisions

### Design Pattern
- Create new `ITrieNodeResolverFactory` interface with `GetStorageTrieNodeResolver(Hash256? address)` method
- Remove method from `ITrieNodeResolver` interface entirely
- Keep method functionality but via factory pattern

### Factory Interface Scope
- Single method: `GetStorageTrieNodeResolver(Hash256? address) -> ITrieNodeResolver`
- No additional methods needed for v1

### Call Site Refactoring
- Update TrieNode.cs (3 call sites) to use factory
- Update PatriciaTree.cs (1 call site) to use factory  
- Update TrieNode.Visitor.cs (1 call site) to use factory

### Implementation Classes
- Remove from: ScopedTrieStore, ReadOnlyTrieStore, RawScopedTrieStore, NullTrieStore, CachedTrieStore, TrieNodeResolverWithReadFlags, NullTrieNodeResolver, EmptyTrieNodeResolver, UnknownNodeResolver, SnapUpperBoundAdapter
- Add factory implementations where needed

</decisions>

<specifics>
## Specific Ideas

- Factory pattern separates "resolving nodes" from "creating child resolvers"
- `IScopedTrieStore` already has the address baked in - that's the scoped view
- The factory is needed when you have an unscoped resolver and need to branch into storage

</specifics>

<deferred>
## Deferred Ideas

None - discussion stayed within phase scope

</deferred>

---

*Phase: 01-remove-getstoragetrienoderesolver-method*
*Context gathered: 2026-02-19*