// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat.History;

public enum HistoryWalkMismatchKind : byte
{
    StateRoot,
    StorageRoot,
    MissingHeader,
    MissingAccountRow,
    CapturedMarker,
}

public readonly record struct HistoryWalkMismatch(ulong Block, HistoryWalkMismatchKind Kind, ValueHash256 Rebuilt, ValueHash256 Expected);

public readonly record struct HistoryWalkVerdict(bool Verified, ulong BlocksCompared, IReadOnlyList<HistoryWalkMismatch> Mismatches);
