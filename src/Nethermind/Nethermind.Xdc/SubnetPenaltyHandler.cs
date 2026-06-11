// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Xdc.Spec;

namespace Nethermind.Xdc;

internal class SubnetPenaltyHandler(IBlockTree tree, ISpecProvider specProvider, IEpochSwitchManager epochSwitchManager, ISigningTxCache signingTxCache) : IPenaltyHandler
{
    private readonly EthereumEcdsa _ethereumEcdsa = new(specProvider.ChainId);

    public Address[] HandlePenalties(ulong number, Hash256 parentHash, Address[] candidates)
    {
        // Triggered only at gap blocks
        XdcSubnetBlockHeader header = tree.FindHeader(parentHash, number - 1) as XdcSubnetBlockHeader
            ?? throw new InvalidOperationException($"Header not found for block {number - 1}");
        IXdcReleaseSpec currentSpec = specProvider.GetXdcSpec(header);

        HashSet<Address> penalties = [];


        List<Hash256> listBlockHash = [];
        List<ulong> listBlockNumber = [];

        Dictionary<Address, int> minerStatistics = [];


        ulong parentNumber = number - 1;
        ulong minBlockNumber = Math.Max(1UL, number.SaturatingSub(currentSpec.EpochLength));

        while (true)
        {
            XdcSubnetBlockHeader parentHeader = tree.FindHeader(parentHash, parentNumber) as XdcSubnetBlockHeader
                ?? throw new InvalidOperationException($"Header not found for block {parentNumber}");

            if (parentNumber == minBlockNumber + 1)
            {
                foreach (Address penalty in parentHeader.PenaltiesAddress ?? [])
                {
                    penalties.Add(penalty);
                }
            }

            listBlockHash.Add(parentHash);
            listBlockNumber.Add(parentNumber);

            Address miner = parentHeader.Beneficiary ?? throw new InvalidOperationException($"Beneficiary is missing for block {parentHeader.Number}.");
            minerStatistics[miner] = minerStatistics.TryGetValue(miner, out int count) ? count + 1 : 1;

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
            parentHash = parentHeader.ParentHash ?? throw new InvalidOperationException($"Parent hash is missing for block {parentHeader.Number}.");
        }

        HashSet<Hash256> blockHashes = [];

        ulong startRange = number + 1 > currentSpec.RangeReturnSigner
            ? number - currentSpec.RangeReturnSigner + 1
            : 1UL;
        for (int i = listBlockNumber.Count - 1; i >= 0; i--)
        {
            ulong blockNumber = listBlockNumber[i];
            Hash256 blockHash = listBlockHash[i];

            if (blockNumber < startRange)
                continue;


            if (blockNumber % currentSpec.MergeSignRange == 0)
                blockHashes.Add(blockHash);

            Transaction[] signingTxs = signingTxCache.GetSigningTransactions(blockHash, blockNumber, currentSpec);
            foreach (Transaction tx in signingTxs)
            {
                Hash256 signedBlockHash = new(tx.Data.Span[^32..]);
                tx.SenderAddress ??= _ethereumEcdsa.RecoverAddress(tx);

                if (tx.SenderAddress is null)
                {
                    continue;
                }

                Address fromSigner = tx.SenderAddress;
                if (blockHashes.Contains(signedBlockHash))
                    penalties.Remove(fromSigner);
            }
        }
        // EIP-55 checksummed hex is required: lowercase byte comparison reorders
        // (e.g. "0xAb..." < "0xaa..." vs "0xab..." > "0xaa...").
        Address[] result = new Address[penalties.Count];
        penalties.CopyTo(result);
        result.AsSpan().Sort(default(AddressByEip55ChecksumOrdinalComparer));
        return result;
    }
}
