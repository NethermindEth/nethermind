# Requirements: Remove GetStorageTrieNodeResolver

**Defined:** 2026-02-19
**Core Value:** Remove dead/abstraction-leaking method from the trie store interfaces to simplify the architecture and eliminate technical debt.

## v1 Requirements

Requirements for removing the `GetStorageTrieNodeResolver` method from the trie store hierarchy.

### Interface Removal

- [ ] **IFACE-01**: Remove `GetStorageTrieNodeResolver` method from `ITrieNodeResolver` interface
- [ ] **IFACE-02**: Verify no other interfaces reference this method after removal

### Implementation Removal

- [ ] **IMPL-01**: Remove `GetStorageTrieNodeResolver` from `ScopedTrieStore`
- [ ] **IMPL-02**: Remove `GetStorageTrieNodeResolver` from `ReadOnlyTrieStore.ScopedReadOnlyTrieStore`
- [ ] **IMPL-03**: Remove `GetStorageTrieNodeResolver` from `RawScopedTrieStore`
- [ ] **IMPL-04**: Remove `GetStorageTrieNodeResolver` from `NullTrieStore`
- [ ] **IMPL-05**: Remove `GetStorageTrieNodeResolver` from `CachedTrieStore`
- [ ] **IMPL-06**: Remove `GetStorageTrieNodeResolver` from `TrieNodeResolverWithReadFlags`
- [ ] **IMPL-07**: Remove `GetStorageTrieNodeResolver` from `NullTrieNodeResolver`
- [ ] **IMPL-08**: Remove `GetStorageTrieNodeResolver` from `EmptyTrieNodeResolver`
- [ ] **IMPL-09**: Remove `GetStorageTrieNodeResolver` from `UnknownNodeResolver`
- [ ] **IMPL-10**: Remove `GetStorageTrieNodeResolver` from `SnapUpperBoundAdapter`

### Call Site Refactoring

- [ ] **CALL-01**: Refactor `TrieNode.cs` to not call `GetStorageTrieNodeResolver`
- [ ] **CALL-02**: Refactor `PatriciaTree.cs` to not call `GetStorageTrieNodeResolver`
- [ ] **CALL-03**: Refactor `TrieNode.Visitor.cs` to not call `GetStorageTrieNodeResolver`

### Test Updates

- [ ] **TEST-01**: Update or remove test code that references `GetStorageTrieNodeResolver`
- [ ] **TEST-02**: Ensure existing tests pass after refactoring

### Verification

- [ ] **VER-01**: Build succeeds without errors
- [ ] **VER-02**: All trie-related tests pass
- [ ] **VER-03**: No remaining references to `GetStorageTrieNodeResolver` in codebase

## v2 Requirements

(None)

## Out of Scope

| Feature | Reason |
|---------|--------|
| Full PatriciaTree architecture refactoring | Beyond scope of method removal |
| StorageTrie decoupling | Separate effort |
| Performance optimization | Not the goal of this task |

## Traceability

Which phases cover which requirements. Updated during roadmap creation.

| Requirement | Phase | Status |
|-------------|-------|--------|
| IFACE-01 | Phase 1 | Pending |
| IFACE-02 | Phase 1 | Pending |
| IMPL-01 | Phase 1 | Pending |
| IMPL-02 | Phase 1 | Pending |
| IMPL-03 | Phase 1 | Pending |
| IMPL-04 | Phase 1 | Pending |
| IMPL-05 | Phase 1 | Pending |
| IMPL-06 | Phase 1 | Pending |
| IMPL-07 | Phase 1 | Pending |
| IMPL-08 | Phase 1 | Pending |
| IMPL-09 | Phase 1 | Pending |
| IMPL-10 | Phase 1 | Pending |
| CALL-01 | Phase 1 | Pending |
| CALL-02 | Phase 1 | Pending |
| CALL-03 | Phase 1 | Pending |
| TEST-01 | Phase 1 | Pending |
| TEST-02 | Phase 1 | Pending |
| VER-01 | Phase 1 | Pending |
| VER-02 | Phase 1 | Pending |
| VER-03 | Phase 1 | Pending |

**Coverage:**
- v1 requirements: 20 total
- Mapped to phases: 20
- Unmapped: 0 ✓

---
*Requirements defined: 2026-02-19*
*Last updated: 2026-02-19 after initial definition*