// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc.Client;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Eez.Follower;

/// <summary>The sequencer reads the follower makes to take its latest blocks as the unsafe head.</summary>
public interface IEezSequencerApi
{
    Task<EezL1Block?> GetLatestBlock(CancellationToken token);

    Task<EezL1Block?> GetBlockByNumber(ulong number, CancellationToken token);

    /// <returns>The RLP of the block, or <see langword="null"/> when the sequencer does not serve it.</returns>
    Task<byte[]?> GetRawBlock(Hash256 hash, CancellationToken token);
}

/// <remarks>Does not own <paramref name="rpcClient"/>; the plugin manages its lifetime.</remarks>
public sealed class EezSequencerApi(IJsonRpcClient rpcClient) : IEezSequencerApi
{
    private const int TransactionReadConcurrency = 8;
    private bool _useStandardRpc;

    public Task<EezL1Block?> GetLatestBlock(CancellationToken token) => rpcClient.Post<EezL1Block?>("eth_getBlockByNumber", "latest", false).WaitAsync(token);

    public Task<EezL1Block?> GetBlockByNumber(ulong number, CancellationToken token) =>
        rpcClient.Post<EezL1Block?>("eth_getBlockByNumber", number.ToHexString(true), false).WaitAsync(token);

    public async Task<byte[]?> GetRawBlock(Hash256 hash, CancellationToken token)
    {
        if (!_useStandardRpc && await rpcClient.Post<byte[]?>("debug_getRawBlock", hash.ToString()).WaitAsync(token) is { } raw)
            return raw;
        SequencerRpcBlock? source = await rpcClient.Post<SequencerRpcBlock?>("eth_getBlockByHash", hash.ToString(), false).WaitAsync(token);
        if (source is null) return null;
        BlockHeader header = source.ToHeader(hash);
        Transaction[] transactions = source.Transactions!.Length == 0 ? [] : new Transaction[source.Transactions.Length];
        Task<Transaction>[] reads = transactions.Length == 0 ? [] : new Task<Transaction>[Math.Min(TransactionReadConcurrency, transactions.Length)];
        for (int first = 0; first < transactions.Length; first += TransactionReadConcurrency)
        {
            token.ThrowIfCancellationRequested();
            int count = Math.Min(TransactionReadConcurrency, transactions.Length - first);
            for (int index = 0; index < count; index++)
                reads[index] = ReadTransaction(source.Transactions[first + index], header.Number, token);
            Transaction[] decoded = await Task.WhenAll(reads.AsSpan(0, count));
            decoded.CopyTo(transactions, first);
        }
        Block block = new(header, new BlockBody(transactions, [], source.Withdrawals));
        byte[] result = Rlp.Encode(block).Bytes;
        _useStandardRpc = true;
        return result;
    }

    private async Task<Transaction> ReadTransaction(Hash256? hash, ulong number, CancellationToken token)
    {
        if (hash is null) throw new L1SourceIncompleteException(number, "The sequencer returned a transaction without a hash.");
        byte[] encoded = await rpcClient.Post<byte[]?>("eth_getRawTransactionByHash", hash.ToString()).WaitAsync(token)
            ?? throw new L1SourceIncompleteException(number, "The sequencer does not serve the transaction bytes.");
        if (Keccak.Compute(encoded) != hash)
            throw new L1SourceIncompleteException(number, "The sequencer returned different transaction bytes.");
        try
        {
            return Rlp.Decode<Transaction>(encoded, RlpBehaviors.SkipTypedWrapping)
                ?? throw new L1SourceIncompleteException(number, "The sequencer returned no transaction.");
        }
        catch (RlpException exception)
        {
            throw new L1SourceIncompleteException(number, $"The sequencer returned invalid transaction bytes: {exception.Message}");
        }
    }

}
