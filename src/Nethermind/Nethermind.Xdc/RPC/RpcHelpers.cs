// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Nethermind.Xdc.RPC;

internal static class RpcHelpers
{
    public static PublicApiSnapshot BuildRpcSnapshot(this Snapshot snapshot, XdcBlockHeader header)
    {
        return new PublicApiSnapshot
        {
            Number = (ulong)header.Number,
            Hash = header.Hash,
            Signers = snapshot.NextEpochCandidates.ToHashSet(),
        };
    }

    public static Address[] GetSigners(this Snapshot snapshot)
    {
        throw new NotImplementedException();
    }

    public static PublicApiMissedRoundsMetadata CalculateMissingRounds(
        this XdcBlockHeader header,
        IBlockTree blockTree,
        IEpochSwitchManager epochSwitchManager,
        ISpecProvider specProvider)
    {
        List<MissedRoundInfo> missedRounds = new List<MissedRoundInfo>();

        EpochSwitchInfo? switchInfo = epochSwitchManager.GetEpochSwitchInfo(header);
        if (switchInfo == null)
        {
            throw new InvalidOperationException($"Cannot get epoch switch info for block {header.Number}, hash {header.Hash}");
        }

        Address[] masternodes = switchInfo.Masternodes;
        if (masternodes == null || masternodes.Length == 0)
        {
            throw new InvalidOperationException($"masternodes is empty in CalculateMissingRounds, number = {header.Number}, hash {header.Hash}");
        }

        var spec = specProvider.GetXdcSpec(header);

        // Loop through from the epoch switch block to the current "header" block
        XdcBlockHeader nextHeader = header;
        while (nextHeader.Number > switchInfo.EpochSwitchBlockInfo.BlockNumber)
        {
            BlockHeader? parentHeaderBase = blockTree.FindHeader(nextHeader.ParentHash);
            if (parentHeaderBase == null)
            {
                throw new InvalidOperationException($"fail to get header by hash {nextHeader.ParentHash}");
            }

            XdcBlockHeader parentHeader = parentHeaderBase as XdcBlockHeader 
                ?? throw new InvalidOperationException($"Parent header is not XdcBlockHeader");

            if (parentHeader.ExtraConsensusData == null || nextHeader.ExtraConsensusData == null)
            {
                throw new InvalidOperationException("ExtraConsensusData is null");
            }

            ulong parentRound = parentHeader.ExtraConsensusData.BlockRound;
            ulong currRound = nextHeader.ExtraConsensusData.BlockRound;

            // This indicates that an increment in the round number is missing during the block production process.
            if (parentRound + 1 != currRound)
            {
                // We need to iterate from the parentRound to the currRound to determine which miner did not perform mining.
                for (ulong i = parentRound + 1; i < currRound; i++)
                {
                    ulong leaderIndex = i % (ulong)spec.EpochLength % (ulong)masternodes.Length;
                    Address whosTurn = masternodes[leaderIndex];
                    
                    missedRounds.Add(new MissedRoundInfo
                    {
                        Round = i,
                        Miner = whosTurn,
                        CurrentBlockHash = nextHeader.Hash,
                        CurrentBlockNum = (UInt256)nextHeader.Number,
                        ParentBlockHash = parentHeader.Hash,
                        ParentBlockNum = (UInt256)parentHeader.Number
                    });
                }
            }

            // Assign the pointer to the next one
            nextHeader = parentHeader;
        }

        PublicApiMissedRoundsMetadata missedRoundsMetadata = new PublicApiMissedRoundsMetadata
        {
            EpochRound = switchInfo.EpochSwitchBlockInfo.Round,
            EpochBlockNumber = (UInt256)switchInfo.EpochSwitchBlockInfo.BlockNumber,
            MissedRounds = missedRounds.ToArray()
        };

        return missedRoundsMetadata;
    }
}
