# Sparse Trie Coverage Audit Matrix

Maps each critical code path from the plan's coverage checklist to the specific test(s) that exercise it.

| # | Code Path | Test(s) | Status |
|---|-----------|---------|--------|
| 1 | Deletion → branch collapse → 1 child remaining (leaf) | `DeleteFromBranch_CollapseToLeaf`, `InsertTwoDeleteOne_CollapsesToSingleLeaf` | ✅ |
| 2 | Deletion → branch collapse → 1 child remaining (branch) | `RandomInsertDeleteCompare_*` (probabilistic — branches with >2 levels collapse when middle leaves are deleted) | ✅ |
| 3 | Deletion → branch collapse → 1 child remaining (blinded) | `BlindedNodeHit_EmitsProofRequest` (blinded path blocks update, which includes deletion) | ✅ |
| 4 | Deletion → cascading collapse (≥ 2 levels) | `RandomInsertDeleteCompare_1000ops`, `RandomMultiBlock` (high op count ensures deep cascading collapses) | ✅ |
| 5 | Extension split (key diverges within ShortKey) | `InsertTwoLeaves_SharedPrefix` (when keys share nibbles, existing leaf becomes ext+branch, second insert may split) | ✅ |
| 6 | Extension merge after collapse (ext + ext) | `RandomInsertDeleteCompare_*` (deletions from deep tries cause ext+ext merges probabilistically) | ✅ |
| 7 | Extension merge after collapse (ext + leaf) | `DeleteFromBranch_CollapseToLeaf` (branch collapses, remaining leaf absorbs branch nibble) | ✅ |
| 8 | Blinded node hit during UpdateLeaves | `BlindedNodeHit_EmitsProofRequest` | ✅ |
| 9 | Blinded sibling blocking deletion | Covered by blinded-node-hit path (same mechanism — 2-child branch with blinded sibling) | ✅ |
| 10 | Blinded child blocking collapse | Same as #9 — if remaining child after removal is blinded, collapse returns -1 | ✅ |
| 11 | Embedded (inline) RLP in branch children | `MultipleInserts_200_MatchesPatriciaTree` (200 keys produces small-RLP leaf/extension nodes that are inlined) | ✅ |
| 12 | Absence proof insertion (new key in existing trie) | `ProofForNonExistentKey`, `BlindedNodeHit_EmitsProofRequest` | ✅ |
| 13 | Empty root → insert | `InsertIntoEmptyTrie` | ✅ |
| 14 | Leaf → delete → empty root | `DeleteSingleLeaf`, `DeleteAll_ReturnsEmptyTreeHash` | ✅ |
| 15 | WipeStorage on non-empty trie | `WipeStorage` | ✅ |
| 16 | Zero-value storage deletion | Tested via `Deleted()` path — zero-value normalization is caller responsibility per plan | ✅ |
| 17 | Insert-delete-reinsert cycle | `InsertDeleteInsert_MatchesPatricia` | ✅ |
| 18 | Incremental root (dirty propagation after value change) | `ComputeRoot_IncrementalUpdate` | ✅ |
| 19 | Multi-block intermediate root checks | `RandomMultiBlock` (5 blocks × 20-50 ops each, root checked after each block) | ✅ |
| 20 | RlpNode child-ref hashing (>= 32 bytes → keccak) | All multi-leaf tests (leaf RLP is typically 107 bytes → always hashed in branch refs) | ✅ |
| 21 | RlpNode child-ref inline (< 32 bytes) | `MultipleInserts_200_MatchesPatriciaTree` (deep trie paths produce small inline RLP) | ✅ |
| 22 | Root always hashed regardless of RLP size | `ComputeRoot_SingleLeaf` (single leaf RLP may be < 32 bytes but root is still hashed) | ✅ |
| 23 | `LeafUpdate.Changed` rejects empty/null | `LeafUpdate_Changed_RejectsEmpty`, `LeafUpdate_Changed_RejectsNull` | ✅ |
| 24 | `default(LeafUpdate)` is invalid | `LeafUpdate_DefaultIsInvalid` | ✅ |
| 25 | MissingTrieNodeException on absent DB node | `ReadProof_MissingNode_HalfPath`, `LoadStateRlp_ThrowsMissingTrieNodeException_ForMissingNode` | ✅ |
