// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.History.Proofs;

namespace Nethermind.State.Flat.History.Walk;

internal readonly record struct WalkReplayContext(
    ulong From,
    ulong To,
    CommitmentEmitter? Emitter,
    SeriesWriter Series,
    WalkProgress Progress,
    int Item,
    CancellationToken Token);
