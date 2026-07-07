// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    /// Checks whether a fork ID advertised by a discovered peer matches the local fork schedule,
    /// without requiring the local head.
    /// </summary>
    /// <remarks>
    /// Intended for filtering discovery candidates by the ENR "eth" entry
    /// (https://github.com/ethereum/devp2p/blob/master/enr-entries/eth.md). More lenient than
    /// <see cref="ValidateForkId"/>: a discovered peer may be at any stage of sync, so its fork hash is
    /// accepted when it matches any past or future local fork. The advertised next transition is validated
    /// only when both sides define one, since 0 stands for "no fork known yet" per EIP-2124.
    /// </remarks>
    bool IsForkIdCompatible(ForkId peerId);

    ForkActivationsSummary GetForkActivationsSummary(BlockHeader? head);
}

public readonly record struct Fork(ForkActivation Activation, ForkId Id);

public readonly ref struct ForkActivationsSummary
{
    public Fork Current { get; init; }
    public Fork? Next { get; init; }
    public Fork? Last { get; init; }
}
