// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.BeaconChain.ForkChoice;

/// <summary>Which of a block's fork-choice nodes a weight or vote refers to: the spec's <c>PayloadStatus</c>.</summary>
/// <remarks>
/// specs/gloas/fork-choice.md Constants and <c>ForkChoiceNode</c>: a block is the <see cref="Pending"/> node, whose
/// children are its <see cref="Empty"/> and <see cref="Full"/> nodes. Distinct from <see cref="ExecutionStatus"/>,
/// which is the execution layer's verdict on a payload.
/// </remarks>
public enum ForkChoicePayloadStatus : byte
{
    Empty = 0,
    Full = 1,
    Pending = 2,
}
