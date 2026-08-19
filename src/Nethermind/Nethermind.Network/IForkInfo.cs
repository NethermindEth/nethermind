// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Network;

public interface IForkInfo
{
    ForkId GetForkId(ulong headNumber, ulong headTimestamp);

    /// <summary>
    /// Verify that the forkid from peer matches our forks.
    /// </summary>
    /// <param name="peerId"></param>
    /// <param name="head"></param>
    /// <returns></returns>
    ValidationResult ValidateForkId(ForkId peerId, BlockHeader? head);

    /// <summary>
    /// Checks whether a discovered peer fork ID belongs to the local fork schedule without local head state.
    /// </summary>
    bool IsForkIdCompatible(ForkId peerId);

    ForkActivationsSummary GetForkActivationsSummary(BlockHeader? head);

    /// <summary>
    /// Retrieves all configured forks in activation order.
    /// </summary>
    /// <returns>A read-only span containing the complete fork schedule.</returns>
    ReadOnlySpan<Fork> GetAllForks();
}

public readonly record struct Fork(ForkActivation Activation, ForkId Id);

public readonly ref struct ForkActivationsSummary
{
    public Fork Current { get; init; }
    public Fork? Next { get; init; }
    public Fork? Last { get; init; }
}
