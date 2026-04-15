// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.StateComposition.Diff;

namespace Nethermind.StateComposition.Data;

/// <summary>
/// Exact delta between two state roots. Contains both additions and removals
/// for every metric category. Apply to <see cref="CumulativeSizeStats"/> via
/// <see cref="CumulativeSizeStats.ApplyDiff"/>.
///
/// Content-addressed property: walking old root vs new root gives exact counts
/// of nodes/accounts/slots that exist in one but not the other.
/// </summary>
public readonly record struct TrieDiff(
    // Account-level changes
    int AccountsAdded,
    int AccountsRemoved,
    int ContractsAdded,
    int ContractsRemoved,

    // Account trie structure changes
    int AccountTrieBranchesAdded,
    int AccountTrieBranchesRemoved,
    int AccountTrieExtensionsAdded,
    int AccountTrieExtensionsRemoved,
    int AccountTrieLeavesAdded,
    int AccountTrieLeavesRemoved,
    long AccountTrieBytesAdded,
    long AccountTrieBytesRemoved,

    // Storage trie structure changes (aggregate across all contracts)
    int StorageTrieBranchesAdded,
    int StorageTrieBranchesRemoved,
    int StorageTrieExtensionsAdded,
    int StorageTrieExtensionsRemoved,
    int StorageTrieLeavesAdded,
    int StorageTrieLeavesRemoved,
    long StorageTrieBytesAdded,
    long StorageTrieBytesRemoved,

    // Storage slot changes
    long StorageSlotsAdded,
    long StorageSlotsRemoved,

    // Semantic account state changes (HasStorage / IsTotallyEmpty transitions)
    int ContractsWithStorageAdded,
    int ContractsWithStorageRemoved,
    int EmptyAccountsAdded,
    int EmptyAccountsRemoved,

    // Optional per-depth distribution delta. Null when TrackDepthIncrementally is disabled.
    // Reuses CumulativeDepthStats as the delta container — identical field layout,
    // merged into the holder's baseline via CumulativeDepthStats.AddInPlace.
    CumulativeDepthStats? DepthDelta = null,

    // Per-account payloads that feed the incremental trackers in the state holder.
    // Null on diffs that don't need them (tests, synthetic apply paths).
    IReadOnlyList<SlotCountChange>? SlotCountChanges = null,
    IReadOnlyList<CodeHashChange>? CodeHashChanges = null
)
{
    public int NetAccounts => AccountsAdded - AccountsRemoved;
    public int NetContracts => ContractsAdded - ContractsRemoved;
    public long NetStorageSlots => StorageSlotsAdded - StorageSlotsRemoved;
    public int NetContractsWithStorage => ContractsWithStorageAdded - ContractsWithStorageRemoved;
    public int NetEmptyAccounts => EmptyAccountsAdded - EmptyAccountsRemoved;

    public long NetAccountTrieBytes => AccountTrieBytesAdded - AccountTrieBytesRemoved;
    public long NetStorageTrieBytes => StorageTrieBytesAdded - StorageTrieBytesRemoved;
}

/// <summary>
/// Net change in the storage-slot count for a single contract across one diff.
/// <see cref="SlotDelta"/> is positive when slots were added, negative when removed.
/// The state holder combines this with its per-address baseline to know which
/// slot-histogram bucket a contract moved between.
/// </summary>
public readonly record struct SlotCountChange(ValueHash256 AddressHash, long SlotDelta);

/// <summary>
/// Per-account code-hash transition across one diff. The <see cref="ValueHash256"/>
/// <c>default</c> sentinel represents "no code" — either the account never had code
/// (gained it in this diff) or lost it entirely. Distinct from
/// <see cref="Keccak.OfAnEmptyString"/> which is the empty-bytecode hash.
/// </summary>
public readonly record struct CodeHashChange(
    ValueHash256 AddressHash,
    ValueHash256 OldCodeHash,
    ValueHash256 NewCodeHash)
{
    /// <summary>
    /// Sentinel ValueHash256 used when an account has no code on one side of the diff.
    /// Using <c>default</c> (all zeros) instead of <see cref="Keccak.OfAnEmptyString"/>
    /// so that every "gained code" / "lost code" event is unambiguous, even if an
    /// account temporarily has the empty-string code hash.
    /// </summary>
    public static ValueHash256 NoCode => default;

    public bool HadCode => OldCodeHash != NoCode;
    public bool HasCode => NewCodeHash != NoCode;
}
