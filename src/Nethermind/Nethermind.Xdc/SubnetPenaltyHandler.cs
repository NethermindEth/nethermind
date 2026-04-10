// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Spec;
using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript;
using Nethermind.Core.Collections;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc;

internal class SubnetPenaltyHandler(IBlockTree tree, ISpecProvider specProvider, IEpochSwitchManager epochSwitchManager, ISigningTxCache signingTxCache) : IPenaltyHandler
{
    private static readonly EthereumEcdsa _ethereumEcdsa = new(0);

    public Address[] HandlePenalties(long number, Hash256 parentHash, Address[] candidates)
    {
        XdcSubnetBlockHeader header = (XdcSubnetBlockHeader)tree.FindHeader(parentHash, number - 1);
        IXdcReleaseSpec currentSpec = specProvider.GetXdcSpec(header!);

        Hash256 currentHash = parentHash;

        List<Address> penalties = [];
        List<Address> prevPenalties = [];


        List<Hash256> listBlockHash = [];
        List<long> listBlockNumber = [];

        Dictionary<Address, int> minerStatistics = new();


        long parentNumber = number - 1;
        long minBlockNumber = Math.Max(1, number - currentSpec.EpochLength);

        while (true)
        {
            XdcSubnetBlockHeader parentHeader = (XdcSubnetBlockHeader)tree.FindHeader(parentHash, parentNumber);

            if (parentNumber == minBlockNumber + 1)
                prevPenalties = parentHeader.PenaltiesAddress.Value ?? [];

            listBlockHash.Add(parentHash);
            listBlockNumber.Add(parentNumber);

            Address miner = parentHeader.Beneficiary ?? _ethereumEcdsa.RecoverAddress(new Signature(parentHeader.Validator.AsSpan(0, 64), parentHeader.Validator[64]), Keccak.Compute(_xdcHeaderDecoder.Encode(parentHeader, RlpBehaviors.ForSealing).Bytes));
            minerStatistics[miner!] = minerStatistics.TryGetValue(miner, out int count) ? count + 1 : 1;

            bool isEpochSwitch = epochSwitchManager.IsEpochSwitchAtBlock(parentHeader);

            if (isEpochSwitch || parentNumber <= minBlockNumber)
            {
                Address[] masternodes = epochSwitchManager.GetEpochSwitchInfo(parentHeader)?.Masternodes ?? [];
                foreach (Address masternode in masternodes)
                {
                    if(minerStatistics.GetValueOrDefault(masternode, 0) <= XdcConstants.MinimumMinerBlockPerEpoch)
                        penalties.Add(masternode);
                }
                minerStatistics.Clear();

                if(parentNumber <= minBlockNumber)
                    break;
            }

            parentNumber--;
            parentHash = parentHeader.ParentHash;
        }

        penalties.AddRange(prevPenalties);

        HashSet<Address> comebacks = new();
        HashSet<Hash256> blockHashes = new();

        long startRange = Math.Max(number - (long)currentSpec.RangeReturnSigner + 1, 0);
        for (int i = listBlockNumber.Count - 1; i >= 0; i--)
        {
            long blockNumber =  listBlockNumber[i];
            Hash256 blockHash = listBlockHash[i];

            if(blockNumber < startRange)
                continue;


            if (blockNumber % currentSpec.MergeSignRange == 0)
                blockHashes.Add(blockHash);

            Transaction[] signingTxs = signingTxCache.GetSigningTransactions(blockHash, blockNumber, currentSpec);
            foreach (Transaction tx in signingTxs)
            {
                var signedBlockHash = new Hash256(tx.Data.Span[^32..]);
                Address fromSigner = tx.SenderAddress;

                if (blockHashes.Contains(signedBlockHash))
                    comebacks.Add(fromSigner);
            }
        }

        HashSet<Address> finalPenaltiesSet = new();
        List<Address> finalPenalties = [];


        foreach (Address penalty in penalties)
        {
            if(finalPenaltiesSet.Contains(penalty))
                continue;
            if(comebacks.Contains(penalty))
                continue;
            finalPenaltiesSet.Add(penalty);
            finalPenalties.Add(penalty);
        }

        // TODO use unstable one like in xdc
        finalPenalties.Sort();

        return finalPenalties.ToArray();
    }
}
