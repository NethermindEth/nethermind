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
    VerifiedCall? calls = null, ISpecProvider? specProvider = null, Func<VerifiedHead?>? getLatestHead = null,
    Func<VerifiedHead, bool>? isLatestHeadFresh = null)
    : IDisposable
{
    private const ulong HistoricalStateDepth = 32;
    private readonly ulong _startingBlock = getLatestHead?.Invoke()?.Number ?? 0;
    private static readonly SemaphoreSlim ActiveExecutionData = new(4, 4);
    private readonly VerifiedExecutionData? _data = specProvider is null ? null : new(execution, specProvider, chainId);
    private readonly SemaphoreSlim _historicalStateRequests = new(2, 2);

    internal JsonElement GetHeader(BlockHeader header) => _data!.GetHeader(header);

    internal async Task<JsonElement> GetSubscriptionLogsAsync(BlockHeader header, CancellationToken cancellationToken)
    {
        await ActiveExecutionData.WaitAsync(cancellationToken);
        try { return await _data!.GetLogsAsync([header], null, [], cancellationToken); }
        finally { ActiveExecutionData.Release(); }
    }

    internal async Task<object> InvokeAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        bool data = method is "eth_getBlockByNumber" or "eth_getBlockByHash" or "eth_getBlockTransactionCountByNumber"
            or "eth_getBlockTransactionCountByHash" or "eth_getTransactionByBlockNumberAndIndex"
            or "eth_getTransactionByBlockHashAndIndex" or "eth_getBlockReceipts" or "eth_getLogs"
            or "eth_getTransactionByHash" or "eth_getTransactionReceipt" or "eth_feeHistory"
            or "eth_gasPrice" or "eth_maxPriorityFeePerGas" or "eth_getProof";
        if (data && !await ActiveExecutionData.WaitAsync(0, cancellationToken))
            throw new RpcException(-32005, "Four verified execution data requests are already active.");
        try { return await InvokeCoreAsync(method, parameters, cancellationToken); }
        finally { if (data) ActiveExecutionData.Release(); }
    }

    private async Task<object> InvokeCoreAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        int count = parameters.ValueKind switch
        {
            JsonValueKind.Undefined => 0,
            JsonValueKind.Array => parameters.GetArrayLength(),
            _ => throw InvalidParameters()
        };
        if (method is "eth_chainId" or "eth_blockNumber" or "eth_syncing")
        {
            if (count != 0) throw InvalidParameters();
            if (method == "eth_chainId") return Quantity(chainId);
            VerifiedHead? current = getLatestHead?.Invoke();
            if (method == "eth_blockNumber")
                return Quantity(current?.Number ?? throw new RpcException(-32001, "An authenticated optimistic execution head is not available."));
            if (current is not null && (isLatestHeadFresh?.Invoke(current) ?? true)) return false;
            ulong number = current?.Number ?? 0;
            return new { startingBlock = Quantity(_startingBlock), currentBlock = Quantity(number), highestBlock = Quantity(number) };
        }
        if (method == "eth_sendRawTransaction")
        {
            if (_data is null) throw new RpcException(-32601, "Verified transaction relay is unavailable.");
            if (count != 1) throw InvalidParameters();
            byte[] raw;
            try { raw = ReadData(parameters[0], 2 * 1024 * 1024); }
            catch (Exception exception) when (exception is InvalidDataException or FormatException) { throw InvalidParameters(); }
            return await _data.SendRawTransactionAsync(LatestHead(), raw, calls, cancellationToken);
        }
        if (method == "eth_getProof") return await GetProofAsync(parameters, count, cancellationToken);
        if (method is "eth_feeHistory" or "eth_gasPrice" or "eth_maxPriorityFeePerGas")
            return await GetFeesAsync(method, parameters, count, cancellationToken);
        if (method is "eth_getTransactionByHash" or "eth_getTransactionReceipt")
        {
            if (count != 1) throw InvalidParameters();
            return await GetTransactionByHashAsync(new Hash256(ReadParameterData(parameters[0], 32)),
                method == "eth_getTransactionReceipt", cancellationToken);
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
        UInt256 key = storage ? ParseQuantity(parameters[1], allowLeadingZeros: true) : UInt256.Zero;
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

    private async Task<JsonElement> GetProofAsync(JsonElement parameters, int count, CancellationToken cancellationToken)
    {
        if (count != 3 || parameters[0].ValueKind != JsonValueKind.String
            || parameters[0].GetString() is not { Length: 42 } text || !Address.IsValidAddress(text, allowPrefix: true)
            || parameters[1].ValueKind != JsonValueKind.Array || parameters[1].GetArrayLength() > 64) throw InvalidParameters();
        Address address = new(text);
        UInt256[] keys = new UInt256[parameters[1].GetArrayLength()];
        for (int i = 0; i < keys.Length; i++) keys[i] = ParseQuantity(parameters[1][i], allowLeadingZeros: true);
        (VerifiedHead head, bool historical) = await ResolveStateHeadAsync(parameters[2], cancellationToken);
        try
        {
            ExecutionAccountProof proof = await execution.GetProofAsync(head, address, keys, cancellationToken);
            if (ExecutionProofVerifier.VerifyAccount(head.StateRoot, address, proof.AccountProof) != proof.Account
                || proof.StorageProofs.Length != keys.Length) throw new InvalidDataException("Invalid exported account proof.");
            object[] storage = new object[keys.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                ExecutionStorageProof slot = proof.StorageProofs[i];
                if (slot.Key != keys[i] || ExecutionProofVerifier.VerifyStorage(proof.Account.StorageRoot, slot.Key, slot.Proof) != slot.Value)
                    throw new InvalidDataException("Invalid exported storage proof.");
                storage[i] = new { key = Quantity(slot.Key), value = Quantity(slot.Value), proof = slot.Proof };
            }
            return BoundedJson.ToElement(new
            {
                address = address.ToString(),
                balance = Quantity(proof.Account.Balance),
                nonce = Quantity(proof.Account.Nonce),
                codeHash = proof.Account.CodeHash.ToString(),
                storageHash = proof.Account.StorageRoot.ToString(),
                accountProof = proof.AccountProof,
                storageProof = storage,
            });
        }
        catch (Exception exception) when (exception is InvalidDataException or RlpException)
        {
            throw new RpcException(-32000, "Execution peers returned invalid or unavailable path proofs.");
        }
        finally { if (historical) _historicalStateRequests.Release(); }
    }

    private async Task<object> GetFeesAsync(string method, JsonElement parameters, int count, CancellationToken cancellationToken)
    {
        if (_data is null) throw new RpcException(-32601, "Verified execution data is unavailable.");
        double[]? percentiles;
        BlockHeader newest;
        int blocks;
        if (method == "eth_feeHistory")
        {
            if (count != 3) throw InvalidParameters();
            UInt256 requested = ParseQuantity(parameters[0]);
            if (requested < 1 || requested > 16) throw new RpcException(-32602, "Fee history is limited to 1 through 16 authenticated blocks.");
            blocks = (int)requested;
            double[] parsedPercentiles = ParsePercentiles(parameters[2]);
            percentiles = parsedPercentiles.Length == 0 ? null : parsedPercentiles;
            newest = await ResolveDataHeaderAsync(parameters[1], false, cancellationToken);
        }
        else
        {
            if (count != 0) throw InvalidParameters();
            newest = await execution.GetHeaderAsync(LatestHead(), cancellationToken);
            blocks = 1;
            percentiles = [60];
        }
        blocks = checked((int)Math.Min((ulong)blocks, newest.Number + 1));
        BlockHeader[] headers = await GetRecentHeadersAsync(newest, blocks, cancellationToken);
        JsonElement history = await _data.GetFeeHistoryAsync(headers, percentiles, cancellationToken);
        if (method == "eth_feeHistory") return history;
        UInt256 tip = ParseQuantity(history.GetProperty("reward")[0][0]);
        if (method == "eth_maxPriorityFeePerGas") return Quantity(tip);
        UInt256 baseFee = ParseQuantity(history.GetProperty("baseFeePerGas")[1]);
        return Quantity(UInt256.AddOverflow(baseFee, tip, out UInt256 price) ? UInt256.MaxValue : price);
    }

    private static double[] ParsePercentiles(JsonElement parameter)
    {
        if (parameter.ValueKind != JsonValueKind.Array || parameter.GetArrayLength() > 100) throw InvalidParameters();
        double[] result = new double[parameter.GetArrayLength()];
        for (int i = 0; i < result.Length; i++)
        {
            if (parameter[i].ValueKind != JsonValueKind.Number || !parameter[i].TryGetDouble(out double value)
                || !double.IsFinite(value) || value < 0 || value > 100 || i != 0 && value < result[i - 1]) throw InvalidParameters();
            result[i] = value;
        }
        return result;
    }

    private async Task<BlockHeader[]> GetRecentHeadersAsync(BlockHeader newest, int count, CancellationToken cancellationToken)
    {
        BlockHeader[] headers = new BlockHeader[count];
        headers[^1] = newest;
        if (count == 1) return headers;
        VerifiedHead anchor = new(0, newest.Number, newest.Hash!, newest.StateRoot!);
        ulong first = newest.Number - (ulong)count + 1;
        Hash256[] hashes = await execution.GetAncestorHashesAsync(anchor, first, cancellationToken);
        if (hashes.Length != count || hashes[^1] != newest.Hash) throw new InvalidDataException("Invalid authenticated ancestor chain.");
        for (int i = 0; i < count - 1; i++)
        {
            headers[i] = await execution.GetHeaderByHashAsync(hashes[i], cancellationToken);
            if (headers[i].Hash != hashes[i] || headers[i].Number != first + (ulong)i)
                throw new InvalidDataException("An ancestor header differs from the authenticated chain.");
        }
        return headers;
    }

    private async Task<JsonElement> GetTransactionByHashAsync(Hash256 hash, bool receipt, CancellationToken cancellationToken)
    {
        if (_data is null) throw new RpcException(-32601, "Verified execution data is unavailable.");
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        cancellationToken = deadline.Token;
        HashSet<Hash256> visited = [];
        if (_data.TryLocateTransaction(hash, out BlockHeader? indexed))
        {
            try
            {
                BlockHeader canonical = await ResolveIndexedTransactionHeaderAsync(indexed!, cancellationToken);
                if (await _data.FindTransactionAsync(canonical, hash, receipt, cancellationToken) is { } known) return known;
            }
            catch (RpcException exception) when (exception.Code == -32001) { }
        }
        if (getLatestHead?.Invoke() is { } latest)
        {
            BlockHeader header = await execution.GetHeaderAsync(latest, cancellationToken);
            visited.Add(header.Hash!);
            if (await _data.FindTransactionAsync(header, hash, receipt, cancellationToken) is { } optimistic) return optimistic;
            BlockHeader[] optimisticHeaders = await GetRecentHeadersAsync(header, checked((int)Math.Min(16UL, latest.Number + 1)), cancellationToken);
            for (int i = optimisticHeaders.Length - 2; i >= 0; i--)
            {
                visited.Add(optimisticHeaders[i].Hash!);
                if (await _data.FindTransactionAsync(optimisticHeaders[i], hash, receipt, cancellationToken) is { } recent) return recent;
            }
        }
        VerifiedHead finalized = getHead();
        BlockHeader newest = await execution.GetHeaderAsync(finalized, cancellationToken);
        if (visited.Add(newest.Hash!) && await _data.FindTransactionAsync(newest, hash, receipt, cancellationToken) is { } current) return current;
        BlockHeader[] headers = await GetRecentHeadersAsync(newest, checked((int)Math.Min(16UL, finalized.Number + 1)), cancellationToken);
        for (int i = headers.Length - 2; i >= 0; i--)
            if (visited.Add(headers[i].Hash!) && await _data.FindTransactionAsync(headers[i], hash, receipt, cancellationToken) is { } found) return found;
        return JsonSerializer.SerializeToElement<object?>(null);
    }

    private async Task<BlockHeader> ResolveIndexedTransactionHeaderAsync(BlockHeader indexed, CancellationToken cancellationToken)
    {
        VerifiedHead? latest = getLatestHead?.Invoke();
        if (latest is not null && indexed.Hash == latest.BlockHash) return await execution.GetHeaderAsync(latest, cancellationToken);
        VerifiedHead finalized = getHead();
        VerifiedHead anchor = indexed.Number <= finalized.Number ? finalized
            : latest ?? throw new RpcException(-32001, "The indexed transaction is outside the authenticated chain.");
        if (indexed.Number > anchor.Number || anchor.Number - indexed.Number > 256)
            throw new RpcException(-32001, "The indexed transaction is outside the authenticated history window.");
        BlockHeader canonical = await execution.GetCanonicalHeaderAsync(anchor, indexed.Number, cancellationToken);
        if (canonical.Hash != indexed.Hash)
            throw new RpcException(-32001, "The indexed transaction's block is no longer canonical.");
        return canonical;
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
        CancellationToken cancellationToken, VerifiedHead? pinnedFinalized = null, bool stateHistory = false,
        VerifiedHead? pinnedLatest = null)
    {
        if (selector.ValueKind != JsonValueKind.String) throw InvalidParameters();
        string value = selector.GetString()!;
        RejectUnavailableTag(value);
        if (!hashOnly && value == "latest") return await execution.GetHeaderAsync(LatestHead(), cancellationToken);
        VerifiedHead? latest = pinnedLatest ?? getLatestHead?.Invoke();
        if (hashOnly || value.Length == 66)
        {
            Hash256 requested = new(ReadParameterData(selector, 32));
            if (latest is not null && requested == latest.BlockHash)
                return await execution.GetHeaderAsync(latest, cancellationToken);
            VerifiedHead finalizedHead = pinnedFinalized ?? getHead();
            if (requested == finalizedHead.BlockHash) return await execution.GetHeaderAsync(finalizedHead, cancellationToken);
            BlockHeader candidate = await execution.GetHeaderByHashAsync(requested, cancellationToken);
            VerifiedHead anchor = SelectHistoryAnchor(finalizedHead, latest, candidate.Number, stateHistory ? HistoricalStateDepth : 256);
            BlockHeader canonical = await execution.GetCanonicalHeaderAsync(anchor, candidate.Number, cancellationToken);
            if (canonical.Hash != requested || canonical.Number != candidate.Number)
                throw new RpcException(-32001, "Block is not in the selected authenticated chain.");
            return canonical;
        }
        if (value == "finalized") return await execution.GetHeaderAsync(pinnedFinalized ?? getHead(), cancellationToken);
        UInt256 parsed = ParseQuantity(selector);
        if (latest is not null && parsed == (UInt256)latest.Number)
            return await execution.GetHeaderAsync(latest, cancellationToken);
        VerifiedHead head = pinnedFinalized ?? getHead();
        VerifiedHead numberAnchor = SelectHistoryAnchor(head, latest, parsed, stateHistory ? HistoricalStateDepth : 256);
        return await execution.GetCanonicalHeaderAsync(numberAnchor, (ulong)parsed, cancellationToken);
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
        VerifiedHead? current = latestFrom || latestTo ? LatestHead() : getLatestHead?.Invoke();
        bool optimisticFrom = latestFrom || current is not null && from.ValueKind == JsonValueKind.String
            && from.GetString()?.StartsWith("0x", StringComparison.Ordinal) == true
            && ParseQuantity(from) == (UInt256)current.Number;
        bool optimisticTo = latestTo || current is not null && to.ValueKind == JsonValueKind.String
            && to.GetString()?.StartsWith("0x", StringComparison.Ordinal) == true
            && ParseQuantity(to) == (UInt256)current.Number;
        if (optimisticFrom && optimisticTo)
        {
            BlockHeader latest = await execution.GetHeaderAsync(current!, cancellationToken);
            return await _data!.GetLogsAsync([latest], addresses, topics, cancellationToken);
        }
        VerifiedHead finalized = getHead();
        ulong first = ParseLogBlock(from, finalized, current);
        ulong last = ParseLogBlock(to, finalized, current);
        if (first > last) throw InvalidParameters();
        VerifiedHead anchor = optimisticFrom || optimisticTo || last > finalized.Number ? current ?? finalized : finalized;
        if (first > anchor.Number || last > anchor.Number) throw InvalidParameters();
        if (anchor.Number < finalized.Number || anchor.Number - first > 256)
            throw new RpcException(-32001, "Block is outside the verified history window.");
        Hash256[] hashes = first == anchor.Number ? [anchor.BlockHash]
            : await execution.GetAncestorHashesAsync(anchor, first, cancellationToken);
        if (hashes.Length != checked((int)(anchor.Number - first + 1)) || hashes[^1] != anchor.BlockHash)
            throw new InvalidDataException("Invalid authenticated ancestor chain.");
        if (first <= finalized.Number && finalized.Number <= anchor.Number
            && hashes[checked((int)(finalized.Number - first))] != finalized.BlockHash)
            throw new InvalidDataException("Optimistic ancestry conflicts with authenticated finality.");
        BlockHeader[] headers = new BlockHeader[checked((int)(last - first + 1))];
        for (int i = 0; i < headers.Length; i++)
        {
            ulong number = first + (ulong)i;
            BlockHeader header = number == anchor.Number
                ? await execution.GetHeaderAsync(anchor, cancellationToken)
                : await execution.GetHeaderByHashAsync(hashes[i], cancellationToken);
            if (header.Number != number || header.Hash != hashes[i]
                || i > 0 && header.ParentHash != headers[i - 1].Hash)
                throw new InvalidDataException("An ancestor header differs from the authenticated chain.");
            headers[i] = header;
        }
        return await _data!.GetLogsAsync(headers, addresses, topics, cancellationToken);
    }

    private static ulong ParseLogBlock(JsonElement selector, VerifiedHead finalized, VerifiedHead? latest)
    {
        if (selector.ValueKind == JsonValueKind.Undefined) return latest!.Number;
        if (selector.ValueKind == JsonValueKind.String)
        {
            string value = selector.GetString()!;
            if (value == "latest") return latest!.Number;
            if (value == "finalized") return finalized.Number;
            RejectUnavailableTag(value);
        }
        UInt256 number = ParseQuantity(selector);
        if (number > ulong.MaxValue) throw InvalidParameters();
        return (ulong)number;
    }

    internal static HashSet<Address>? ParseAddresses(JsonElement value)
    {
        HashSet<Address> result = [];
        if (value.ValueKind == JsonValueKind.String) Add(value);
        else if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= 16)
            foreach (JsonElement item in value.EnumerateArray()) Add(item);
        else throw InvalidParameters();
        return result.Count == 0 ? null : result;

        void Add(JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } text
                || text.Length != 42 || !Address.IsValidAddress(text, allowPrefix: true)) throw InvalidParameters();
            result.Add(new Address(text));
        }
    }

    internal static List<HashSet<Hash256>?> ParseTopics(JsonElement value)
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
            result.Add(wildcard || choices.Count == 0 ? null : choices);
        }
        return result;
    }

    private VerifiedHead LatestHead()
    {
        VerifiedHead head = getLatestHead?.Invoke()
            ?? throw new RpcException(-32001, "An authenticated optimistic execution head is not available.");
        if (!(isLatestHeadFresh?.Invoke(head) ?? true))
            throw new RpcException(-32001, "Authenticated optimistic execution head is stale or ahead of the local clock; waiting for consensus sync.");
        return head;
    }

    private async Task<(VerifiedHead Head, bool Historical)> ResolveStateHeadAsync(JsonElement block, CancellationToken cancellationToken)
    {
        if (block.ValueKind == JsonValueKind.String)
        {
            string selector = block.GetString()!;
            RejectUnavailableTag(selector);
            if (selector == "latest") return (LatestHead(), false);
            if (selector.Length == 66)
            {
                Hash256 requested = new(ReadParameterData(block, 32));
                VerifiedHead? optimistic = getLatestHead?.Invoke();
                if (optimistic is not null && requested == optimistic.BlockHash)
                    return (optimistic, false);
                VerifiedHead hashAnchor = getHead();
                if (requested == hashAnchor.BlockHash) return (hashAnchor, false);
                return await HistoricalStateHashAsync(hashAnchor, optimistic, block, cancellationToken);
            }
            if (selector == "finalized") return (getHead(), false);
            UInt256 number = ParseQuantity(block);
            VerifiedHead? latest = getLatestHead?.Invoke();
            if (latest is not null && number == (UInt256)latest.Number)
                return (latest, false);
            VerifiedHead finalized = getHead();
            if (number == (UInt256)finalized.Number) return (finalized, false);
            return await HistoricalStateHeadAsync(SelectHistoryAnchor(finalized, latest, number, HistoricalStateDepth), number, cancellationToken);
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
                VerifiedHead? latest = getLatestHead?.Invoke();
                if (latest is not null && requested == latest.BlockHash)
                    return (latest, false);
                VerifiedHead finalized = getHead();
                if (requested == finalized.BlockHash) return (finalized, false);
                return await HistoricalStateHashAsync(finalized, latest, hash, cancellationToken);
            }
            else
            {
                UInt256 parsed = ParseQuantity(number);
                VerifiedHead? latest = getLatestHead?.Invoke();
                if (latest is not null && parsed == (UInt256)latest.Number)
                    return (latest, false);
                VerifiedHead finalized = getHead();
                if (parsed == (UInt256)finalized.Number) return (finalized, false);
                return await HistoricalStateHeadAsync(SelectHistoryAnchor(finalized, latest, parsed, HistoricalStateDepth), parsed, cancellationToken);
            }
        }
        else throw InvalidParameters();
    }

    private async Task<(VerifiedHead Head, bool Historical)> HistoricalStateHashAsync(VerifiedHead finalized, VerifiedHead? latest, JsonElement hash,
        CancellationToken cancellationToken)
    {
        await AcquireHistoricalStateAsync(cancellationToken);
        try
        {
            BlockHeader header = await ResolveDataHeaderAsync(hash, hashOnly: true, cancellationToken, finalized, stateHistory: true, pinnedLatest: latest);
            VerifiedHead anchor = SelectHistoryAnchor(finalized, latest, header.Number, HistoricalStateDepth);
            return (new(anchor.Slot, header.Number, header.Hash!, header.StateRoot!), true);
        }
        catch
        {
            _historicalStateRequests.Release();
            throw;
        }
    }

    private async Task<(VerifiedHead Head, bool Historical)> HistoricalStateHeadAsync(VerifiedHead anchor, UInt256 number,
        CancellationToken cancellationToken)
    {
        await AcquireHistoricalStateAsync(cancellationToken);
        try
        {
            try
            {
                BlockHeader header = await execution.GetCanonicalHeaderAsync(anchor, (ulong)number, cancellationToken);
                if (header.Number != (ulong)number) throw new RpcException(-32000, "Execution peer returned a header outside the selected authenticated chain.");
                return (new(anchor.Slot, header.Number, header.Hash!, header.StateRoot!), true);
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

    private static VerifiedHead SelectHistoryAnchor(VerifiedHead finalized, VerifiedHead? latest, UInt256 number, ulong depth)
    {
        VerifiedHead anchor = number > (UInt256)finalized.Number && latest is not null ? latest : finalized;
        if (number > (UInt256)anchor.Number || number > ulong.MaxValue || anchor.Number - (ulong)number > depth)
            throw new RpcException(-32001, "Block is outside the verified history window.");
        return anchor;
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

    private static UInt256 ParseQuantity(JsonElement element, bool allowLeadingZeros = false)
    {
        if (element.ValueKind != JsonValueKind.String) throw InvalidParameters();
        string value = element.GetString()!;
        if (!value.StartsWith("0x", StringComparison.Ordinal) || value.Length is < 3 or > 66 ||
            !allowLeadingZeros && value.Length > 3 && value[2] == '0')
            throw InvalidParameters();
        try
        {
            string digits = value[2..];
            return new UInt256(Convert.FromHexString(digits.Length % 2 == 0 ? digits : "0" + digits), isBigEndian: true);
        }
        catch (FormatException) { throw InvalidParameters(); }
    }

    private static void RejectUnavailableTag(string selector)
    {
        if (selector == "pending") throw new RpcException(-32001, "Pending state cannot be authenticated from execution peers.");
        if (selector == "safe") throw new RpcException(-32001, "A distinct authenticated safe execution head is unavailable; select finalized or latest explicitly.");
        if (selector == "earliest") throw new RpcException(-32001, "Genesis is outside the bounded authenticated history; use a recent block selector.");
    }

    internal static string Quantity(ulong value) => $"0x{value:x}";
    internal static string Quantity(UInt256 value) => value.ToHexString(skipLeadingZeros: true);
    private static RpcException InvalidParameters() => new(-32602, "Invalid verified RPC parameters.");

    public void Dispose()
    {
        _data?.Dispose();
        _historicalStateRequests.Dispose();
    }
}
