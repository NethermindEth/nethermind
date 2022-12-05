// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Synchronization;

namespace Nethermind.Consensus.AuRa;

public class AuRaBetterPeerStrategy : IBetterPeerStrategy
{
    private readonly IBetterPeerStrategy _betterPeerStrategy;
    private readonly ILogger _logger;

    public AuRaBetterPeerStrategy(IBetterPeerStrategy betterPeerStrategy, ILogManager logManager)
    {
        _betterPeerStrategy = betterPeerStrategy;
        _logger = logManager.GetClassLogger();
    }

    public int Compare(in (UInt256 TotalDifficulty, long Number) valueX, in (UInt256 TotalDifficulty, long Number) valueY)
        => _betterPeerStrategy.Compare(valueX, valueY);

    public bool IsBetterThanLocalChain(in (UInt256 TotalDifficulty, long Number) bestPeerInfo, in (UInt256 TotalDifficulty, long Number) bestBlock) =>
        _betterPeerStrategy.IsBetterThanLocalChain(bestPeerInfo, bestBlock);

    public bool IsDesiredPeer(in (UInt256 TotalDifficulty, long Number) bestPeerInfo, in (UInt256 TotalDifficulty, long Number) bestHeader)
    {
        if (_betterPeerStrategy.IsDesiredPeer(bestPeerInfo, bestHeader))
        {
            // We really, really don't trust parity when its saying it has same block level with higher difficulty in AuRa, its lying most of the times in AuRa.
            // This is because if its different block we already imported, but on same level, it will have lower difficulty (AuRa specific).
            // If we imported the previous one than we probably shouldn't import this one.
            bool ignoreParitySameLevel = bestPeerInfo.Number == bestHeader.Number;

            // We can ignore reorg for one round, if we accepted previous block fine, this reorg is malicious
            if (ignoreParitySameLevel)
            {
                if (_logger.IsDebug) _logger.Debug($"Ignoring best peer [{bestPeerInfo.Number},{bestPeerInfo.TotalDifficulty}], possible Parity/OpenEthereum outlier.");
                return false;
            }

            return true;
        }

        return false;

    }

    public bool IsLowerThanTerminalTotalDifficulty(UInt256 totalDifficulty) =>
        _betterPeerStrategy.IsLowerThanTerminalTotalDifficulty(totalDifficulty);
}
