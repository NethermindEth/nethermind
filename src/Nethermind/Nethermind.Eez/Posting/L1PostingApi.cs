// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Data;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc.Client;
using Nethermind.Serialization.Json;

namespace Nethermind.Eez.Posting;

/// <summary>A JSON-RPC answer with its error kept, since whether a submission may be retried depends on it.</summary>
public readonly record struct RpcAnswer<T>(T? Result, int? ErrorCode, string? ErrorMessage)
{
    public bool IsError => ErrorCode is not null;
}

public readonly struct EezL1Receipt
{
    public Hash256 BlockHash { get; init; }
    public ulong BlockNumber { get; init; }
    public ulong Status { get; init; }
}

/// <summary>The L1 calls that post a batch: the poster's nonce, the submission, and what became of it.</summary>
public interface IL1PostingApi
{
    Task<ulong?> GetPendingNonce(Address sender, CancellationToken token);

    Task<RpcAnswer<Hash256>> SendRawTransaction(byte[] transaction, CancellationToken token);

    /// <summary>Sends the transactions as one bundle to the builder, to land together in <paramref name="block"/> or not at all.</summary>
    Task<RpcAnswer<object>> SendBundle(IReadOnlyList<byte[]> transactions, ulong block, BundleTarget target, CancellationToken token);

    Task<EezL1Receipt?> GetReceipt(Hash256 transaction, CancellationToken token);
}

/// <remarks>Does not own the clients; the plugin manages their lifetimes.</remarks>
public sealed class L1PostingApi(IJsonRpcClient l1, IJsonRpcClient builder, IJsonSerializer serializer) : IL1PostingApi
{
    public Task<ulong?> GetPendingNonce(Address sender, CancellationToken token) =>
        l1.Post<ulong?>("eth_getTransactionCount", sender, "pending").WaitAsync(token);

    public Task<RpcAnswer<Hash256>> SendRawTransaction(byte[] transaction, CancellationToken token) =>
        Answer<Hash256>(l1, "eth_sendRawTransaction", token, transaction.ToHexString(true));

    public Task<RpcAnswer<object>> SendBundle(IReadOnlyList<byte[]> transactions, ulong block, BundleTarget target, CancellationToken token) =>
        Answer<object>(builder, "eth_sendBundle", token, BundleRequest.Create(transactions, block, target));

    public Task<EezL1Receipt?> GetReceipt(Hash256 transaction, CancellationToken token) =>
        l1.Post<EezL1Receipt?>("eth_getTransactionReceipt", transaction).WaitAsync(token);

    private async Task<RpcAnswer<T>> Answer<T>(IJsonRpcClient client, string method, CancellationToken token, params object?[] parameters)
    {
        string response = await client.Post(method, parameters).WaitAsync(token) ?? throw new DataException($"{method} returned nothing.");
        JsonRpcResponse<T> answer = serializer.Deserialize<JsonRpcResponse<T>>(response) ?? throw new DataException($"{method} returned no JSON-RPC response.");
        return answer.Error is { } error ? new RpcAnswer<T>(default, error.Code, error.Message) : new RpcAnswer<T>(answer.Result, null, null);
    }
}

/// <summary>The <c>eth_sendBundle</c> parameter.</summary>
/// <remarks>
/// The builder reads the timestamp bounds as JSON numbers; as hex strings they are accepted and the bundle is then never
/// included, so they are written raw.
/// </remarks>
public sealed record BundleRequest(
    [property: JsonPropertyName("txs")] string[] Transactions,
    [property: JsonPropertyName("blockNumber")] string BlockNumber,
    [property: JsonPropertyName("minTimestamp"), JsonConverter(typeof(NullableRawULongConverter)), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ulong? MinTimestamp,
    [property: JsonPropertyName("maxTimestamp"), JsonConverter(typeof(NullableRawULongConverter)), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ulong? MaxTimestamp)
{
    /// <summary>A bundle for <paramref name="block"/>, pinned to the target's timestamp when the target has one.</summary>
    public static BundleRequest Create(IReadOnlyList<byte[]> transactions, ulong block, BundleTarget target)
    {
        string[] encoded = new string[transactions.Count];
        for (int i = 0; i < encoded.Length; i++)
        {
            encoded[i] = transactions[i].ToHexString(true);
        }

        ulong? pin = target.IsPinned ? target.Timestamp : null;
        return new BundleRequest(encoded, block.ToHexString(true), pin, pin);
    }
}
