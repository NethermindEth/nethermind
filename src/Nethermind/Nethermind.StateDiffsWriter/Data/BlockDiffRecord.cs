// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.StateDiffsWriter.Data;

/// <summary>
/// Canonical per-block payload persisted to the <c>BlockDiffs</c> column family
/// and consumed by the v19 sidecar's tail mode.
///
/// Wire format (RLP, top-level sequence):
/// <code>
/// BlockDiffRecord = [
///   BlockNumber             (uint64),
///   StateRoot               (32B),
///   CodeHashChanges = [
///     [ OldHash (32B|empty), NewHash (32B|empty), NewCodeSize (uint32) ],
///     ...
///   ],
///   SlotCountChanges = [
///     [ HashedAddress (32B), OldCount (uint64), NewCount (uint64) ],
///     ...
///   ],
///   AccountTrieBytesDelta   (int64, optional, defaults to 0),
///   StorageTrieBytesDelta   (int64, optional, defaults to 0),
///   AccountsAddedDelta      (int64, optional, defaults to 0)
/// ]
/// </code>
/// <para>
/// <c>OldHash</c> / <c>NewHash</c> use RLP empty-string encoding (<c>0x80</c>) to
/// denote "no code", matching <c>CodeHashChange.NoCode</c>. <c>NewCodeSize</c> is
/// the byte length of the code at <c>NewHash</c> resolved against the global
/// code DB; it is <c>0</c> whenever the account loses code in this block.
/// </para>
/// <para>
/// The trailing trio (<see cref="AccountTrieBytesDelta"/>,
/// <see cref="StorageTrieBytesDelta"/>, <see cref="AccountsAddedDelta"/>) is
/// strictly additive: decoders MUST treat absent trailing fields as zero so
/// pre-extension payloads still parse, and encoders MUST append the fields in
/// this order so older readers can ignore the suffix without misalignment.
/// </para>
/// <para>
/// The schema is append-only and version-less: any future addition will be
/// gated by the column-family layout, not by an in-record version byte. Keep
/// field order stable — the sidecar decodes positionally.
/// </para>
/// </summary>
public sealed record BlockDiffRecord(
    long BlockNumber,
    Hash256 StateRoot,
    IReadOnlyList<CodeHashEntry> CodeHashChanges,
    IReadOnlyList<SlotCountEntry> SlotCountChanges,
    long AccountTrieBytesDelta = 0,
    long StorageTrieBytesDelta = 0,
    long AccountsAddedDelta = 0);

/// <summary>
/// Single row of <see cref="BlockDiffRecord.CodeHashChanges"/>. <see cref="OldHash"/>
/// and <see cref="NewHash"/> use the <c>default</c> <see cref="ValueHash256"/> sentinel
/// for "no code" (RLP-encoded as the empty byte string).
/// </summary>
public readonly record struct CodeHashEntry(
    ValueHash256 OldHash,
    ValueHash256 NewHash,
    uint NewCodeSize);

/// <summary>
/// Single row of <see cref="BlockDiffRecord.SlotCountChanges"/>: the per-contract
/// pre- and post-block storage-slot totals. The sidecar derives delta + bucket
/// transitions from these counts without maintaining a second running map.
/// </summary>
public readonly record struct SlotCountEntry(
    ValueHash256 AddressHash,
    ulong OldCount,
    ulong NewCount);
