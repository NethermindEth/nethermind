// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Data;
using Nethermind.Serialization.Json;
using Nethermind.State.Proofs;
using Nethermind.Int256;
using Nethermind.Network;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.LightClient.Rpc;

internal sealed partial class RpcBackend(IExecutionStateSource execution, Func<VerifiedHead> getHead, ulong chainId,
    EvmExecutor? calls = null, ISpecProvider? specProvider = null, Func<VerifiedHead?>? getLatestHead = null,
    Func<VerifiedHead, bool>? isLatestHeadFresh = null, Func<VerifiedHead, bool>? isFinalizedHeadFresh = null,
    Func<CancellationToken, Task<VerifiedHead>>? waitForLatestHead = null, IBlockForRpcFactory? blockForRpcFactory = null,
    SendPolicyRelay? sendPolicy = null, Func<int>? getExecutionPeerCount = null, IForkInfo? forkInfo = null,
    Func<Task>? getNextHeadChange = null, TimeProvider? timeProvider = null)
    : IDisposable
{
    private const ulong HistoricalStateDepth = 128;
    private const ulong BlockHistoryDepth = 256;
    private const ulong TransactionSearchFinalizedDepth = 15;
    private const long LogResultCacheCapacity = 32 * 1024 * 1024;
    private readonly ulong _startingBlock = getLatestHead?.Invoke()?.Number ?? 0;
    private static readonly SemaphoreSlim ActiveExecutionData = new(4, 4);
    private readonly ExecutionData? _data = specProvider is null ? null
        : new(execution, specProvider, chainId, blockForRpcFactory ?? new BlockForRpcFactory(), sendPolicy);
    private readonly SemaphoreSlim _historicalStateRequests = new(2, 2);
    private readonly SemaphoreSlim _proofRequests = new(2, 2);
    private readonly MemoryCache _logResults = new(new MemoryCacheOptions { SizeLimit = LogResultCacheCapacity });
    internal RpcFilters Filters { get; } = new();

    internal SerializedJson GetHeader(BlockHeader header) => _data!.GetHeader(header);

    internal Task<SerializedJson> GetSubscriptionLogsAsync(BlockHeader header, Func<Address, Hash256[], bool> matches,
        CancellationToken cancellationToken) => _data!.GetLogsAsync([header], null, [], cancellationToken, matches);

    internal ulong ChainId => chainId;
    internal int PeerCount => getExecutionPeerCount?.Invoke() ?? 0;

    internal SyncingResult GetSyncing()
    {
        VerifiedHead? current = getLatestHead?.Invoke();
        if (current is not null && (isLatestHeadFresh?.Invoke(current) ?? true)) return SyncingResult.NotSyncing;
        return new() { IsSyncing = true, StartingBlock = _startingBlock, CurrentBlock = current?.Number ?? 0, HighestBlock = current?.Number ?? 0 };
    }

    internal Task<ulong> GetBlockNumberAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(getLatestHead?.Invoke()?.Number
            ?? throw new RpcException(-32001, "An authenticated optimistic execution head is not available."));
    }

    internal async Task<UInt256> GetBlobBaseFeeAsync(CancellationToken cancellationToken)
    {
        if (specProvider is null) throw new RpcException(-32601, "Verified blob fees are unavailable.");
        VerifiedHead latest = await LatestHeadAsync(cancellationToken);
        using DataRequest admission = new(cancellationToken);
        BlockHeader header = await execution.GetHeaderAsync(latest, cancellationToken);
        IReleaseSpec spec = specProvider.GetSpec(header);
        if (!spec.IsEip4844Enabled || header.ExcessBlobGas is not { } excess)
            throw new RpcException(-32001, "Blob fees are unavailable for this authenticated execution head.");
        if (!BlobGasCalculator.TryCalculateFeePerBlobGas(excess, spec.BlobBaseFeeUpdateFraction, out UInt256 fee))
            throw new RpcException(-32000, "Authenticated blob fee exceeds the supported integer range.");
        return fee;
    }

    internal async Task<Hash256> SendRawTransactionAsync(byte[] raw, CancellationToken cancellationToken)
    {
        if (_data is null) throw new RpcException(-32601, "Verified transaction relay is unavailable.");
        if (raw.Length > 2 * 1024 * 1024) throw InvalidParameters();
        return await _data.SendRawTransactionAsync(await LatestHeadAsync(cancellationToken), raw, calls, cancellationToken);
    }

    internal Task<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> SimulateAsync(JsonElement payload, JsonElement block,
        CancellationToken cancellationToken) =>
        WithCallHeadAsync(UseLatestForPending(block),
            (executor, head) => executor.SimulateAsync(head, payload, cancellationToken), cancellationToken);

    internal async Task<UInt256> GetBalanceAsync(Address address, JsonElement block, CancellationToken cancellationToken) =>
        (await GetAccountAsync(address, block, cancellationToken)).Balance;

    internal async Task<ulong> GetNonceAsync(Address address, JsonElement block, CancellationToken cancellationToken)
    {
        bool pending = IsPendingTag(block);
        Account account = await GetAccountAsync(address, pending ? default : block, cancellationToken);
        return pending ? execution.GetPendingAccount(address, account).Nonce : account.Nonce;
    }

    internal async Task<byte[]> GetCodeAsync(Address address, JsonElement block, CancellationToken cancellationToken) =>
        (await GetAccountInfoAsync(address, block, cancellationToken)).Code;

    internal async Task<AccountInfoForRpc> GetAccountInfoAsync(Address address, JsonElement block, CancellationToken cancellationToken)
    {
        (VerifiedHead head, bool historical) = block.ValueKind == JsonValueKind.Undefined
            ? (await LatestHeadAsync(cancellationToken), false) : await ResolveStateHeadAsync(block, cancellationToken);
        try
        {
            Account account = await execution.GetAccountAsync(head, address, cancellationToken);
            return new AccountInfoForRpc
            {
                Balance = account.Balance,
                Nonce = account.Nonce,
                Code = await execution.GetCodeAsync(account, cancellationToken),
            };
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException or RlpReaderException)
        { throw new RpcException(-32000, "Execution peers returned an invalid or unavailable proof."); }
        finally { if (historical) _historicalStateRequests.Release(); }
    }

    internal async Task<byte[]> GetStorageAsync(Address address, UInt256 key, JsonElement block, CancellationToken cancellationToken)
    {
        // Keep the historical request lease while its account and storage proofs are fetched.
        (VerifiedHead head, bool historical) = block.ValueKind == JsonValueKind.Undefined
            ? (await LatestHeadAsync(cancellationToken), false) : await ResolveStateHeadAsync(block, cancellationToken);
        try
        {
            Account account = await execution.GetAccountAsync(head, address, cancellationToken);
            return (await execution.GetStorageAsync(head, address, account, key, cancellationToken)).ToBigEndian();
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException or RlpReaderException)
        { throw new RpcException(-32000, "Execution peers returned an invalid or unavailable proof."); }
        finally { if (historical) _historicalStateRequests.Release(); }
    }

    private async Task<Account> GetAccountAsync(Address address, JsonElement block, CancellationToken cancellationToken)
    {
        (VerifiedHead head, bool historical) = block.ValueKind == JsonValueKind.Undefined
            ? (await LatestHeadAsync(cancellationToken), false) : await ResolveStateHeadAsync(block, cancellationToken);
        try { return await execution.GetAccountAsync(head, address, cancellationToken); }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException or RlpReaderException)
        { throw new RpcException(-32000, "Execution peers returned an invalid or unavailable proof."); }
        finally { if (historical) _historicalStateRequests.Release(); }
    }

    internal async Task<AccountProof> GetProofAsync(Address address, StorageIndex[] storageKeys, JsonElement block, CancellationToken cancellationToken)
    {
        if (storageKeys.Length > 64) throw InvalidParameters();
        UInt256[] keys = new UInt256[storageKeys.Length];
        for (int i = 0; i < keys.Length; i++) keys[i] = storageKeys[i].Value;
        if (block.ValueKind != JsonValueKind.Undefined) _ = ParseBlockSelector(block, false, out _, out _);
        VerifiedHead? latest = await LatestForSelectorAsync(block, cancellationToken);
        if (!await _proofRequests.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken))
            throw new RpcException(-32005, "Too many concurrent account proof requests.");
        try
        {
            using DataRequest admission = new(cancellationToken);
            (VerifiedHead head, bool historical) = block.ValueKind == JsonValueKind.Undefined
                ? (latest!, false) : await ResolveStateHeadAsync(block, cancellationToken, latest);
            try
            {
                ExecutionAccountProof proof = await execution.GetProofAsync(head, address, keys, cancellationToken);
                if (ExecutionProofVerifier.VerifyAccount(head.StateRoot, address, proof.AccountProof) != proof.Account
                    || proof.StorageProofs.Length != keys.Length) throw new InvalidDataException("Invalid exported account proof.");
                StorageProof[] storage = new StorageProof[keys.Length];
                for (int i = 0; i < storage.Length; i++)
                {
                    ExecutionStorageProof slot = proof.StorageProofs[i];
                    if (slot.Key != keys[i] || ExecutionProofVerifier.VerifyStorage(proof.Account.StorageRoot, slot.Key, slot.Proof) != slot.Value)
                        throw new InvalidDataException("Invalid exported storage proof.");
                    storage[i] = new() { Key = slot.Key.ToBigEndian(), Value = slot.Value.ToBigEndian(), Proof = slot.Proof };
                }
                return new()
                {
                    Address = address,
                    Balance = proof.Account.Balance,
                    Nonce = proof.Account.Nonce,
                    CodeHash = proof.Account.CodeHash,
                    StorageRoot = proof.Account.StorageRoot,
                    Proof = proof.AccountProof,
                    StorageProofs = storage
                };
            }
            catch (Exception exception) when (exception is InvalidDataException or RlpReaderException)
            { throw new RpcException(-32000, "Execution peers returned invalid or unavailable path proofs."); }
            finally { if (historical) _historicalStateRequests.Release(); }
        }
        finally { _proofRequests.Release(); }
    }

    internal Task<JsonElement> GetFeeHistoryAsync(UInt256 requested, JsonElement block, double[] percentiles, CancellationToken cancellationToken)
    {
        if (requested < 1) throw InvalidParameters();
        return GetFeeHistoryCoreAsync((int)UInt256.Min(requested, 16), block, percentiles, cancellationToken);
    }

    internal async Task<UInt256> GetGasPriceAsync(bool priorityOnly, CancellationToken cancellationToken)
    {
        JsonElement history = await GetFeeHistoryCoreAsync(1, default, [60], cancellationToken);
        UInt256 tip = UInt256.Max(UInt256.One, ParseQuantity(history.GetProperty("reward")[0][0]));
        if (priorityOnly) return tip;
        UInt256 baseFee = ParseQuantity(history.GetProperty("baseFeePerGas")[1]);
        return UInt256.AddOverflow(baseFee, tip, out UInt256 price) ? UInt256.MaxValue : price;
    }

    private async Task<JsonElement> GetFeeHistoryCoreAsync(int blocks, JsonElement selector, double[] percentiles, CancellationToken cancellationToken)
    {
        if (_data is null) throw new RpcException(-32601, "Verified execution data is unavailable.");
        if (percentiles.Length > 100) throw InvalidParameters();
        for (int i = 0; i < percentiles.Length; i++)
            if (!double.IsFinite(percentiles[i]) || percentiles[i] < 0 || percentiles[i] > 100 || i != 0 && percentiles[i] < percentiles[i - 1]) throw InvalidParameters();
        bool requireCanonical = false;
        bool byHash = false;
        if (selector.ValueKind != JsonValueKind.Undefined) selector = ParseBlockSelector(selector, false, out requireCanonical, out byHash);
        VerifiedHead? latest = await LatestForSelectorAsync(selector, cancellationToken);
        using DataRequest admission = new(cancellationToken);
        BlockHeader newest = selector.ValueKind != JsonValueKind.Undefined
            ? await ResolveDataHeaderAsync(selector, byHash, cancellationToken, latest, requireCanonical: requireCanonical)
                ?? throw new RpcException(-32001, "Requested authenticated fee history is unavailable.")
            : await execution.GetHeaderAsync(latest!, cancellationToken);
        blocks = checked((int)Math.Min((ulong)blocks, newest.Number + 1));
        BlockHeader[] headers = await execution.GetRecentHeadersAsync(newest, blocks, cancellationToken);
        return await _data.GetFeeHistoryAsync(headers, percentiles.Length == 0 ? null : percentiles, cancellationToken);
    }

    internal async Task<SerializedJson> GetTransactionByHashAsync(Hash256 hash, ExecutionData.TransactionResult result, CancellationToken cancellationToken) =>
        await FindRecentTransactionAsync(hash, (data, header, token) => data.FindTransactionAsync(header, hash, result, token), cancellationToken)
        ?? BoundedJson.Null;

    private async Task<T?> FindRecentTransactionAsync<T>(Hash256 hash,
        Func<ExecutionData, BlockHeader, CancellationToken, Task<T?>> find, CancellationToken cancellationToken) where T : struct
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
                if (await find(_data, canonical, cancellationToken) is { } known) return known;
            }
            catch (RpcException exception) when (exception.Code == -32001)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        VerifiedHead finalized = getHead();
        if (getLatestHead?.Invoke() is { } latest)
        {
            BlockHeader header = await execution.GetHeaderAsync(latest, cancellationToken);
            visited.Add(header.Hash!);
            if (await find(_data, header, cancellationToken) is { } optimistic) return optimistic;
            ulong oldest = Math.Min(latest.Number, finalized.Number.SaturatingSub(TransactionSearchFinalizedDepth));
            int count = checked((int)Math.Min(BlockHistoryDepth + 1, latest.Number - oldest + 1));
            BlockHeader[] optimisticHeaders = await execution.GetRecentHeadersAsync(header, count, cancellationToken);
            for (int i = optimisticHeaders.Length - 2; i >= 0; i--)
            {
                visited.Add(optimisticHeaders[i].Hash!);
                if (await find(_data, optimisticHeaders[i], cancellationToken) is { } recent) return recent;
            }
        }
        BlockHeader newest = await execution.GetHeaderAsync(finalized, cancellationToken);
        if (visited.Add(newest.Hash!) && await find(_data, newest, cancellationToken) is { } current) return current;
        BlockHeader[] headers = await execution.GetRecentHeadersAsync(newest,
            checked((int)(Math.Min(TransactionSearchFinalizedDepth, finalized.Number) + 1)), cancellationToken);
        for (int i = headers.Length - 2; i >= 0; i--)
            if (visited.Add(headers[i].Hash!) && await find(_data, headers[i], cancellationToken) is { } found) return found;
        return null;
    }

    private async Task<BlockHeader> ResolveIndexedTransactionHeaderAsync(BlockHeader indexed, CancellationToken cancellationToken)
    {
        VerifiedHead? latest = getLatestHead?.Invoke();
        if (latest is not null && indexed.Hash == latest.BlockHash) return await execution.GetHeaderAsync(latest, cancellationToken);
        VerifiedHead finalized = getHead();
        VerifiedHead anchor = indexed.Number <= finalized.Number ? finalized
            : latest ?? throw new RpcException(-32001, "The indexed transaction is outside the authenticated chain.");
        if (indexed.Number > anchor.Number || anchor.Number - indexed.Number > BlockHistoryDepth)
            throw new RpcException(-32001, "The indexed transaction is outside the authenticated history window.");
        BlockHeader canonical = await execution.GetCanonicalHeaderAsync(anchor, indexed.Number, cancellationToken);
        if (canonical.Hash != indexed.Hash)
            throw new RpcException(-32001, "The indexed transaction's block is no longer canonical.");
        return canonical;
    }

    internal Task<SerializedJson> GetBlockAsync(JsonElement selector, bool hashOnly, bool fullTransactions, CancellationToken token) =>
        GetDataAsync(selector, hashOnly, DataKind.Block, fullTransactions, UInt256.Zero, token);
    internal Task<SerializedJson> GetBlockTransactionCountAsync(JsonElement selector, bool hashOnly, CancellationToken token) =>
        GetDataAsync(selector, hashOnly, DataKind.TransactionCount, false, UInt256.Zero, token);
    internal Task<SerializedJson> GetTransactionAsync(JsonElement selector, bool hashOnly, UInt256 index, CancellationToken token) =>
        GetDataAsync(selector, hashOnly, DataKind.Transaction, false, index, token);
    internal Task<SerializedJson> GetBlockReceiptsAsync(JsonElement selector, CancellationToken token) =>
        GetDataAsync(selector, false, DataKind.Receipts, false, UInt256.Zero, token);
    internal Task<SerializedJson> GetLogsDataAsync(JsonElement filter, CancellationToken token) =>
        GetDataAsync(filter, false, DataKind.Logs, false, UInt256.Zero, token);

    private enum DataKind { Block, Header, BlockAccessList, TransactionCount, Transaction, RawTransaction, Receipts, Logs }

    private async Task<SerializedJson> GetDataAsync(JsonElement selector, bool hashOnly, DataKind kind, bool fullTransactions,
        UInt256 index, CancellationToken cancellationToken)
    {
        if (_data is null) throw new RpcException(-32601, "Verified execution data is unavailable.");
        CancellationToken callerCancellation = cancellationToken;
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        cancellationToken = deadline.Token;
        try
        {
            if (kind == DataKind.Logs)
            {
                return await GetLogsAsync(selector, cancellationToken);
            }
            if (kind is DataKind.Header or DataKind.BlockAccessList && IsPendingTag(selector)) return BoundedJson.Null;
            selector = ParseBlockSelector(selector, hashOnly, out bool requireCanonical, out bool byHash);
            VerifiedHead? latest = await LatestForSelectorAsync(selector, cancellationToken);
            using DataRequest admission = new(cancellationToken);
            BlockHeader? header = await ResolveDataHeaderAsync(selector, byHash, cancellationToken, latest, requireCanonical: requireCanonical);
            if (header is null) return BoundedJson.Null;
            return kind switch
            {
                DataKind.Block => await _data.GetBlockAsync(header, fullTransactions, cancellationToken),
                DataKind.Header => _data.GetHeader(header),
                DataKind.BlockAccessList => await _data.GetBlockAccessListAsync(header, cancellationToken),
                DataKind.TransactionCount => await _data.GetBlockTransactionCountAsync(header, cancellationToken),
                DataKind.Receipts => await _data.GetBlockReceiptsAsync(header, cancellationToken),
                _ when index > int.MaxValue => BoundedJson.Null,
                _ => await _data.GetTransactionAsync(header, (int)index, cancellationToken, raw: kind == DataKind.RawTransaction),
            };
        }
        catch (Exception exception) when (exception is InvalidDataException or RlpReaderException or OverflowException)
        { throw new RpcException(-32000, "Execution peers returned invalid or unavailable authenticated block data."); }
        catch (ExecutionDataUnavailableException)
        { throw new RpcException(-32001, "Requested authenticated block data is unavailable."); }
        catch (OperationCanceledException) when (!callerCancellation.IsCancellationRequested)
        { throw new RpcException(-32000, "Verified execution data request exceeded the 90-second deadline."); }
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
                if (!property.NameEquals("blockHash"u8) && !property.NameEquals("blockNumber"u8) && !property.NameEquals("requireCanonical"u8))
                    throw InvalidParameters();
            if (selector.TryGetProperty("requireCanonical", out JsonElement requiredCanonical)
                && (!hasHash || requiredCanonical.ValueKind is not (JsonValueKind.True or JsonValueKind.False))) throw InvalidParameters();
            if (hasNumber) _ = ParseQuantity(number);
            requireCanonical = requiredCanonical.ValueKind == JsonValueKind.True;
            selector = hasHash ? hash : number;
            hashOnly = hasHash;
            byNumber = hasNumber;
        }
        if (selector.ValueKind != JsonValueKind.String) throw InvalidParameters();
        ReadOnlySpan<byte> raw = JsonMarshal.GetRawUtf8Value(selector);
        bool hashLength = raw.IndexOf((byte)'\\') >= 0 ? selector.GetString()!.Length == 66 : raw.Length == 68;
        byHash = hashOnly || !byNumber && hashLength;
        if (byHash) _ = ReadParameterData(selector, 32);
        else if (!selector.ValueEquals("latest"u8) && !IsFinalizedTag(selector))
        {
            RejectUnavailableTag(selector);
            _ = ParseQuantity(selector);
        }
        return selector;
    }

    private async Task<BlockHeader?> ResolveDataHeaderAsync(JsonElement selector, bool hashOnly,
        CancellationToken cancellationToken, VerifiedHead? latest, VerifiedHead? pinnedFinalized = null,
        bool stateHistory = false, bool requireCanonical = false)
    {
        if (!hashOnly && selector.ValueEquals("latest"u8))
            return await execution.GetHeaderAsync(latest ?? await LatestHeadAsync(cancellationToken), cancellationToken);
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
            VerifiedHead anchor = SelectHistoryAnchor(finalizedHead, latest, candidate.Number, stateHistory);
            BlockHeader canonical = await execution.GetCanonicalHeaderAsync(anchor, candidate.Number, cancellationToken);
            if (canonical.Number != candidate.Number) throw new InvalidDataException("Canonical header has an unexpected block number.");
            if (canonical.Hash != requested)
            {
                if (requireCanonical) throw new RpcException(-32000, "Block is not in the selected authenticated chain.");
                return null;
            }
            return canonical;
        }
        if (IsFinalizedTag(selector)) return await execution.GetHeaderAsync(FinalizedHead(pinnedFinalized), cancellationToken);
        UInt256 parsed = ParseQuantity(selector);
        if (latest is not null && parsed == (UInt256)latest.Number)
            return await execution.GetHeaderAsync(latest, cancellationToken);
        VerifiedHead head = pinnedFinalized ?? getHead();
        if (parsed > (UInt256)(latest ?? head).Number) return null;
        VerifiedHead numberAnchor = SelectHistoryAnchor(head, latest, parsed, stateHistory);
        return await execution.GetCanonicalHeaderAsync(numberAnchor, (ulong)parsed, cancellationToken);
    }

    internal sealed record LogQuery(Hash256? BlockHash, ulong? First, ulong? Last, bool FromFinalized, bool ToFinalized,
        HashSet<Address>? Addresses, List<HashSet<Hash256>?> Topics)
    {
        internal bool Matches(BlockHeader header) => BlockHash is not null
            ? header.Hash == BlockHash
            : header.Number >= (First ?? 0) && header.Number <= (Last ?? ulong.MaxValue);
    }

    internal static LogQuery ParseLogQuery(JsonElement filter)
    {
        if (filter.ValueKind != JsonValueKind.Object) throw InvalidParameters();
        HashSet<Address>? addresses = null;
        List<HashSet<Hash256>?> topics = [];
        JsonElement blockHash = default;
        JsonElement from = default;
        JsonElement to = default;
        foreach (JsonProperty property in filter.EnumerateObject())
        {
            if (property.NameEquals("blockHash"u8)) blockHash = property.Value;
            else if (property.NameEquals("fromBlock"u8)) from = property.Value;
            else if (property.NameEquals("toBlock"u8)) to = property.Value;
            else if (property.NameEquals("address"u8)) addresses = property.Value.ValueKind == JsonValueKind.Null ? null : ParseAddresses(property.Value);
            else if (property.NameEquals("topics"u8)) topics = property.Value.ValueKind == JsonValueKind.Null ? [] : ParseTopics(property.Value);
            else throw InvalidParameters();
        }
        if (blockHash.ValueKind != JsonValueKind.Undefined)
        {
            if (from.ValueKind != JsonValueKind.Undefined || to.ValueKind != JsonValueKind.Undefined) throw InvalidParameters();
            return new(new Hash256(ReadParameterData(blockHash, 32)), null, null, false, false, addresses, topics);
        }
        ulong? first = ParseLogBlockNumber(from);
        ulong? last = ParseLogBlockNumber(to);
        if (first > last) throw InvalidParameters();
        return new(null, first, last, IsFinalizedTag(from), IsFinalizedTag(to), addresses, topics);
    }

    private Task<SerializedJson> GetLogsAsync(JsonElement filter, CancellationToken cancellationToken) =>
        GetLogsAsync(ParseLogQuery(filter), cancellationToken);

    internal async Task<SerializedJson> GetLogsAsync(LogQuery query, CancellationToken cancellationToken)
    {
        if (_data is null) throw new RpcException(-32601, "Verified execution data is unavailable.");
        if (query.BlockHash is { } requested)
        {
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
            return await _data.GetLogsAsync([one], query.Addresses, query.Topics, cancellationToken);
        }
        bool latestFrom = query.First is null && !query.FromFinalized;
        bool latestTo = query.Last is null && !query.ToFinalized;
        VerifiedHead? current = latestFrom || latestTo ? await LatestHeadAsync(cancellationToken) : getLatestHead?.Invoke();
        using DataRequest logAdmission = new(cancellationToken);
        bool optimisticFrom = latestFrom || current is not null && query.First == current.Number;
        bool optimisticTo = latestTo || current is not null && query.Last == current.Number;
        if (optimisticFrom && optimisticTo)
        {
            BlockHeader latest = await execution.GetHeaderAsync(current!, cancellationToken);
            return await _data.GetLogsAsync([latest], query.Addresses, query.Topics, cancellationToken);
        }
        VerifiedHead finalized = getHead();
        if (query.FromFinalized || query.ToFinalized) finalized = FinalizedHead(finalized);
        ulong first = query.First ?? (query.FromFinalized ? finalized.Number : current!.Number);
        ulong last = query.Last ?? (query.ToFinalized ? finalized.Number : current!.Number);
        if (first > last) throw InvalidParameters();
        VerifiedHead anchor = optimisticFrom || optimisticTo || last > finalized.Number ? current ?? finalized : finalized;
        if (first > anchor.Number || last > anchor.Number) throw InvalidParameters();
        BlockHeader[] headers = await execution.GetLogHeadersAsync(anchor, finalized, first, last, cancellationToken);
        if (last > finalized.Number)
            return await _data.GetLogsAsync(headers, query.Addresses, query.Topics, cancellationToken);

        (ulong First, ulong Last, ValueHash256 EndHash, ValueHash256 Filter) key =
            (first, last, headers[^1].Hash!.ValueHash256, LogFilterHash(query.Addresses, query.Topics));
        cancellationToken.ThrowIfCancellationRequested();
        if (_logResults.TryGetValue(key, out SerializedJson cached)) return cached;

        SerializedJson result = await _data.GetLogsAsync(headers, query.Addresses, query.Topics, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // Account for cache metadata along with the retained result bytes.
        long size = 1024L + result.Utf8Json.Length;
        if (size <= LogResultCacheCapacity)
            _logResults.Set(key, result, new MemoryCacheEntryOptions { Size = size });
        return result;
    }

    private readonly struct DataRequest : IDisposable
    {
        internal DataRequest(CancellationToken cancellationToken)
        {
            if (!ActiveExecutionData.Wait(0, cancellationToken))
                throw new RpcException(-32005, "Four verified execution data requests are already active.");
        }

        public void Dispose() => ActiveExecutionData.Release();
    }

    private static ValueHash256 LogFilterHash(HashSet<Address>? addresses, IReadOnlyList<HashSet<Hash256>?> topics)
    {
        KeccakHash filter = KeccakHash.Create();
        AppendCount(filter, addresses?.Count ?? 0);
        if (addresses is not null)
        {
            Address[] sorted = [.. addresses];
            Array.Sort(sorted);
            foreach (Address address in sorted) filter.Update(address.Bytes);
        }
        AppendCount(filter, topics.Count);
        foreach (HashSet<Hash256>? choices in topics)
        {
            AppendCount(filter, choices?.Count ?? 0);
            if (choices is null) continue;
            Hash256[] sorted = [.. choices];
            Array.Sort(sorted);
            foreach (Hash256 topic in sorted) filter.Update(topic.Bytes);
        }
        Span<byte> hash = stackalloc byte[Keccak.Size];
        filter.UpdateFinalTo(hash);
        return new(hash);

        static void AppendCount(KeccakHash filter, int count)
        {
            Span<byte> encoded = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(encoded, count);
            filter.Update(encoded);
        }
    }

    private VerifiedHead FinalizedHead(VerifiedHead? pinned = null)
    {
        VerifiedHead head = pinned ?? getHead();
        if (!(isFinalizedHeadFresh?.Invoke(head) ?? true))
            throw new RpcException(-32001, "Verified finality is more than one hour old; waiting for consensus sync.");
        return head;
    }

    private static bool IsFinalizedTag(JsonElement selector) =>
        selector.ValueKind == JsonValueKind.String && selector.ValueEquals("finalized"u8);

    private static bool IsPendingTag(JsonElement selector) =>
        selector.ValueKind == JsonValueKind.String && selector.ValueEquals("pending"u8);

    private static ulong? ParseLogBlockNumber(JsonElement selector)
    {
        if (selector.ValueKind == JsonValueKind.Undefined) return null;
        if (selector.ValueKind == JsonValueKind.String)
        {
            if (selector.ValueEquals("latest"u8) || IsFinalizedTag(selector)) return null;
            RejectUnavailableTag(selector);
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
        if (block.ValueEquals("latest"u8)) return (pinnedLatest ?? await LatestHeadAsync(cancellationToken), false);
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
        if (IsFinalizedTag(block)) return (FinalizedHead(), false);
        UInt256 number = ParseQuantity(block);
        VerifiedHead? latest = getLatestHead?.Invoke();
        if (latest is not null && number == (UInt256)latest.Number)
            return (latest, false);
        VerifiedHead finalized = getHead();
        if (number == (UInt256)finalized.Number) return (finalized, false);
        return await HistoricalStateHeadAsync(SelectHistoryAnchor(finalized, latest, number, stateHistory: true), number, cancellationToken);
    }

    private async Task<(VerifiedHead Head, bool Historical)> HistoricalStateHashAsync(VerifiedHead finalized, VerifiedHead? latest, JsonElement hash,
        CancellationToken cancellationToken, bool requireCanonical = false)
    {
        await AcquireHistoricalStateAsync(cancellationToken);
        try
        {
            BlockHeader header = await ResolveDataHeaderAsync(hash, hashOnly: true, cancellationToken, latest, finalized,
                stateHistory: true, requireCanonical: requireCanonical)
                ?? throw new RpcException(-32001, "Requested authenticated block was not found.");
            VerifiedHead anchor = SelectHistoryAnchor(finalized, latest, header.Number, stateHistory: true);
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

    private static VerifiedHead SelectHistoryAnchor(VerifiedHead finalized, VerifiedHead? latest, UInt256 number, bool stateHistory)
    {
        VerifiedHead anchor = number > (UInt256)finalized.Number && latest is not null ? latest : finalized;
        ulong tip = stateHistory && latest is not null ? Math.Max(anchor.Number, latest.Number) : anchor.Number;
        ulong depth = stateHistory ? HistoricalStateDepth : BlockHistoryDepth;
        if (number > (UInt256)anchor.Number || tip - (ulong)number > depth)
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

    private static UInt256 ParseQuantity(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String) throw InvalidParameters();
        string value = element.GetString()!;
        if (!value.StartsWith("0x", StringComparison.Ordinal) || value.Length is < 3 or > 66 ||
            value.Length > 3 && value[2] == '0')
            throw InvalidParameters();
        try
        {
            string digits = value[2..];
            return new UInt256(Convert.FromHexString(digits.Length % 2 == 0 ? digits : "0" + digits), isBigEndian: true);
        }
        catch (FormatException) { throw InvalidParameters(); }
    }

    private static void RejectUnavailableTag(JsonElement selector)
    {
        if (IsPendingTag(selector)) throw new RpcException(-32001, "Pending state cannot be authenticated from execution peers.");
        if (selector.ValueEquals("safe"u8)) throw new RpcException(-32001, "A distinct authenticated safe execution head is unavailable; select finalized or latest explicitly.");
        if (selector.ValueEquals("earliest"u8)) throw new RpcException(-32001, "Genesis is outside the bounded authenticated history; use a recent block selector.");
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
        _sendReceiptRequests.Dispose();
    }
}
