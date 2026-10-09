// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.LightClient;

internal sealed class VerifiedRpc(IExecutionStateSource execution, Func<VerifiedHead> getHead, ulong chainId,
    VerifiedCall? calls = null, ISpecProvider? specProvider = null, Func<VerifiedHead?>? getLatestHead = null)
{
    private const ulong HistoricalStateDepth = 32;
    private readonly VerifiedExecutionData? _data = specProvider is null ? null : new(execution, specProvider, chainId);
    private readonly SemaphoreSlim _historicalStateRequests = new(2, 2);

    internal async Task<object> InvokeAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        int count = parameters.ValueKind switch
        {
            JsonValueKind.Undefined => 0,
            JsonValueKind.Array => parameters.GetArrayLength(),
            _ => throw InvalidParameters()
        };
        if (method is "eth_chainId" or "eth_blockNumber")
        {
            if (count != 0) throw InvalidParameters();
            return method == "eth_chainId" ? Quantity(chainId) : Quantity(LatestHead().Number);
        }
        if (method is "eth_getBlockByNumber" or "eth_getBlockByHash" or "eth_getBlockTransactionCountByNumber"
            or "eth_getBlockTransactionCountByHash" or "eth_getTransactionByBlockNumberAndIndex"
            or "eth_getTransactionByBlockHashAndIndex" or "eth_getBlockReceipts" or "eth_getLogs")
            return await InvokeExecutionDataAsync(method, parameters, count, cancellationToken);
        if (method is "eth_call" or "eth_estimateGas")
        {
            if (count is < 1 or > 2 || parameters[0].ValueKind != JsonValueKind.Object) throw InvalidParameters();
            if (calls is null) throw new RpcException(-32601, "Method is not supported by the verified RPC.");
            (VerifiedHead callHead, bool historical) = count == 1
                ? (LatestHead(), false) : await ResolveStateHeadAsync(parameters[1], cancellationToken);
            try { return await calls.ExecuteAsync(callHead, parameters[0], method == "eth_estimateGas", cancellationToken); }
            finally { if (historical) _historicalStateRequests.Release(); }
        }
        if (method is not ("eth_getBalance" or "eth_getTransactionCount" or "eth_getCode" or "eth_getStorageAt"))
            throw new RpcException(-32601, "Method is not supported by the verified RPC.");

        bool storage = method == "eth_getStorageAt";
        if (count != (storage ? 3 : 2)) throw InvalidParameters();
        JsonElement addressParameter = parameters[0];
        if (addressParameter.ValueKind != JsonValueKind.String) throw InvalidParameters();
        string addressText = addressParameter.GetString()!;
        if (addressText.Length != 42 || !Address.IsValidAddress(addressText, allowPrefix: true)) throw InvalidParameters();
        Address address = new(addressText);
        UInt256 key = storage ? ParseQuantity(parameters[1]) : UInt256.Zero;
        (VerifiedHead head, bool historicalState) = await ResolveStateHeadAsync(parameters[storage ? 2 : 1], cancellationToken);
        try
        {
            Account account = await execution.GetAccountAsync(head, address, cancellationToken);
            if (method == "eth_getBalance") return Quantity(account.Balance);
            if (method == "eth_getTransactionCount") return Quantity(account.Nonce);
            if (storage)
            {
                UInt256 value = await execution.GetStorageAsync(head, address, account, key, cancellationToken);
                return value.ToBigEndian().ToHexString(withZeroX: true);
            }
            byte[] code = await execution.GetCodeAsync(account, cancellationToken);
            return code.ToHexString(withZeroX: true);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException or RlpException)
        {
            throw new RpcException(-32000, "Execution peers returned an invalid or unavailable proof.");
        }
        finally { if (historicalState) _historicalStateRequests.Release(); }
    }

    private async Task<JsonElement> InvokeExecutionDataAsync(string method, JsonElement parameters, int count, CancellationToken cancellationToken)
    {
        if (_data is null) throw new RpcException(-32601, "Verified execution data is unavailable.");
        CancellationToken callerCancellation = cancellationToken;
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        cancellationToken = deadline.Token;
        try
        {
            if (method == "eth_getLogs")
            {
                if (count != 1 || parameters[0].ValueKind != JsonValueKind.Object) throw InvalidParameters();
                return await GetLogsAsync(parameters[0], cancellationToken);
            }

            bool blockByHash = method is "eth_getBlockByHash" or "eth_getBlockTransactionCountByHash"
                or "eth_getTransactionByBlockHashAndIndex";
            int expected = method is "eth_getBlockByHash" or "eth_getBlockByNumber"
                or "eth_getTransactionByBlockHashAndIndex" or "eth_getTransactionByBlockNumberAndIndex" ? 2 : 1;
            if (count != expected) throw InvalidParameters();
            BlockHeader header = await ResolveDataHeaderAsync(parameters[0], blockByHash, cancellationToken);
            if (method is "eth_getBlockByHash" or "eth_getBlockByNumber")
            {
                if (parameters[1].ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw InvalidParameters();
                return await _data.GetBlockAsync(header, parameters[1].GetBoolean(), cancellationToken);
            }
            if (method is "eth_getBlockTransactionCountByHash" or "eth_getBlockTransactionCountByNumber")
                return await _data.GetBlockTransactionCountAsync(header, cancellationToken);
            if (method == "eth_getBlockReceipts")
                return await _data.GetBlockReceiptsAsync(header, cancellationToken);

            UInt256 index = ParseQuantity(parameters[1]);
            if (index > int.MaxValue) return JsonSerializer.SerializeToElement<object?>(null);
            return await _data.GetTransactionAsync(header, (int)index, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or RlpException or OverflowException)
        {
            throw new RpcException(-32000, "Execution peers returned invalid or unavailable authenticated block data.");
        }
        catch (OperationCanceledException) when (!callerCancellation.IsCancellationRequested)
        {
            throw new RpcException(-32000, "Verified execution data request exceeded the 90-second deadline.");
        }
    }

    private async Task<BlockHeader> ResolveDataHeaderAsync(JsonElement selector, bool hashOnly,
        CancellationToken cancellationToken, VerifiedHead? pinnedFinalized = null, bool stateHistory = false)
    {
        if (selector.ValueKind != JsonValueKind.String) throw InvalidParameters();
        string value = selector.GetString()!;
        if (!hashOnly && value == "latest") return await execution.GetHeaderAsync(LatestHead(), cancellationToken);
        if (hashOnly || value.Length == 66)
        {
            Hash256 requested = new(ReadParameterData(selector, 32));
            if (getLatestHead?.Invoke() is { } latest && requested == latest.BlockHash)
                return await execution.GetHeaderAsync(latest, cancellationToken);
            VerifiedHead finalizedHead = pinnedFinalized ?? getHead();
            if (requested == finalizedHead.BlockHash) return await execution.GetHeaderAsync(finalizedHead, cancellationToken);
            BlockHeader candidate = await execution.GetHeaderByHashAsync(requested, cancellationToken);
            if (stateHistory) ValidateHistoricalStateNumber(finalizedHead, candidate.Number);
            else if (candidate.Number > finalizedHead.Number || finalizedHead.Number - candidate.Number > 256)
                throw new RpcException(-32001, "Block is outside the verified finalized history window.");
            BlockHeader canonical = await execution.GetCanonicalHeaderAsync(finalizedHead, candidate.Number, cancellationToken);
            if (canonical.Hash != requested)
                throw new RpcException(-32001, "Block is not in the verified finalized chain.");
            return candidate;
        }
        if (value == "finalized") return await execution.GetHeaderAsync(pinnedFinalized ?? getHead(), cancellationToken);
        UInt256 parsed = ParseQuantity(selector);
        if (getLatestHead?.Invoke() is { } current && parsed == (UInt256)current.Number)
            return await execution.GetHeaderAsync(current, cancellationToken);
        VerifiedHead head = pinnedFinalized ?? getHead();
        if (parsed > (UInt256)head.Number || parsed > ulong.MaxValue)
            throw new RpcException(-32001, "Block is newer than the verified finalized head.");
        ulong number = (ulong)parsed;
        if (head.Number - number > 256)
            throw new RpcException(-32001, "Block is outside the verified finalized history window.");
        return await execution.GetCanonicalHeaderAsync(head, number, cancellationToken);
    }

    private async Task<JsonElement> GetLogsAsync(JsonElement filter, CancellationToken cancellationToken)
    {
        HashSet<Address>? addresses = null;
        List<HashSet<Hash256>?> topics = [];
        JsonElement blockHash = default;
        JsonElement from = default;
        JsonElement to = default;
        foreach (JsonProperty property in filter.EnumerateObject())
        {
            switch (property.Name)
            {
                case "blockHash": blockHash = property.Value; break;
                case "fromBlock": from = property.Value; break;
                case "toBlock": to = property.Value; break;
                case "address": addresses = property.Value.ValueKind == JsonValueKind.Null ? null : ParseAddresses(property.Value); break;
                case "topics": topics = property.Value.ValueKind == JsonValueKind.Null ? [] : ParseTopics(property.Value); break;
                default: throw InvalidParameters();
            }
        }
        if (blockHash.ValueKind != JsonValueKind.Undefined &&
            (from.ValueKind != JsonValueKind.Undefined || to.ValueKind != JsonValueKind.Undefined)) throw InvalidParameters();
        if (blockHash.ValueKind != JsonValueKind.Undefined)
        {
            BlockHeader one = await ResolveDataHeaderAsync(blockHash, hashOnly: true, cancellationToken);
            return await _data!.GetLogsAsync([one], addresses, topics, cancellationToken);
        }
        bool latestFrom = from.ValueKind == JsonValueKind.Undefined || from.ValueKind == JsonValueKind.String && from.GetString() == "latest";
        bool latestTo = to.ValueKind == JsonValueKind.Undefined || to.ValueKind == JsonValueKind.String && to.GetString() == "latest";
        VerifiedHead? current = getLatestHead?.Invoke();
        bool optimisticFrom = latestFrom || current is not null && from.ValueKind == JsonValueKind.String
            && from.GetString()?.StartsWith("0x", StringComparison.Ordinal) == true
            && ParseQuantity(from) == (UInt256)current.Number;
        bool optimisticTo = latestTo || current is not null && to.ValueKind == JsonValueKind.String
            && to.GetString()?.StartsWith("0x", StringComparison.Ordinal) == true
            && ParseQuantity(to) == (UInt256)current.Number;
        if (optimisticFrom || optimisticTo)
        {
            if (!optimisticFrom || !optimisticTo)
                throw new RpcException(-32001, "Log ranges mixing latest and finalized history are unavailable.");
            BlockHeader latest = await execution.GetHeaderAsync(current ?? LatestHead(), cancellationToken);
            return await _data!.GetLogsAsync([latest], addresses, topics, cancellationToken);
        }
        VerifiedHead head = getHead();
        ulong first = ParseLogBlock(from, head);
        ulong last = ParseLogBlock(to, head);
        if (first > last) return JsonSerializer.SerializeToElement(Array.Empty<object>());
        if (last - first >= 16)
            throw new RpcException(-32001, "Verified log queries are limited to 16 finalized blocks.");
        BlockHeader[] headers = new BlockHeader[checked((int)(last - first + 1))];
        Hash256[] hashes = first == head.Number ? [head.BlockHash]
            : await execution.GetAncestorHashesAsync(head, first, cancellationToken);
        for (int i = 0; i < headers.Length; i++)
        {
            ulong number = first + (ulong)i;
            BlockHeader header = number == head.Number
                ? await execution.GetHeaderAsync(head, cancellationToken)
                : await execution.GetHeaderByHashAsync(hashes[i], cancellationToken);
            if (header.Number != number || header.Hash != hashes[i])
                throw new RpcException(-32000, "Execution peers returned a header outside the verified finalized chain.");
            headers[i] = header;
        }
        return await _data!.GetLogsAsync(headers, addresses, topics, cancellationToken);
    }

    private static ulong ParseLogBlock(JsonElement selector, VerifiedHead head)
    {
        if (selector.ValueKind == JsonValueKind.String && selector.GetString() == "finalized") return head.Number;
        UInt256 number = ParseQuantity(selector);
        if (number > (UInt256)head.Number || number > ulong.MaxValue || head.Number - (ulong)number > 256)
            throw new RpcException(-32001, "Block is outside the verified finalized history window.");
        return (ulong)number;
    }

    private static HashSet<Address> ParseAddresses(JsonElement value)
    {
        HashSet<Address> result = [];
        if (value.ValueKind == JsonValueKind.String) Add(value);
        else if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= 16)
            foreach (JsonElement item in value.EnumerateArray()) Add(item);
        else throw InvalidParameters();
        return result;

        void Add(JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } text
                || text.Length != 42 || !Address.IsValidAddress(text, allowPrefix: true)) throw InvalidParameters();
            result.Add(new Address(text));
        }
    }

    private static List<HashSet<Hash256>?> ParseTopics(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 4) throw InvalidParameters();
        List<HashSet<Hash256>?> result = [];
        foreach (JsonElement slot in value.EnumerateArray())
        {
            if (slot.ValueKind == JsonValueKind.Null) { result.Add(null); continue; }
            HashSet<Hash256> choices = [];
            bool wildcard = false;
            if (slot.ValueKind == JsonValueKind.String) choices.Add(new Hash256(ReadParameterData(slot, 32)));
            else if (slot.ValueKind == JsonValueKind.Array && slot.GetArrayLength() <= 64)
            {
                foreach (JsonElement alternative in slot.EnumerateArray())
                {
                    if (alternative.ValueKind == JsonValueKind.Null) { wildcard = true; break; }
                    choices.Add(new Hash256(ReadParameterData(alternative, 32)));
                }
            }
            else throw InvalidParameters();
            result.Add(wildcard ? null : choices);
        }
        return result;
    }

    private VerifiedHead LatestHead() => getLatestHead?.Invoke()
        ?? throw new RpcException(-32001, "An authenticated optimistic execution head is not available.");

    private async Task<(VerifiedHead Head, bool Historical)> ResolveStateHeadAsync(JsonElement block, CancellationToken cancellationToken)
    {
        if (block.ValueKind == JsonValueKind.String)
        {
            string selector = block.GetString()!;
            if (selector == "latest") return (LatestHead(), false);
            if (selector.Length == 66)
            {
                Hash256 requested = new(ReadParameterData(block, 32));
                if (getLatestHead?.Invoke() is { } optimistic && requested == optimistic.BlockHash)
                    return (optimistic, false);
                VerifiedHead hashAnchor = getHead();
                if (requested == hashAnchor.BlockHash) return (hashAnchor, false);
                return await HistoricalStateHashAsync(hashAnchor, block, cancellationToken);
            }
            if (selector == "finalized") return (getHead(), false);
            UInt256 number = ParseQuantity(block);
            if (getLatestHead?.Invoke() is { } latest && number == (UInt256)latest.Number)
                return (latest, false);
            VerifiedHead finalized = getHead();
            if (number == (UInt256)finalized.Number) return (finalized, false);
            return await HistoricalStateHeadAsync(finalized, number, cancellationToken);
        }
        else if (block.ValueKind == JsonValueKind.Object)
        {
            bool hasHash = block.TryGetProperty("blockHash", out JsonElement hash);
            bool hasNumber = block.TryGetProperty("blockNumber", out JsonElement number);
            if (hasHash == hasNumber) throw InvalidParameters();
            foreach (JsonProperty property in block.EnumerateObject())
            {
                if (property.Name is not ("blockHash" or "blockNumber" or "requireCanonical")) throw InvalidParameters();
            }
            if (block.TryGetProperty("requireCanonical", out JsonElement canonical)
                && (!hasHash || canonical.ValueKind is not (JsonValueKind.True or JsonValueKind.False))) throw InvalidParameters();
            if (hasHash)
            {
                if (hash.ValueKind != JsonValueKind.String) throw InvalidParameters();
                Hash256 requested = new(ReadParameterData(hash, 32));
                if (getLatestHead?.Invoke() is { } latest && requested == latest.BlockHash)
                    return (latest, false);
                VerifiedHead finalized = getHead();
                if (requested == finalized.BlockHash) return (finalized, false);
                return await HistoricalStateHashAsync(finalized, hash, cancellationToken);
            }
            else
            {
                UInt256 parsed = ParseQuantity(number);
                if (getLatestHead?.Invoke() is { } latest && parsed == (UInt256)latest.Number)
                    return (latest, false);
                VerifiedHead finalized = getHead();
                if (parsed == (UInt256)finalized.Number) return (finalized, false);
                return await HistoricalStateHeadAsync(finalized, parsed, cancellationToken);
            }
        }
        else throw InvalidParameters();
    }

    private async Task<(VerifiedHead Head, bool Historical)> HistoricalStateHashAsync(VerifiedHead finalized, JsonElement hash,
        CancellationToken cancellationToken)
    {
        await AcquireHistoricalStateAsync(cancellationToken);
        try
        {
            BlockHeader header = await ResolveDataHeaderAsync(hash, hashOnly: true, cancellationToken, finalized, stateHistory: true);
            return (new(finalized.Slot, header.Number, header.Hash!, header.StateRoot!), true);
        }
        catch
        {
            _historicalStateRequests.Release();
            throw;
        }
    }

    private async Task<(VerifiedHead Head, bool Historical)> HistoricalStateHeadAsync(VerifiedHead finalized, UInt256 number,
        CancellationToken cancellationToken)
    {
        if (number > ulong.MaxValue)
            throw new RpcException(-32001, "Block is outside the verified historical state window.");
        ValidateHistoricalStateNumber(finalized, (ulong)number);
        await AcquireHistoricalStateAsync(cancellationToken);
        try
        {
            try
            {
                BlockHeader header = await execution.GetCanonicalHeaderAsync(finalized, (ulong)number, cancellationToken);
                return (new(finalized.Slot, header.Number, header.Hash!, header.StateRoot!), true);
            }
            catch (NotSupportedException)
            {
                throw new RpcException(-32001, "Historical state proofs are unavailable from the execution source.");
            }
        }
        catch
        {
            _historicalStateRequests.Release();
            throw;
        }
    }

    private async Task AcquireHistoricalStateAsync(CancellationToken cancellationToken)
    {
        if (!await _historicalStateRequests.WaitAsync(0, cancellationToken))
            throw new RpcException(-32000, "Too many concurrent historical state requests.");
    }

    private static void ValidateHistoricalStateNumber(VerifiedHead finalized, ulong number)
    {
        if (number > finalized.Number || finalized.Number - number > HistoricalStateDepth)
            throw new RpcException(-32001, "Block is outside the verified historical state window.");
    }

    private static byte[] ReadData(JsonElement element, int maximumBytes)
    {
        if (element.ValueKind != JsonValueKind.String) throw new InvalidDataException("Expected hexadecimal data.");
        string value = element.GetString()!;
        if (!value.StartsWith("0x", StringComparison.Ordinal) || value.Length % 2 != 0 || value.Length > maximumBytes * 2 + 2)
            throw new InvalidDataException("Invalid hexadecimal data size.");
        return Convert.FromHexString(value.AsSpan(2));
    }

    private static byte[] ReadParameterData(JsonElement element, int size)
    {
        try
        {
            byte[] bytes = ReadData(element, size);
            if (bytes.Length != size) throw InvalidParameters();
            return bytes;
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException)
        {
            throw InvalidParameters();
        }
    }

    private static UInt256 ParseQuantity(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String) throw InvalidParameters();
        string value = element.GetString()!;
        if (!value.StartsWith("0x", StringComparison.Ordinal) || value.Length is < 3 or > 66 || value.Length > 3 && value[2] == '0')
            throw InvalidParameters();
        try
        {
            string digits = value[2..];
            return new UInt256(Convert.FromHexString(digits.Length % 2 == 0 ? digits : "0" + digits), isBigEndian: true);
        }
        catch (FormatException) { throw InvalidParameters(); }
    }

    private static string Quantity(ulong value) => $"0x{value:x}";
    private static string Quantity(UInt256 value) => value.ToHexString(skipLeadingZeros: true);
    private static RpcException InvalidParameters() => new(-32602, "Invalid verified RPC parameters.");
}
