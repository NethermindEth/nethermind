// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.StateDiffArchive.Data;

/// <summary>How an account changed in a block: not re-set (storage-only), upserted, or deleted.</summary>
public enum AccountChangeKind : byte
{
    /// <summary>The account itself was not written this block; only its storage changed (its storage root is reconciled from <see cref="AccountDiff.Slots"/> on replay).</summary>
    None = 0,

    /// <summary>The account was upserted to <see cref="AccountDiff.Account"/>.</summary>
    Set = 1,

    /// <summary>The account was deleted (<c>writeBatch.Set(addr, null)</c>), which also clears its storage.</summary>
    Deleted = 2,
}

/// <summary>
/// The committed per-block state diff captured from the world-state scope's write batch: the exact
/// account/storage/code writes that produced <see cref="StateRoot"/>. Replaying these writes through
/// <c>IWorldStateScopeProvider.IScope</c> and committing rebuilds the trie without the EVM.
/// </summary>
/// <remarks>
/// Trie nodes are intentionally not recorded — they are regenerated on commit. Code bytes are captured
/// (the scope snapshot only carries code hashes) so a replayed node is fully executable. Serialized via
/// <see cref="StateDiffRecordDecoder"/> and Snappy-compressed per block; see that type for the wire layout.
/// </remarks>
public sealed class StateDiffRecord(
    byte version,
    ulong blockNumber,
    Hash256 stateRoot,
    IReadOnlyList<AccountDiff> accounts,
    IReadOnlyList<CodeDiff> codes)
{
    public const byte CurrentVersion = 1;

    public byte Version => version;
    public ulong BlockNumber => blockNumber;
    public Hash256 StateRoot => stateRoot;
    public IReadOnlyList<AccountDiff> Accounts => accounts;
    public IReadOnlyList<CodeDiff> Codes => codes;
}

/// <summary>Per-address change: an optional account upsert/delete, an optional storage clear, and the changed slots.</summary>
public sealed class AccountDiff(
    Address address,
    AccountChangeKind change,
    Account? account,
    bool storageCleared,
    IReadOnlyList<SlotDiff> slots)
{
    public Address Address => address;
    public AccountChangeKind Change => change;

    /// <summary>The upserted account when <see cref="Change"/> is <see cref="AccountChangeKind.Set"/>; otherwise null.</summary>
    public Account? Account => account;

    /// <summary>Whether the storage write batch issued a <c>Clear()</c> (self-destruct) for this address.</summary>
    public bool StorageCleared => storageCleared;

    public IReadOnlyList<SlotDiff> Slots => slots;
}

/// <summary>A single storage slot write. A zero/empty <see cref="Value"/> denotes a cleared slot.</summary>
public readonly struct SlotDiff(UInt256 index, byte[] value)
{
    public UInt256 Index { get; } = index;
    public byte[] Value { get; } = value;
}

/// <summary>Contract code captured when its hash first appears in a block, so replay can re-insert it.</summary>
public readonly struct CodeDiff(ValueHash256 codeHash, byte[] code)
{
    public ValueHash256 CodeHash { get; } = codeHash;
    public byte[] Code { get; } = code;
}
