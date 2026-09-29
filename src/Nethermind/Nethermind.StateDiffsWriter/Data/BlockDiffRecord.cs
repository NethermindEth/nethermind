// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.StateDiffsWriter.Data;

/// <summary>
/// Canonical per-block payload persisted to the <c>BlockDiffs</c> column family and consumed by an
/// external reader. Append-only, version-less, decoded positionally; keep field order stable.
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
/// <c>OldHash</c>/<c>NewHash</c> use RLP empty-string (<c>0x80</c>) for "no code". The trailing trio
/// is strictly additive: decoders MUST treat absent fields as zero and encoders MUST append in this order.
/// </summary>
public sealed record BlockDiffRecord(
    long BlockNumber,
    Hash256 StateRoot,
    IReadOnlyList<CodeHashEntry> CodeHashChanges,
    IReadOnlyList<SlotCountEntry> SlotCountChanges,
    long AccountTrieBytesDelta = 0,
    long StorageTrieBytesDelta = 0,
    long AccountsAddedDelta = 0);

/// <summary>Single row of <see cref="BlockDiffRecord.CodeHashChanges"/>; default hash is the "no code" sentinel.</summary>
public readonly record struct CodeHashEntry(
    ValueHash256 OldHash,
    ValueHash256 NewHash,
    uint NewCodeSize);

/// <summary>Single row of <see cref="BlockDiffRecord.SlotCountChanges"/>: per-contract pre/post storage-slot totals.</summary>
public readonly record struct SlotCountEntry(
    ValueHash256 AddressHash,
    ulong OldCount,
    ulong NewCount);
