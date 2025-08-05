// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Network;

public interface IForkInfo
{
    ForkId GetForkId(long headNumber, ulong headTimestamp);

    /// <summary>
    /// Verify that the forkid from peer matches our forks.
    /// </summary>
    /// <param name="peerId"></param>
    /// <param name="head"></param>
    /// <returns></returns>
    ValidationResult ValidateForkId(ForkId peerId, BlockHeader? head);

    ForkActivationsSummary GetForkActivationsSummary(BlockHeader? head);
}

public readonly record struct Fork(ForkActivation Activation, ForkId Id);

public readonly ref struct ForkActivationsSummary
{
    public Fork Current { get; init; }
    public Fork? Next { get; init; }
    public Fork? Last { get; init; }
}
