// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution.Settlement;

namespace Nethermind.Eez.Follower;

/// <summary>
/// Which of a batch's claimed steps L1 ran. Each step moves the stored commitment one hop and emits one
/// <c>L2ExecutionPerformed</c>; a skipped step stops the commitment, so the steps that ran are contiguous.
/// </summary>
/// <param name="Start">Index in the claimed chain of the first step that ran.</param>
/// <param name="Length">How many consecutive steps ran; zero when none did.</param>
/// <param name="FinalState">The commitment after the last step that ran: the L2 block hash L1 now stores.</param>
/// <param name="EntryState">The commitment the run started from: the claimed current state, or the step before the run.</param>
public readonly record struct L1Settlement(int Start, int Length, ValueHash256 FinalState, ValueHash256 EntryState)
{
    public static readonly L1Settlement None = default;

    public bool IsEmpty => Length == 0;

    public ProducingSlice Effects => ProducingSlice.OfSettledRun(Start, Length);
}
