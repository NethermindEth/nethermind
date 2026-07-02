// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;
using Nethermind.Xdc.Contracts;

namespace Nethermind.Xdc.RPC;

internal sealed class XdcExtendedEthModule(
    IBlockFinder blockFinder,
    IReceiptFinder receiptFinder,
    ISpecProvider specProvider,
    IMasternodeVotingContract masternodeVotingContract,
    IRewardsStore rewardsStore) : IXdcExtendedEthRpcModule
{
    private static readonly IRlpDecoder<TxReceipt> ReceiptEncoder = Rlp.GetDecoder<TxReceipt>();

    public Task<ResultWrapper<Address>> eth_getOwnerByCoinbase(Address coinbase, BlockParameter? blockParameter = null)
    {
        SearchResult<BlockHeader> searchResult = blockFinder.SearchForHeader(blockParameter);
        if (searchResult.IsError)
        {
            return Task.FromResult(ResultWrapper<Address>.Fail(searchResult));
        }

        Address owner = masternodeVotingContract.GetCandidateOwner(searchResult.Object, coinbase);
        return Task.FromResult(ResultWrapper<Address>.Success(owner));
    }

    public Task<ResultWrapper<Dictionary<string, Dictionary<string, Dictionary<string, string>>>>> eth_getRewardByHash(
        Hash256 blockHash)
    {
        BlockHeader? header = blockFinder.FindHeader(blockHash);
        if (header is null)
        {
            return Task.FromResult(ResultWrapper<Dictionary<string, Dictionary<string, Dictionary<string, string>>>>.Success([]));
        }

        if (!rewardsStore.TryGetEpochRewards(header.Hash!, out Dictionary<string, Dictionary<string, Dictionary<string, string>>>? rewards)
            || rewards is null)
        {
            rewards = [];
        }

        return Task.FromResult(ResultWrapper<Dictionary<string, Dictionary<string, Dictionary<string, string>>>>.Success(rewards));
    }

    public Task<ResultWrapper<XdcTransactionAndReceiptProof?>> eth_getTransactionAndReceiptProof(Hash256 transactionHash)
    {
        Hash256? blockHash = receiptFinder.FindBlockHash(transactionHash);
        if (blockHash is null)
        {
            return Task.FromResult(ResultWrapper<XdcTransactionAndReceiptProof?>.Success(null));
        }

        Block? block = blockFinder.FindBlock(blockHash);
        if (block is null)
        {
            return Task.FromResult(ResultWrapper<XdcTransactionAndReceiptProof?>.Success(null));
        }

        Transaction[] transactions = block.Transactions;
        int index = -1;
        for (int i = 0; i < transactions.Length; i++)
        {
            if (transactions[i].Hash == transactionHash)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return Task.FromResult(ResultWrapper<XdcTransactionAndReceiptProof?>.Success(null));
        }

        TxReceipt[] receipts = receiptFinder.Get(block);
        if (index >= receipts.Length)
        {
            return Task.FromResult(ResultWrapper<XdcTransactionAndReceiptProof?>.Success(null));
        }

        IReleaseSpec spec = specProvider.GetSpec(block.Header);
        byte[][] txProof = TxTrie.CalculateProof(transactions, index);
        byte[][] receiptProof = ReceiptTrie.CalculateReceiptProofs(spec, receipts, index, ReceiptEncoder);
        (string[] txProofKeys, string[] txProofValues) = FromProofNodes(txProof);
        (string[] receiptProofKeys, string[] receiptProofValues) = FromProofNodes(receiptProof);

        XdcTransactionAndReceiptProof proof = new()
        {
            BlockHash = block.Hash ?? throw new InvalidOperationException($"Block returned by FindBlock has a null hash for block hash lookup {blockHash}"),
            TxRoot = TxTrie.CalculateRoot(transactions),
            ReceiptRoot = ReceiptTrie.CalculateRoot(spec, receipts, ReceiptEncoder),
            Key = Bytes.ToHexString(Rlp.Encode(index).Bytes, withZeroX: true),
            TxProofKeys = txProofKeys,
            TxProofValues = txProofValues,
            ReceiptProofKeys = receiptProofKeys,
            ReceiptProofValues = receiptProofValues,
        };

        return Task.FromResult(ResultWrapper<XdcTransactionAndReceiptProof?>.Success(proof));
    }

    private static (string[] Keys, string[] Values) FromProofNodes(byte[][] proofNodes)
    {
        string[] nodeHashes = new string[proofNodes.Length];
        string[] values = new string[proofNodes.Length];
        for (int i = 0; i < proofNodes.Length; i++)
        {
            byte[] rlp = proofNodes[i];
            nodeHashes[i] = Keccak.Compute(rlp).ToString();
            values[i] = Bytes.ToHexString(rlp, withZeroX: true);
        }

        return (nodeHashes, values);
    }
}
