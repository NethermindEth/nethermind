// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat.History;

/// <summary>What a history walk found to disagree between the rebuilt rows and this node's own records.</summary>
public enum HistoryWalkMismatchKind : byte
{
    /// <summary>The state root rebuilt from account rows differs from the header's.</summary>
    StateRoot,
    /// <summary>A storage root rebuilt from slot rows differs from the one on the owner's account row.</summary>
    StorageRoot,
    /// <summary>No header could be read for a block inside the walked range.</summary>
    MissingHeader,
    /// <summary>A block changed a slot without an account row for the slot's owner.</summary>
    MissingAccountRow,
    /// <summary>The captured per-block marker does not match the header the serving gate trusts.</summary>
    CapturedMarker,
    /// <summary>An account row's storage root moved with no slot history and no clear at its block.</summary>
    MissingSlotHistory,
}

/// <summary>One disagreement, at the block that names it; <c>Rebuilt</c> and <c>Expected</c>
/// carry the two hashes the kind compares, and are meaningful only for root-comparing kinds.</summary>
public readonly record struct HistoryWalkMismatch(ulong Block, HistoryWalkMismatchKind Kind, ValueHash256 Rebuilt, ValueHash256 Expected);

/// <summary>The outcome of one walk: <c>Verified</c> is true only when every compared block matched,
/// <c>BlocksCompared</c> counts headers actually checked, and an unfinished walk reports what it saw.
/// A reference type, so a publish-once field holds it without boxing.</summary>
public sealed record HistoryWalkVerdict(bool Verified, ulong BlocksCompared, IReadOnlyList<HistoryWalkMismatch> Mismatches);
