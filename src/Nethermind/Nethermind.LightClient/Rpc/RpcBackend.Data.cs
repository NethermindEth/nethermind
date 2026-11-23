// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.LightClient.Rpc;

internal sealed partial class RpcBackend
{
    private static readonly JsonSerializerOptions StorageKeyOptions = new() { Converters = { new RpcParameterConverters.StorageIndexParameterConverter() } };

    internal Task<JsonElement> GetBlockHeaderAsync(JsonElement selector, bool hashOnly, CancellationToken token) =>
        GetDataAsync(selector, hashOnly, DataKind.Header, false, UInt256.Zero, token);

    internal Task<JsonElement> GetBlockAccessListAsync(JsonElement selector, CancellationToken token) =>
        GetDataAsync(selector, false, DataKind.BlockAccessList, false, UInt256.Zero, token);

    internal Task<JsonElement> GetRawTransactionAsync(JsonElement selector, bool hashOnly, UInt256 index, CancellationToken token) =>
        GetDataAsync(selector, hashOnly, DataKind.RawTransaction, false, index, token);

    internal Task<JsonElement> GetRawTransactionByHashAsync(Hash256 hash, CancellationToken token) =>
        GetTransactionByHashAsync(hash, ExecutionData.TransactionResult.Raw, token);

    internal async Task<AccountForRpc?> GetAccountForRpcAsync(Address address, JsonElement selector, CancellationToken cancellationToken)
    {
        Account account = await GetAccountAsync(address, selector, cancellationToken);
        return ReferenceEquals(account, Account.TotallyEmpty) ? null : new AccountForRpc(account.ToStruct());
    }

    internal async Task<StorageValuesResult> GetStorageValuesAsync(JsonElement request, JsonElement selector, CancellationToken cancellationToken)
    {
        StorageValuesRequest requests = ParseStorageValuesRequest(request);
        if (selector.ValueKind != JsonValueKind.Undefined) _ = ParseBlockSelector(selector, false, out _, out _);
        VerifiedHead? latest = await LatestForSelectorAsync(selector, cancellationToken);
        using DataRequest admission = new(cancellationToken);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        CancellationToken token = deadline.Token;
        try
        {
            (VerifiedHead head, bool historical) = selector.ValueKind == JsonValueKind.Undefined
                ? (latest!, false)
                : await ResolveStateHeadAsync(selector, token, latest);
            try { return await GetStorageValuesAsync(requests, head, token); }
            finally { if (historical) _historicalStateRequests.Release(); }
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException or RlpReaderException)
        { throw new RpcException(-32000, "Execution peers returned an invalid or unavailable proof."); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new RpcException(-32000, "Verified storage values request exceeded the 90-second deadline."); }
    }

    private static StorageValuesRequest ParseStorageValuesRequest(JsonElement request)
    {
        if (request.ValueKind != JsonValueKind.Object || request.GetPropertyCount() > StorageValuesRequest.MaxSlots)
            throw InvalidParameters();
        foreach (JsonProperty entry in request.EnumerateObject())
        {
            string address = entry.Name;
            if (address.Length != Address.Size * 2 + 2 || !Address.IsValidAddress(address, allowPrefix: true)
                || entry.Value.ValueKind != JsonValueKind.Array)
                throw InvalidParameters();
        }

        StorageValuesRequest requests = new();
        try { requests.ReadJson(request, StorageKeyOptions); }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException)
        { throw InvalidParameters(); }
        if (requests.TooManySlots)
            throw new RpcException(-32602, $"too many slots (max {StorageValuesRequest.MaxSlots})");
        if (requests.TotalSlots == 0)
            throw new RpcException(-32602, "empty request");
        return requests;
    }

    private async Task<StorageValuesResult> GetStorageValuesAsync(StorageValuesRequest requests, VerifiedHead head, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(requests.TotalSlots * 32);
        Dictionary<Address, Memory<byte>[]> slots = [with(requests.Entries.Count)];
        try
        {
            int offset = 0;
            foreach ((Address address, UInt256[] keys) in requests.Entries)
            {
                Memory<byte>[] values = new Memory<byte>[keys.Length];
                slots.Add(address, values);
                for (int i = 0; i < keys.Length; i++, offset += 32) values[i] = buffer.AsMemory(offset, 32);
                if (keys.Length == 0) continue;
                Account account = await execution.GetAccountAsync(head, address, cancellationToken);
                if (keys.Length == 1)
                {
                    await FetchSlotAsync(0, cancellationToken);
                }
                else
                {
                    await Parallel.ForAsync(0, keys.Length,
                        new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = 3 },
                        FetchSlotAsync);
                }

                async ValueTask FetchSlotAsync(int index, CancellationToken token)
                {
                    UInt256 value = await execution.GetStorageAsync(head, address, account, keys[index], token);
                    value.ToBigEndian(values[index].Span);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new StorageValuesResult(buffer, slots);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    internal async Task<UInt256?> GetBaseFeeAsync(CancellationToken cancellationToken)
    {
        if (specProvider is null) throw new RpcException(-32601, "Verified execution fees are unavailable.");
        VerifiedHead latest = await LatestHeadAsync(cancellationToken);
        using DataRequest admission = new(cancellationToken);
        BlockHeader header = await execution.GetHeaderAsync(latest, cancellationToken);
        IEip1559Spec next = specProvider.GetSpecFor1559(checked(header.Number + 1));
        return next.IsEip1559Enabled ? BaseFeeCalculator.Calculate(header, next) : null;
    }

    internal async Task<JsonNode> GetConfigAsync(CancellationToken cancellationToken)
    {
        if (specProvider is null || forkInfo is null) throw new RpcException(-32601, "Execution fork configuration is unavailable.");
        VerifiedHead head = getLatestHead?.Invoke() ?? getHead();
        using DataRequest admission = new(cancellationToken);
        BlockHeader header = await execution.GetHeaderAsync(head, cancellationToken);
        return ForkConfigFactory.CreateSummary(forkInfo.GetForkActivationsSummary(header), specProvider);
    }

    internal EthCapabilities GetCapabilities()
    {
        VerifiedHead finalized = getHead();
        VerifiedHead latest = getLatestHead?.Invoke() is { } optimistic && optimistic.Number >= finalized.Number ? optimistic : finalized;
        // The verification window is a routing limit. Peers can prune data inside it.
        ResourceAvailability state = new(false);
        ResourceAvailability executionData = new(_data is null);
        return new(new ChainHead(latest.Number, latest.BlockHash), state, executionData, executionData, state);
    }
}
