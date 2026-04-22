// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Xdc.Spec;

namespace Nethermind.Xdc;

internal class SubnetPenaltyHandler(IBlockTree tree, ISpecProvider specProvider, IEpochSwitchManager epochSwitchManager, ISigningTxCache signingTxCache) : IPenaltyHandler
{
    public Address[] HandlePenalties(long number, Hash256 parentHash, Address[] candidates)
    {
        // Triggered only at gap blocks
        XdcSubnetBlockHeader header = tree.FindHeader(parentHash, number - 1) as XdcSubnetBlockHeader
            ?? throw new InvalidOperationException($"Header not found for block {number - 1}");
        IXdcReleaseSpec currentSpec = specProvider.GetXdcSpec(header);

        HashSet<Address> penalties = new();


        List<Hash256> listBlockHash = [];
        List<long> listBlockNumber = [];

        Dictionary<Address, int> minerStatistics = new();


        long parentNumber = number - 1;
        long minBlockNumber = Math.Max(1, number - currentSpec.EpochLength);

        while (true)
        {
            XdcSubnetBlockHeader parentHeader = tree.FindHeader(parentHash, parentNumber) as XdcSubnetBlockHeader
                ?? throw new InvalidOperationException($"Header not found for block {parentNumber}");

            if (parentNumber == minBlockNumber + 1)
            {
                foreach (Address penalty in parentHeader.PenaltiesAddress)
                {
                    penalties.Add(penalty);
                }
            }

            listBlockHash.Add(parentHash);
            listBlockNumber.Add(parentNumber);

            Address miner = parentHeader.Beneficiary;
            minerStatistics[miner!] = minerStatistics.TryGetValue(miner, out int count) ? count + 1 : 1;

            bool isEpochSwitch = epochSwitchManager.IsEpochSwitchAtBlock(parentHeader);

            if (isEpochSwitch || parentNumber <= minBlockNumber)
            {
                Address[] masternodes = epochSwitchManager.GetEpochSwitchInfo(parentHeader)?.Masternodes ?? [];
                foreach (Address masternode in masternodes)
                {
                    if (minerStatistics.GetValueOrDefault(masternode, 0) < XdcConstants.MinimumMinerBlockPerEpoch)
                        penalties.Add(masternode);
                }
                minerStatistics.Clear();

                if (parentNumber <= minBlockNumber)
                    break;
            }

            parentNumber--;
            parentHash = parentHeader.ParentHash;
        }

        HashSet<Hash256> blockHashes = new();

        long startRange = Math.Max(number - (long)currentSpec.RangeReturnSigner + 1, 0);
        for (int i = listBlockNumber.Count - 1; i >= 0; i--)
        {
            long blockNumber = listBlockNumber[i];
            Hash256 blockHash = listBlockHash[i];

            if (blockNumber < startRange)
                continue;


            if (blockNumber % currentSpec.MergeSignRange == 0)
                blockHashes.Add(blockHash);

            Transaction[] signingTxs = signingTxCache.GetSigningTransactions(blockHash, blockNumber, currentSpec);
            foreach (Transaction tx in signingTxs)
            {
                Hash256 signedBlockHash = new(tx.Data.Span[^32..]);
                Address fromSigner = tx.SenderAddress;

                if (blockHashes.Contains(signedBlockHash))
                    penalties.Remove(fromSigner);
            }
        }
        // TODO Optimize
        // Must use EIP-55 checksummed hex to match XDC Go node ordering (addr.Hex()).
        // Plain lowercase comparison gives wrong order: e.g. "0xAb..." < "0xaa..." but "0xab..." > "0xaa..."
        Address[] result = new Address[penalties.Count];
        penalties.CopyTo(result);
        Array.Sort(result, (a, b) => string.CompareOrdinal(
            a.ToString(withEip55Checksum: true),
            b.ToString(withEip55Checksum: true)));
        return result;
    }
}
