// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.LightClient;

internal sealed class VerifiedRpc(IExecutionStateSource execution, Func<VerifiedHead> getHead, ulong chainId,
    VerifiedCall? calls = null, ISpecProvider? specProvider = null, Func<VerifiedHead?>? getLatestHead = null,
    Func<VerifiedHead, bool>? isLatestHeadFresh = null, Func<VerifiedHead, bool>? isFinalizedHeadFresh = null,
    Func<CancellationToken, Task<VerifiedHead>>? waitForLatestHead = null)
    : IDisposable
{
    private const ulong HistoricalStateDepth = 32;
    private const long LogResultCacheCapacity = 32 * 1024 * 1024;
    private readonly ulong _startingBlock = getLatestHead?.Invoke()?.Number ?? 0;
    private static readonly SemaphoreSlim ActiveExecutionData = new(4, 4);
    private readonly VerifiedExecutionData? _data = specProvider is null ? null : new(execution, specProvider, chainId);
    private readonly SemaphoreSlim _historicalStateRequests = new(2, 2);
    private readonly SemaphoreSlim _proofRequests = new(2, 2);
    private readonly MemoryCache _logResults = new(new MemoryCacheOptions { SizeLimit = LogResultCacheCapacity });

    internal JsonElement GetHeader(BlockHeader header) => _data!.GetHeader(header);

    internal Task<JsonElement> GetSubscriptionLogsAsync(BlockHeader header, Func<Address, Hash256[], bool> matches,
        CancellationToken cancellationToken) => _data!.GetLogsAsync([header], null, [], cancellationToken, matches);

    internal async Task<object> InvokeAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        int count = parameters.ValueKind switch
        {
            JsonValueKind.Undefined => 0,
            JsonValueKind.Array => parameters.GetArrayLength(),
            _ => throw InvalidParameters()
        };
        if (TryReadLocalMetadata(method, parameters, chainId, listening: true, out object metadata)) return metadata;
        if (method == "eth_blobBaseFee")
        {
            if (count != 0) throw InvalidParameters();
            if (specProvider is null) throw new RpcException(-32601, "Verified blob fees are unavailable.");
            VerifiedHead latest = await LatestHeadAsync(cancellationToken);
            using DataRequest admission = new(cancellationToken);
            BlockHeader header = await execution.GetHeaderAsync(latest, cancellationToken);
            IReleaseSpec spec = specProvider.GetSpec(header);
            if (!spec.IsEip4844Enabled || header.ExcessBlobGas is not { } excess)
                throw new RpcException(-32001, "Blob fees are unavailable for this authenticated execution head.");
            if (!BlobGasCalculator.TryCalculateFeePerBlobGas(excess, spec.BlobBaseFeeUpdateFraction, out UInt256 fee))
                throw new RpcException(-32000, "Authenticated blob fee exceeds the supported integer range.");
            return Quantity(fee);
        }
        if (method is "eth_blockNumber" or "eth_syncing")
        {
            if (count != 0) throw InvalidParameters();
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
            return await _data.SendRawTransactionAsync(await LatestHeadAsync(cancellationToken), raw, calls, cancellationToken);
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
                ? (await LatestHeadAsync(cancellationToken), false) : await ResolveStateHeadAsync(parameters[1], cancellationToken);
            try { return await calls.ExecuteAsync(callHead, parameters[0], method == "eth_estimateGas", cancellationToken); }
            finally { if (historical) _historicalStateRequests.Release(); }
        }
        if (method is not ("eth_getBalance" or "eth_getTransactionCount" or "eth_getCode" or "eth_getStorageAt"))
            throw new RpcException(-32601, "Method is not supported by the verified RPC.");

        bool storage = method == "eth_getStorageAt";
        int required = storage ? 2 : 1;
        if (count < required || count > required + 1) throw InvalidParameters();
        JsonElement addressParameter = parameters[0];
        if (addressParameter.ValueKind != JsonValueKind.String) throw InvalidParameters();
        string addressText = addressParameter.GetString()!;
        if (addressText.Length != 42 || !Address.IsValidAddress(addressText, allowPrefix: true)) throw InvalidParameters();
        Address address = new(addressText);
        UInt256 key = storage ? ParseQuantity(parameters[1], allowLeadingZeros: true) : UInt256.Zero;
        (VerifiedHead head, bool historicalState) = count == required
            ? (await LatestHeadAsync(cancellationToken), false)
            : await ResolveStateHeadAsync(parameters[required], cancellationToken);
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
        if (count is < 2 or > 3 || parameters[0].ValueKind != JsonValueKind.String
            || parameters[0].GetString() is not { Length: 42 } text || !Address.IsValidAddress(text, allowPrefix: true)
            || parameters[1].ValueKind != JsonValueKind.Array || parameters[1].GetArrayLength() > 64) throw InvalidParameters();
        Address address = new(text);
        UInt256[] keys = new UInt256[parameters[1].GetArrayLength()];
        for (int i = 0; i < keys.Length; i++) keys[i] = ParseQuantity(parameters[1][i], allowLeadingZeros: true);
        if (count == 3) _ = ParseBlockSelector(parameters[2], false, out _, out _);
        VerifiedHead? latest = await LatestForSelectorAsync(count == 2 ? default : parameters[2], cancellationToken);
        if (!await _proofRequests.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken))
            throw new RpcException(-32005, "Too many concurrent account proof requests.");
        try
        {
            using DataRequest admission = new(cancellationToken);
            (VerifiedHead head, bool historical) = count == 2
                ? (latest!, false)
                : await ResolveStateHeadAsync(parameters[2], cancellationToken, latest);
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
                    storage[i] = new { key = slot.Key.ToBigEndian().ToHexString(withZeroX: true), value = Quantity(slot.Value), proof = slot.Proof };
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
        finally { _proofRequests.Release(); }
    }

    private async Task<object> GetFeesAsync(string method, JsonElement parameters, int count, CancellationToken cancellationToken)
    {
        if (_data is null) throw new RpcException(-32601, "Verified execution data is unavailable.");
        double[]? percentiles;
        int blocks;
        JsonElement selector = default;
        bool requireCanonical = false;
        bool byHash = false;
        if (method == "eth_feeHistory")
        {
            if (count != 3) throw InvalidParameters();
            UInt256 requested = ParseQuantity(parameters[0]);
            if (requested < 1) throw InvalidParameters();
            blocks = (int)UInt256.Min(requested, 16);
            double[] parsedPercentiles = ParsePercentiles(parameters[2]);
            percentiles = parsedPercentiles.Length == 0 ? null : parsedPercentiles;
            selector = ParseBlockSelector(parameters[1], false, out requireCanonical, out byHash);
        }
        else
        {
            if (count != 0) throw InvalidParameters();
            blocks = 1;
            percentiles = [60];
        }
        VerifiedHead? latest = await LatestForSelectorAsync(selector, cancellationToken);
        using DataRequest admission = new(cancellationToken);
        BlockHeader newest = method == "eth_feeHistory"
            ? await ResolveDataHeaderAsync(selector, byHash, cancellationToken, pinnedLatest: latest, requireCanonical: requireCanonical)
                ?? throw new RpcException(-32001, "Requested authenticated fee history is unavailable.")
            : await execution.GetHeaderAsync(latest!, cancellationToken);
        blocks = checked((int)Math.Min((ulong)blocks, newest.Number + 1));
        BlockHeader[] headers = await GetRecentHeadersAsync(newest, blocks, cancellationToken);
        JsonElement history = await _data.GetFeeHistoryAsync(headers, percentiles, cancellationToken);
        if (method == "eth_feeHistory") return history;
        UInt256 tip = UInt256.Max(UInt256.One, ParseQuantity(history.GetProperty("reward")[0][0]));
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
        using DataRequest admission = new(cancellationToken);
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
        VerifiedHead finalized = getHead();
        if (getLatestHead?.Invoke() is { } latest)
        {
            BlockHeader header = await execution.GetHeaderAsync(latest, cancellationToken);
            visited.Add(header.Hash!);
            if (await _data.FindTransactionAsync(header, hash, receipt, cancellationToken) is { } optimistic) return optimistic;
            ulong oldest = Math.Min(latest.Number, finalized.Number > 15 ? finalized.Number - 15 : 0);
            int count = checked((int)Math.Min(257UL, latest.Number - oldest + 1));
            BlockHeader[] optimisticHeaders = await GetRecentHeadersAsync(header, count, cancellationToken);
            for (int i = optimisticHeaders.Length - 2; i >= 0; i--)
            {
                visited.Add(optimisticHeaders[i].Hash!);
                if (await _data.FindTransactionAsync(optimisticHeaders[i], hash, receipt, cancellationToken) is { } recent) return recent;
            }
        }
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
            bool block = method is "eth_getBlockByHash" or "eth_getBlockByNumber";
            int expected = block || method is "eth_getTransactionByBlockHashAndIndex" or "eth_getTransactionByBlockNumberAndIndex" ? 2 : 1;
            if (count != expected) throw InvalidParameters();
            bool fullTransactions = false;
            UInt256 index = UInt256.Zero;
            if (expected == 2)
            {
                if (block)
                {
                    if (parameters[1].ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw InvalidParameters();
                    fullTransactions = parameters[1].GetBoolean();
                }
                else index = ParseQuantity(parameters[1]);
            }
            JsonElement selector = ParseBlockSelector(parameters[0], blockByHash, out bool requireCanonical, out bool byHash);
            VerifiedHead? latest = await LatestForSelectorAsync(selector, cancellationToken);
            using DataRequest admission = new(cancellationToken);
            BlockHeader? header = await ResolveDataHeaderAsync(selector, byHash, cancellationToken, pinnedLatest: latest,
                requireCanonical: requireCanonical);
            if (header is null) return JsonSerializer.SerializeToElement<object?>(null);
            if (block) return await _data.GetBlockAsync(header, fullTransactions, cancellationToken);
            if (method is "eth_getBlockTransactionCountByHash" or "eth_getBlockTransactionCountByNumber")
                return await _data.GetBlockTransactionCountAsync(header, cancellationToken);
            if (method == "eth_getBlockReceipts")
                return await _data.GetBlockReceiptsAsync(header, cancellationToken);

            if (index > int.MaxValue) return JsonSerializer.SerializeToElement<object?>(null);
            return await _data.GetTransactionAsync(header, (int)index, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or RlpException or OverflowException)
        {
            throw new RpcException(-32000, "Execution peers returned invalid or unavailable authenticated block data.");
        }
        catch (ExecutionDataUnavailableException)
        {
            throw new RpcException(-32001, "Requested authenticated block data is unavailable.");
        }
        catch (OperationCanceledException) when (!callerCancellation.IsCancellationRequested)
        {
            throw new RpcException(-32000, "Verified execution data request exceeded the 90-second deadline.");
        }
    }

    private static JsonElement ParseBlockSelector(JsonElement selector, bool hashOnly, out bool requireCanonical, out bool byHash)
    {
        requireCanonical = false;
        bool byNumber = false;
        if (selector.ValueKind == JsonValueKind.Object && !hashOnly)
        {
            bool hasHash = selector.TryGetProperty("blockHash", out JsonElement hash);
            bool hasNumber = selector.TryGetProperty("blockNumber", out JsonElement number);
            if (hasHash == hasNumber) throw InvalidParameters();
            foreach (JsonProperty property in selector.EnumerateObject())
                if (property.Name is not ("blockHash" or "blockNumber" or "requireCanonical")) throw InvalidParameters();
            if (selector.TryGetProperty("requireCanonical", out JsonElement requiredCanonical)
                && (!hasHash || requiredCanonical.ValueKind is not (JsonValueKind.True or JsonValueKind.False))) throw InvalidParameters();
            if (hasNumber) _ = ParseQuantity(number);
            requireCanonical = requiredCanonical.ValueKind == JsonValueKind.True;
            selector = hasHash ? hash : number;
            hashOnly = hasHash;
            byNumber = hasNumber;
        }
        if (selector.ValueKind != JsonValueKind.String) throw InvalidParameters();
        string value = selector.GetString()!;
        byHash = hashOnly || !byNumber && value.Length == 66;
        if (byHash) _ = ReadParameterData(selector, 32);
        else if (value is not ("latest" or "finalized"))
        {
            RejectUnavailableTag(value);
            _ = ParseQuantity(selector);
        }
        return selector;
    }

    private async Task<BlockHeader?> ResolveDataHeaderAsync(JsonElement selector, bool hashOnly,
        CancellationToken cancellationToken, VerifiedHead? pinnedFinalized = null, bool stateHistory = false,
        VerifiedHead? pinnedLatest = null, bool requireCanonical = false)
    {
        string value = selector.GetString()!;
        if (!hashOnly && value == "latest") return await execution.GetHeaderAsync(pinnedLatest ?? await LatestHeadAsync(cancellationToken), cancellationToken);
        VerifiedHead? latest = pinnedLatest ?? getLatestHead?.Invoke();
        if (hashOnly)
        {
            Hash256 requested = new(ReadParameterData(selector, 32));
            if (latest is not null && requested == latest.BlockHash)
                return await execution.GetHeaderAsync(latest, cancellationToken);
            VerifiedHead finalizedHead = pinnedFinalized ?? getHead();
            if (requested == finalizedHead.BlockHash) return await execution.GetHeaderAsync(finalizedHead, cancellationToken);
            BlockHeader candidate;
            try { candidate = await execution.GetHeaderByHashAsync(requested, cancellationToken); }
            catch (ExecutionDataUnavailableException) { return null; }
            VerifiedHead anchor = SelectHistoryAnchor(finalizedHead, latest, candidate.Number, stateHistory ? HistoricalStateDepth : 256);
            BlockHeader canonical = await execution.GetCanonicalHeaderAsync(anchor, candidate.Number, cancellationToken);
            if (canonical.Number != candidate.Number) throw new InvalidDataException("Canonical header has an unexpected block number.");
            if (canonical.Hash != requested)
            {
                if (requireCanonical) throw new RpcException(-32000, "Block is not in the selected authenticated chain.");
                return null;
            }
            return canonical;
        }
        if (value == "finalized") return await execution.GetHeaderAsync(FinalizedHead(pinnedFinalized), cancellationToken);
        UInt256 parsed = ParseQuantity(selector);
        if (latest is not null && parsed == (UInt256)latest.Number)
            return await execution.GetHeaderAsync(latest, cancellationToken);
        VerifiedHead head = pinnedFinalized ?? getHead();
        if (parsed > (UInt256)(latest ?? head).Number) return null;
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
        ValidateLogSelector(from);
        ValidateLogSelector(to);
        if (blockHash.ValueKind != JsonValueKind.Undefined)
        {
            Hash256 requested = new(ReadParameterData(blockHash, 32));
            using DataRequest admission = new(cancellationToken);
            VerifiedHead? latest = getLatestHead?.Invoke();
            BlockHeader one;
            if (latest?.BlockHash == requested) one = await execution.GetHeaderAsync(latest, cancellationToken);
            else
            {
                VerifiedHead hashFinalized = getHead();
                if (hashFinalized.BlockHash == requested) one = await execution.GetHeaderAsync(hashFinalized, cancellationToken);
                else
                {
                    BlockHeader candidate = await execution.GetHeaderByHashAsync(requested, cancellationToken);
                    VerifiedHead hashAnchor = candidate.Number > hashFinalized.Number ? latest ?? hashFinalized : hashFinalized;
                    if (candidate.Number > hashAnchor.Number)
                        throw new RpcException(-32001, "Block is outside the authenticated log chain.");
                    BlockHeader[] verified = await execution.GetLogHeadersAsync(hashAnchor, hashFinalized, candidate.Number, candidate.Number, cancellationToken);
                    one = verified[0];
                    if (one.Hash != requested)
                        throw new RpcException(-32000, "Block is not in the selected authenticated chain.");
                }
            }
            return await _data!.GetLogsAsync([one], addresses, topics, cancellationToken);
        }
        bool latestFrom = from.ValueKind == JsonValueKind.Undefined || from.ValueKind == JsonValueKind.String && from.GetString() == "latest";
        bool latestTo = to.ValueKind == JsonValueKind.Undefined || to.ValueKind == JsonValueKind.String && to.GetString() == "latest";
        VerifiedHead? current = latestFrom || latestTo ? await LatestHeadAsync(cancellationToken) : getLatestHead?.Invoke();
        using DataRequest logAdmission = new(cancellationToken);
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
        if (IsFinalizedTag(from) || IsFinalizedTag(to)) finalized = FinalizedHead(finalized);
        ulong first = ParseLogBlock(from, finalized, current);
        ulong last = ParseLogBlock(to, finalized, current);
        if (first > last) throw InvalidParameters();
        VerifiedHead anchor = optimisticFrom || optimisticTo || last > finalized.Number ? current ?? finalized : finalized;
        if (first > anchor.Number || last > anchor.Number) throw InvalidParameters();
        BlockHeader[] headers = await execution.GetLogHeadersAsync(anchor, finalized, first, last, cancellationToken);
        if (last > finalized.Number)
            return await _data!.GetLogsAsync(headers, addresses, topics, cancellationToken);

        (ulong First, ulong Last, ValueHash256 EndHash, ValueHash256 Filter) key =
            (first, last, headers[^1].Hash!.ValueHash256, LogFilterHash(addresses, topics));
        cancellationToken.ThrowIfCancellationRequested();
        if (_logResults.TryGetValue(key, out JsonElement cached)) return cached;

        JsonElement result = await _data!.GetLogsAsync(headers, addresses, topics, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // JsonDocument retains its token table as well as the UTF-8 result.
        long size = Math.Max(1024, result.GetRawText().Length * 4L);
        if (size <= LogResultCacheCapacity)
            _logResults.Set(key, result, new MemoryCacheEntryOptions { Size = size });
        return result;
    }

    private readonly struct DataRequest : IDisposable
    {
        internal DataRequest(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ActiveExecutionData.Wait(0))
                throw new RpcException(-32005, "Four verified execution data requests are already active.");
        }

        public void Dispose() => ActiveExecutionData.Release();
    }

    private static ValueHash256 LogFilterHash(HashSet<Address>? addresses, IReadOnlyList<HashSet<Hash256>?> topics)
    {
        StringBuilder filter = new();
        if (addresses is not null)
        {
            Address[] sorted = [.. addresses];
            Array.Sort(sorted);
            foreach (Address address in sorted) filter.Append(address);
        }
        filter.Append(';').Append(topics.Count);
        foreach (HashSet<Hash256>? choices in topics)
        {
            filter.Append(';');
            if (choices is null) continue;
            Hash256[] sorted = [.. choices];
            Array.Sort(sorted);
            foreach (Hash256 topic in sorted) filter.Append(topic);
        }
        return Keccak.Compute(filter.ToString()).ValueHash256;
    }

    private VerifiedHead FinalizedHead(VerifiedHead? pinned = null)
    {
        VerifiedHead head = pinned ?? getHead();
        if (!(isFinalizedHeadFresh?.Invoke(head) ?? true))
            throw new RpcException(-32001, "Verified finality is more than one hour old; waiting for consensus sync.");
        return head;
    }

    private static bool IsFinalizedTag(JsonElement selector) =>
        selector.ValueKind == JsonValueKind.String && selector.GetString() == "finalized";

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

    private static void ValidateLogSelector(JsonElement selector)
    {
        if (selector.ValueKind == JsonValueKind.Undefined) return;
        if (selector.ValueKind == JsonValueKind.String)
        {
            string value = selector.GetString()!;
            if (value is "latest" or "finalized") return;
            RejectUnavailableTag(value);
        }
        if (ParseQuantity(selector) > ulong.MaxValue) throw InvalidParameters();
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

    private async ValueTask<VerifiedHead?> LatestForSelectorAsync(JsonElement selector, CancellationToken cancellationToken) =>
        selector.ValueKind == JsonValueKind.Undefined || selector.ValueKind == JsonValueKind.String && selector.ValueEquals("latest")
            ? await LatestHeadAsync(cancellationToken) : getLatestHead?.Invoke();

    private async ValueTask<VerifiedHead> LatestHeadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VerifiedHead? head = getLatestHead?.Invoke();
        if ((head is null || !(isLatestHeadFresh?.Invoke(head) ?? true)) && waitForLatestHead is not null)
        {
            head = await waitForLatestHead(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        if (head is null) throw new RpcException(-32001, "An authenticated optimistic execution head is not available.");
        if (!(isLatestHeadFresh?.Invoke(head) ?? true))
            throw new RpcException(-32001, "Authenticated optimistic execution head is stale or ahead of the local clock; waiting for consensus sync.");
        return head;
    }

    private async Task<(VerifiedHead Head, bool Historical)> ResolveStateHeadAsync(JsonElement block, CancellationToken cancellationToken, VerifiedHead? pinnedLatest = null)
    {
        block = ParseBlockSelector(block, false, out bool requireCanonical, out bool byHash);
        string selector = block.GetString()!;
        if (selector == "latest") return (pinnedLatest ?? await LatestHeadAsync(cancellationToken), false);
        if (byHash)
        {
            Hash256 requested = new(ReadParameterData(block, 32));
            VerifiedHead? optimistic = getLatestHead?.Invoke();
            if (optimistic is not null && requested == optimistic.BlockHash)
                return (optimistic, false);
            VerifiedHead hashAnchor = getHead();
            if (requested == hashAnchor.BlockHash) return (hashAnchor, false);
            return await HistoricalStateHashAsync(hashAnchor, optimistic, block, cancellationToken, requireCanonical);
        }
        if (selector == "finalized") return (FinalizedHead(), false);
        UInt256 number = ParseQuantity(block);
        VerifiedHead? latest = getLatestHead?.Invoke();
        if (latest is not null && number == (UInt256)latest.Number)
            return (latest, false);
        VerifiedHead finalized = getHead();
        if (number == (UInt256)finalized.Number) return (finalized, false);
        return await HistoricalStateHeadAsync(SelectHistoryAnchor(finalized, latest, number, HistoricalStateDepth), number, cancellationToken);
    }

    private async Task<(VerifiedHead Head, bool Historical)> HistoricalStateHashAsync(VerifiedHead finalized, VerifiedHead? latest, JsonElement hash,
        CancellationToken cancellationToken, bool requireCanonical = false)
    {
        await AcquireHistoricalStateAsync(cancellationToken);
        try
        {
            BlockHeader header = await ResolveDataHeaderAsync(hash, hashOnly: true, cancellationToken, finalized, stateHistory: true,
                pinnedLatest: latest, requireCanonical: requireCanonical)
                ?? throw new RpcException(-32001, "Requested authenticated block was not found.");
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
        if (!await _historicalStateRequests.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken))
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

    internal static bool TryReadLocalMetadata(string method, JsonElement parameters, ulong chainId, bool listening, out object result)
    {
        result = null!;
        if (method is not ("eth_chainId" or "net_version" or "web3_clientVersion" or "eth_accounts" or "net_listening")) return false;
        if (parameters.ValueKind != JsonValueKind.Undefined &&
            (parameters.ValueKind != JsonValueKind.Array || parameters.GetArrayLength() != 0)) throw InvalidParameters();
        result = method switch
        {
            "eth_chainId" => Quantity(chainId),
            "net_version" => chainId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "web3_clientVersion" => ProductInfo.ClientId,
            "eth_accounts" => Array.Empty<string>(),
            _ => listening,
        };
        return true;
    }

    internal static string Quantity(ulong value) => $"0x{value:x}";
    internal static string Quantity(UInt256 value) => value.ToHexString(skipLeadingZeros: true);
    private static RpcException InvalidParameters() => new(-32602, "Invalid verified RPC parameters.");

    public void Dispose()
    {
        _logResults.Dispose();
        _data?.Dispose();
        _historicalStateRequests.Dispose();
        _proofRequests.Dispose();
    }
}
