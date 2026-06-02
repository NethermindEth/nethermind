// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.StateComposition.Diff;

namespace Nethermind.StateComposition.Data;

/// <summary>
/// Exact delta between two state roots. Contains both additions and removals
/// for every metric category. Apply to <see cref="CumulativeTrieStats"/> via
/// <see cref="CumulativeTrieStats.ApplyDiff"/>.
///
/// Content-addressed property: walking old root vs new root gives exact counts
/// of nodes/accounts/slots that exist in one but not the other.
/// </summary>
internal readonly record struct TrieDiff(
    int AccountsAdded,
    int AccountsRemoved,
    int ContractsAdded,
    int ContractsRemoved,

    int AccountTrieBranchesAdded,
    int AccountTrieBranchesRemoved,
    int AccountTrieExtensionsAdded,
    int AccountTrieExtensionsRemoved,
    int AccountTrieLeavesAdded,
    int AccountTrieLeavesRemoved,
    long AccountTrieBytesAdded,
    long AccountTrieBytesRemoved,

    int StorageTrieBranchesAdded,
    int StorageTrieBranchesRemoved,
    int StorageTrieExtensionsAdded,
    int StorageTrieExtensionsRemoved,
    int StorageTrieLeavesAdded,
    int StorageTrieLeavesRemoved,
    long StorageTrieBytesAdded,
    long StorageTrieBytesRemoved,

    long StorageSlotsAdded,
    long StorageSlotsRemoved,

    int ContractsWithStorageAdded,
    int ContractsWithStorageRemoved,
    int EmptyAccountsAdded,
    int EmptyAccountsRemoved,

    // Per-depth distribution delta. Always non-null; an unseeded instance
    // (TrackDepthIncrementally disabled) is a no-op under CumulativeDepthStats.AddInPlace.
    // Reuses CumulativeDepthStats as the delta container — identical field layout,
    // merged into the holder's baseline via CumulativeDepthStats.AddInPlace.
    CumulativeDepthStats DepthDelta,

    IReadOnlyList<SlotCountChange> SlotCountChanges,
    IReadOnlyList<CodeHashChange> CodeHashChanges
)
{
    public int NetAccounts => AccountsAdded - AccountsRemoved;
    public int NetContracts => ContractsAdded - ContractsRemoved;
    public long NetStorageSlots => StorageSlotsAdded - StorageSlotsRemoved;
    public int NetContractsWithStorage => ContractsWithStorageAdded - ContractsWithStorageRemoved;
    public int NetEmptyAccounts => EmptyAccountsAdded - EmptyAccountsRemoved;

    public long NetAccountTrieBytes => AccountTrieBytesAdded - AccountTrieBytesRemoved;
    public long NetStorageTrieBytes => StorageTrieBytesAdded - StorageTrieBytesRemoved;

    /// <summary>
    /// Zero-delta sentinel returned by <see cref="TrieDiffWalker.ComputeDiff"/>
    /// when both roots are equal. Callers iterate <see cref="SlotCountChanges"/>
    /// / <see cref="CodeHashChanges"/> as empty lists and feed
    /// <see cref="DepthDelta"/> through the unseeded no-op path on the holder.
    /// </summary>
    public static TrieDiff Empty { get; } = new(
        0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0,
        0, 0,
        0, 0, 0, 0,
        DepthDelta: new CumulativeDepthStats(),
        SlotCountChanges: Array.Empty<SlotCountChange>(),
        CodeHashChanges: Array.Empty<CodeHashChange>());
}

/// <summary>
/// Net change in the storage-slot count for a single contract across one diff.
/// <see cref="SlotDelta"/> is positive when slots were added, negative when removed.
/// The state holder combines this with its per-address baseline to know which
/// slot-histogram bucket a contract moved between.
/// </summary>
internal readonly record struct SlotCountChange(ValueHash256 AddressHash, long SlotDelta);

/// <summary>
/// Per-account code-hash transition across one diff. The <see cref="ValueHash256"/>
/// <c>default</c> sentinel represents "no code" — either the account never had code
/// (gained it in this diff) or lost it entirely. Distinct from
/// <see cref="Keccak.OfAnEmptyString"/> which is the empty-bytecode hash.
/// </summary>
internal readonly record struct CodeHashChange(
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
