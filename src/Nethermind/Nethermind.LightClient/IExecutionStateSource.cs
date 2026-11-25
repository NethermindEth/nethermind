// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.LightClient;

internal interface IExecutionStateSource
{
    /// <summary>Returns the authenticated account, or the <see cref="Account.TotallyEmpty"/> instance for a proven absence.</summary>
    Task<Account> GetAccountAsync(VerifiedHead head, Address address, CancellationToken cancellationToken);
    Task<UInt256> GetStorageAsync(VerifiedHead head, Address address, Account account, UInt256 key, CancellationToken cancellationToken);
    Task<byte[]> GetCodeAsync(Account account, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<Hash256, byte[]>> GetStorageTrieNodesAsync(VerifiedHead head, Address address,
        StorageTrieNodeRequest[] requests, CancellationToken cancellationToken) => throw new NotSupportedException();
    Task<BlockHeader> GetHeaderByHashAsync(Hash256 hash, CancellationToken cancellationToken) => throw new NotSupportedException();
    Task<BlockHeader> GetHeaderAsync(VerifiedHead head, CancellationToken cancellationToken);
    Task<ReadOnlyBlockAccessList> GetBlockAccessListAsync(BlockHeader header, CancellationToken cancellationToken) => throw new NotSupportedException();
    Task<Hash256[]> GetAncestorHashesAsync(VerifiedHead head, ulong firstNumber, CancellationToken cancellationToken);
    async Task<BlockHeader[]> GetRecentHeadersAsync(BlockHeader newest, int count, CancellationToken cancellationToken)
    {
        BlockHeader[] headers = new BlockHeader[count];
        headers[^1] = newest;
        if (count == 1) return headers;
        VerifiedHead anchor = new(0, newest.Number, newest.Hash!, newest.StateRoot!);
        ulong first = newest.Number - (ulong)count + 1;
        Hash256[] hashes = await GetAncestorHashesAsync(anchor, first, cancellationToken);
        if (hashes.Length != count || hashes[^1] != newest.Hash) throw new InvalidDataException("Invalid authenticated ancestor chain.");
        for (int i = 0; i < count - 1; i++)
        {
            headers[i] = await GetHeaderByHashAsync(hashes[i], cancellationToken);
            if (headers[i].Hash != hashes[i] || headers[i].Number != first + (ulong)i)
                throw new InvalidDataException("An ancestor header differs from the authenticated chain.");
        }
        return headers;
    }
    Task<BlockHeader> GetCanonicalHeaderAsync(VerifiedHead head, ulong number, CancellationToken cancellationToken) => throw new NotSupportedException();
    Task<BlockHeader[]> GetLogHeadersAsync(VerifiedHead anchor, VerifiedHead finalized, ulong firstNumber,
        ulong lastNumber, CancellationToken cancellationToken) =>
        LogHeaderChain.GetAsync(this, anchor, finalized, firstNumber, lastNumber, cancellationToken);
    Task<Block> GetBlockAsync(BlockHeader header, CancellationToken cancellationToken) => throw new NotSupportedException();
    /// <summary>Authenticates the complete receipt list, using the exact transaction count when known.</summary>
    Task<TxReceipt[]> GetReceiptsAsync(BlockHeader header, CancellationToken cancellationToken, int? transactionCount = null) => throw new NotSupportedException();
    /// <summary>Starts a bounded receipt window, retaining a separate wait for each block.</summary>
    Task<TxReceipt[]>[] GetReceiptsAsync(BlockHeader[] headers, CancellationToken cancellationToken)
    {
        Task<TxReceipt[]>[] result = new Task<TxReceipt[]>[headers.Length];
        for (int i = 0; i < result.Length; i++) result[i] = FetchAsync(headers[i]);
        return result;

        async Task<TxReceipt[]> FetchAsync(BlockHeader header) => await GetReceiptsAsync(header, cancellationToken);
    }
    Task BroadcastTransactionAsync(Transaction transaction, CancellationToken cancellationToken) => throw new NotSupportedException();
    Account GetPendingAccount(Address address, Account account, ulong nonceLimit = ulong.MaxValue) => account;
    Task<ExecutionAccountProof> GetProofAsync(VerifiedHead head, Address address, UInt256[] storageKeys,
        CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed record ExecutionAccountProof(Account Account, byte[][] AccountProof, ExecutionStorageProof[] StorageProofs);
internal sealed record ExecutionStorageProof(UInt256 Key, UInt256 Value, byte[][] Proof);
internal readonly record struct StorageTrieNodeRequest(TreePath Path, Hash256 Hash);

internal sealed class ExecutionDataUnavailableException(string message) : IOException(message);
