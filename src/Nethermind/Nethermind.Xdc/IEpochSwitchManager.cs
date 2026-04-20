// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc;

public interface IEpochSwitchManager
{
    /// <summary>
    /// Determines if an epoch switch occurs at the given round, based on the parent block.
    /// </summary>
    bool IsEpochSwitchAtRound(ulong currentRound, XdcBlockHeader parent);

    /// <summary>
    /// Determines if the given block is an epoch switch block.
    /// </summary>
    bool IsEpochSwitchAtBlock(XdcBlockHeader header);

    /// <summary>
    /// Returns epoch switch info for the epoch containing the block with the given header.
    /// </summary>
    EpochSwitchInfo? GetEpochSwitchInfo(XdcBlockHeader header);

    /// <summary>
    /// Returns epoch switch info for the epoch containing the block with the given hash.
    /// </summary>
    EpochSwitchInfo? GetEpochSwitchInfo(Hash256 blockHash);

    /// <summary>
    /// Returns epoch switch info for the epoch containing the given consensus round.
    /// </summary>
    EpochSwitchInfo? GetEpochSwitchInfo(ulong round);

    /// <summary>
    /// Returns epoch switch info for the epoch in which the timeout certificate was produced.
    /// </summary>
    EpochSwitchInfo? GetTimeoutCertificateEpochInfo(TimeoutCertificate timeoutCertificate);

    /// <summary>
    /// Returns the epoch switch block info for the given epoch number, or null if not found.
    /// </summary>
    BlockRoundInfo? GetBlockByEpochNumber(ulong epochNumber);
}
