// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc.Modules;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.TraceStore.Ots;

/// <summary>A partial Otterscan API backed by stored traces, blocks, receipts and state.</summary>
[RpcModule(ModuleType.Ots)]
public interface IOtsRpcModule : IRpcModule
{
    /// <summary>Returns zero to identify partial OTS support, not compatibility with the full Otterscan API.</summary>
    [JsonRpcMethod(Description = "Returns 0: full Otterscan API compatibility is not advertised.", IsImplemented = true, IsSharable = true)]
    ResultWrapper<int> ots_getApiLevel();

    /// <summary>Checks for deployed code at an address in the selected block's state.</summary>
    [JsonRpcMethod(Description = "Checks for deployed code without returning the bytecode. Missing state is an error.", IsImplemented = true, IsSharable = false)]
    ResultWrapper<bool> ots_hasCode(Address address, BlockParameter block);

    /// <summary>Returns the raw revert bytes, or empty bytes for success and non-revert failures.</summary>
    [JsonRpcMethod(Description = "Returns a transaction's raw revert data from its stored trace.", IsImplemented = true, IsSharable = false)]
    ResultWrapper<byte[]> ots_getTransactionError(Hash256 transactionHash);

    /// <summary>Returns nested ETH transfers, creations and selfdestructs in execution order, including reverted operations.</summary>
    [JsonRpcMethod(Description = "Returns nested operations, excluding the root transaction and zero-value calls.", IsImplemented = true, IsSharable = false)]
    ResultWrapper<List<OtsInternalOperation>> ots_getInternalOperations(Hash256 transactionHash);

    /// <summary>Finds a canonical transaction by sender and nonce, returning null if none exists.</summary>
    [JsonRpcMethod(Description = "Scans canonical blocks for a sender and nonce. Missing block or sender data is an error.", IsImplemented = true, IsSharable = false, ResultCanBeNull = true)]
    ResultWrapper<Hash256?> ots_getTransactionBySenderAndNonce(Address sender,
        [JsonRpcParameter(ConverterType = typeof(ULongConverter))] ulong nonce);

    /// <summary>Returns the transaction and direct creator of an extant contract, or null for an address without code.</summary>
    [JsonRpcMethod(Description = "Finds the latest successful creation of a contract in canonical stored history.", IsImplemented = true, IsSharable = false, ResultCanBeNull = true)]
    ResultWrapper<OtsContractCreator?> ots_getContractCreator(Address address);

    /// <summary>Returns summarized block data, issuance and execution fees, or null for an unknown block.</summary>
    [JsonRpcMethod(Description = "Returns block details using stored receipts and reward traces. Missing history is an error.", IsImplemented = true, IsSharable = false, ResultCanBeNull = true)]
    ResultWrapper<OtsBlockDetails?> ots_getBlockDetails(BlockParameter block);

    /// <summary>Returns summarized block details selected by hash.</summary>
    [JsonRpcMethod(Description = "Returns block details by hash, including noncanonical blocks when their history is available.", IsImplemented = true, IsSharable = false, ResultCanBeNull = true)]
    ResultWrapper<OtsBlockDetails?> ots_getBlockDetailsByHash(Hash256 blockHash);

    /// <summary>Returns a page counted from the end of a block, in ascending transaction-index order.</summary>
    [JsonRpcMethod(Description = "Returns transactions with four-byte input selectors and receipts without logs or blooms.", IsImplemented = true, IsSharable = false, ResultCanBeNull = true)]
    ResultWrapper<OtsBlockTransactions?> ots_getBlockTransactions(BlockParameter block,
        [JsonRpcParameter(ConverterType = typeof(ByteConverter))] byte pageNumber,
        [JsonRpcParameter(ConverterType = typeof(ByteConverter))] byte pageSize);

    /// <summary>Searches before an exclusive block cursor; zero starts at the current head.</summary>
    [JsonRpcMethod(Description = "Searches stored call involvement backwards, returning whole blocks of matches in descending order. Missing traces are an error.", IsImplemented = true, IsSharable = false)]
    ResultWrapper<OtsSearchResult> ots_searchTransactionsBefore(Address address,
        [JsonRpcParameter(ConverterType = typeof(ULongConverter))] ulong blockNumber, int pageSize);

    /// <summary>Searches after an exclusive block cursor, returning the nearest page in descending order.</summary>
    [JsonRpcMethod(Description = "Searches stored call involvement forwards, returning whole blocks of matches in descending order. Missing traces are an error.", IsImplemented = true, IsSharable = false)]
    ResultWrapper<OtsSearchResult> ots_searchTransactionsAfter(Address address,
        [JsonRpcParameter(ConverterType = typeof(ULongConverter))] ulong blockNumber, int pageSize);

    /// <summary>Returns a transaction's stored call tree in depth-first OTS format.</summary>
    [JsonRpcMethod(Description = "Returns stored calls, creations and selfdestructs, excluding precompiles. Includes reverted calls; this is not a list of committed ETH transfers.", IsImplemented = true, IsSharable = false)]
    ResultWrapper<List<OtsTraceEntry>> ots_traceTransaction(Hash256 transactionHash);
}
