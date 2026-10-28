// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc.Modules;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.TraceStore.Ots;

/// <summary>The account-history subset of the Otterscan API, backed by stored traces.</summary>
[RpcModule(ModuleType.Ots)]
public interface IOtsRpcModule : IRpcModule
{
    /// <summary>Returns zero to identify partial OTS support, not compatibility with the full Otterscan API.</summary>
    [JsonRpcMethod(Description = "Returns 0: only the account-history OTS subset is implemented.", IsImplemented = true, IsSharable = true)]
    ResultWrapper<int> ots_getApiLevel();

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
