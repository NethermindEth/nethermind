# Roadmap: Remove GetStorageTrieNodeResolver

## Overview

**Project:** Remove IScopedTrieStore.GetStorageTrieNodeResolver  
**Total Phases:** 1  
**Total Requirements:** 20 (all mapped)  
**Depth:** Quick

## Phase 1: Remove GetStorageTrieNodeResolver Method

**Goal:** Remove the `GetStorageTrieNodeResolver` method from the trie store hierarchy and refactor all call sites.

**Requirements:** IFACE-01, IFACE-02, IMPL-01, IMPL-02, IMPL-03, IMPL-04, IMPL-05, IMPL-06, IMPL-07, IMPL-08, IMPL-09, IMPL-10, CALL-01, CALL-02, CALL-03, TEST-01, TEST-02, VER-01, VER-02, VER-03

**Success Criteria:**

1. Interface method removed - `ITrieNodeResolver.GetStorageTrieNodeResolver` is no longer defined
2. All implementations removed - 10 implementing classes no longer have this method
3. Call sites refactored - 3 main files (TrieNode.cs, PatriciaTree.cs, TrieNode.Visitor.cs) no longer call the method
4. Tests updated - test files that depend on this method are updated or removed
5. Build passes - `dotnet build` succeeds without errors
6. Tests pass - trie-related tests continue to pass
7. No remaining references - grep confirms no remaining usages in main codebase

**Notes:**
- This is a single-phase project due to the focused scope
- The method has a TODO comment in the source indicating it should be removed
- No research needed - this is a refactoring task on existing code

---

*Roadmap created: 2026-02-19*
*Last updated: 2026-02-19 after initial roadmap*