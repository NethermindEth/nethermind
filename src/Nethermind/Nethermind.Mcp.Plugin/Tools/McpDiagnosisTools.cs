// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Numerics;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.TxPool;

namespace Nethermind.Mcp.Plugin.Tools;

/// <summary>Explains the state of a transaction or an account's transactions on this node.</summary>
[McpServerToolType]
internal sealed class McpDiagnosisTools(McpToolExecutor executor, IMcpConfig config, McpNodeCapabilities capabilities, ITxPool? txPool = null) : IMcpToolSet
{
    private const int MaxTransactions = 50;
    private const string ToolName = "diagnose_transaction";

    /// <inheritdoc/>
    public IEnumerable<McpServerTool> CreateServerTools() => McpToolFactory.Create(this, _ => string.Empty, config.MaxResultSize);

    /// <summary>Diagnoses a mined or pending transaction, or the pending transactions of one sender.</summary>
    [McpServerTool(Name = ToolName, Title = "Diagnose transaction", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [McpToolOutputSchema("""
        {
          "type":"object","required":["result"],"properties":{"result":{"$ref":"#/$defs/diagnosis"}},
          "$defs":{
            "diagnosis":{
              "type":"object",
              "required":["status","summary","recommendations","canSend"],
              "properties":{
                "status":{"type":"string","enum":["mined","pending_ready","pending_queued","pending_blocked","underpriced","blob_underpriced","nonce_too_low","replaced","not_found","fee_unavailable","address","txpool_unavailable"]},
                "summary":{"type":"string"},
                "ownFeeStatus":{"type":"string","enum":["pending_ready","underpriced","blob_underpriced","fee_unavailable"],"description":"This transaction's fees independently of preceding transactions and nonce gaps."},
                "recommendations":{"type":"array","items":{"type":"string"}},
                "canSend":{"type":"boolean"},
                "hash":{"type":["string","null"]},
                "replacementHash":{"type":"string"},
                "address":{"type":"string"},
                "latestNonce":{"type":"string"},
                "pendingNonce":{"type":"string"},
                "nonce":{"type":"string"},
                "type":{"type":"integer"},
                "blockNumber":{"type":"integer"},
                "transactionIndex":{"type":"integer"},
                "succeeded":{"type":["boolean","null"]},
                "transactions":{"type":"array","items":{"$ref":"#/$defs/diagnosis"}},
                "omitted":{"type":"integer"},
                "stale":{"type":"integer","description":"Pool entries with a nonce below latest, excluded from pending and queued counts."},
                "blockedBy":{"type":"object","required":["nonce","hash","reason"],"properties":{"nonce":{"type":"string"},"hash":{"type":["string","null"]},"reason":{"type":"string","enum":["underpriced","blob_underpriced"]}}},
                "nonceGaps":{"type":"array","items":{"type":"object","required":["from","to"],"properties":{"from":{"type":"string"},"to":{"type":"string"}}}},
                "nonceGapsOmitted":{"type":"integer"},
                "poolAvailable":{"type":"boolean"},
                "fees":{"$ref":"#/$defs/fees"},
                "replacementMinimum":{"$ref":"#/$defs/fees"},
                "recommended":{"$ref":"#/$defs/fees"},
                "recentTips":{"type":"object","required":["p25","p50","p75"],"properties":{"p25":{"type":"object","required":["wei","gwei"],"properties":{"wei":{"type":["string","null"]},"gwei":{"type":["string","null"]}}},"p50":{"type":"object","required":["wei","gwei"],"properties":{"wei":{"type":["string","null"]},"gwei":{"type":["string","null"]}}},"p75":{"type":"object","required":["wei","gwei"],"properties":{"wei":{"type":["string","null"]},"gwei":{"type":["string","null"]}}}}},
                "lowTip":{"type":["boolean","null"]},
                "notes":{"type":"array","items":{"type":"string"}}
              }
            },
            "fees":{
              "type":["object","null"],
              "properties":{
                "gasPriceWei":{"type":["string","null"]},
                "gasPriceGwei":{"type":["string","null"]},
                "maxFeePerGasWei":{"type":["string","null"]},
                "maxFeePerGasGwei":{"type":["string","null"]},
                "maxPriorityFeePerGasWei":{"type":["string","null"]},
                "maxPriorityFeePerGasGwei":{"type":["string","null"]},
                "maxFeePerBlobGasWei":{"type":["string","null"]},
                "maxFeePerBlobGasGwei":{"type":["string","null"]},
                "nextBaseFeeWei":{"type":["string","null"]},
                "nextBaseFeeGwei":{"type":["string","null"]},
                "effectiveTipWei":{"type":["string","null"]},
                "effectiveTipGwei":{"type":["string","null"]},
                "blobBaseFeeWei":{"type":["string","null"]},
                "blobBaseFeeGwei":{"type":["string","null"]},
                "blobBaseFeeIsNext":{"type":"boolean"},
                "minimumBlobCount":{"type":["integer","null"]}
              }
            }
          }
        }
        """)]
    [Description("Diagnoses this node's transactions using the canonical chain, ordinary and blob pools, nonces, and recent fees. " +
        "Give exactly one of hash or address. Status is mined, pending_ready, pending_queued, pending_blocked, underpriced, blob_underpriced, " +
        "nonce_too_low, replaced, not_found, fee_unavailable, address or txpool_unavailable. Low tips are advisory, not an inclusion floor. " +
        "pending_blocked names an earlier underpriced transaction that must execute first. ownFeeStatus and nonceGaps retain each transaction's own issues even when blocked. Address summaries count listed statuses and give the omitted count; stale totals cover the whole sender pool. " +
        "replacementMinimum is the pool rule; recommended adds next-block fee headroom. This tool cannot sign or send anything.")]
    public Task<CallToolResult> DiagnoseTransaction(
        [Description("Transaction hash; give this or address, not both.")] string? hash = null,
        [Description("Sender address; give this or hash, not both.")] string? address = null,
        CancellationToken cancellationToken = default)
    {
        if ((hash is null) == (address is null)) return McpEthHelpers.InvalidInput("Give exactly one of 'hash' or 'address'.");
        if (hash is not null && !McpToolInput.TryParseHash(hash, nameof(hash), out _, out string? hashError))
            return McpEthHelpers.InvalidInput(hashError);
        if (address is not null && !McpToolInput.TryParseAddress(address, nameof(address), out _, out string? addressError))
            return McpEthHelpers.InvalidInput(addressError);

        return executor.ExecuteAsync(ToolName, nameof(IEthRpcModule.eth_getTransactionByHash), async (eth, token) =>
        {
            Hash256? txHash = hash is null ? null : new Hash256(hash);
            LegacyTransactionForRpc? transaction = null;
            if (txHash is not null)
            {
                using ResultWrapper<TransactionForRpc?> found = eth.eth_getTransactionByHash(txHash);
                if (found.Result.ResultType != ResultType.Success) return executor.Failure(ToolName, found);
                if (found.Data?.BlockNumber is not null) return executor.Success(Mined(eth, found.Data, txHash));
                if (found.Data is null)
                {
                    JsonObject missing = NewResult("not_found", $"{Short(txHash)} (nonce unknown) is not mined or known to this node. It may never have reached this node, or it was dropped. If you sped up or cancelled it, run diagnose_transaction with your address to see the replacement." + capabilities.DescribeTransactionHistoryLimit());
                    missing["hash"] = txHash.ToString();
                    ((JsonArray)missing["recommendations"]!).Insert(0, "Check the network and hash, then rebroadcast through your wallet if the transaction is still wanted.");
                    return executor.Success(missing);
                }
                transaction = found.Data as LegacyTransactionForRpc;
                if (transaction?.From is null)
                    return McpToolExecutor.Error(McpToolErrorCodes.Unavailable, "The pending transaction's sender or fee fields are unavailable; inspect get_transaction or provide its sender address.");
            }

            Address sender = transaction?.From ?? new Address(address!);
            using ResultWrapper<UInt256> latestResult = await eth.eth_getTransactionCount(sender, BlockParameter.Latest);
            if (latestResult.Result.ResultType != ResultType.Success) return executor.Failure(ToolName, latestResult);
            using ResultWrapper<UInt256> pendingResult = await eth.eth_getTransactionCount(sender, BlockParameter.Pending);
            if (pendingResult.Result.ResultType != ResultType.Success) return executor.Failure(ToolName, pendingResult);
            ulong latest = (ulong)latestResult.Data;
            JsonObject result = NewResult(address is null ? "pending_ready" : "address", string.Empty);
            result["address"] = McpEthHelpers.Checksum(sender);
            result["latestNonce"] = latestResult.Data.ToString();
            result["pendingNonce"] = pendingResult.Data.ToString();
            result["poolAvailable"] = txPool is not null;
            if (txHash is not null) result["hash"] = txHash.ToString();
            if (txPool is null)
            {
                result["status"] = "txpool_unavailable";
                result["summary"] = "The transaction pool service is unavailable on this node; account nonces are known, but pending transactions cannot be diagnosed. This tool cannot send transactions.";
                return executor.Success(result);
            }

            SortedDictionary<ulong, Transaction> all = [];
            Add(txPool.GetPendingTransactionsBySender(sender));
            Add(txPool.GetPendingLightBlobTransactionsBySender(sender));
            ulong firstMissing = latest;
            while (firstMissing < ulong.MaxValue && all.ContainsKey(firstMissing)) firstMissing++;
            if (transaction is not null)
            {
                if (transaction.Nonce < latest)
                {
                    using ResultWrapper<TransactionForRpc?> recheck = eth.eth_getTransactionByHash(txHash!);
                    if (recheck.Result.ResultType == ResultType.Success && recheck.Data?.BlockNumber is not null)
                        return executor.Success(Mined(eth, recheck.Data, txHash!));
                }
                if (transaction.Nonce is { } nonce && all.TryGetValue(nonce, out Transaction? replacement)
                    && replacement.Hash is not null && replacement.Hash != txHash)
                {
                    result["status"] = "replaced";
                    result["nonce"] = nonce.ToString();
                    result["replacementHash"] = replacement.Hash.ToString();
                    result["summary"] = $"{Short(txHash)} (nonce {nonce}) was replaced in this node's pool by {replacement.Hash}. This tool cannot send transactions.";
                    ((JsonArray)result["recommendations"]!).Insert(0, $"Inspect replacement transaction {replacement.Hash} before submitting anything else at nonce {nonce}.");
                    return executor.Success(result);
                }
            }

            FeeSnapshot snapshot = ReadFees(eth);
            BlockingTransaction? blocker = null;
            ulong executableUntil = latest;
            while (executableUntil < firstMissing)
            {
                token.ThrowIfCancellationRequested();
                Transaction candidate = all[executableUntil];
                string? reason = snapshot.NextBaseFee is { } baseFee && candidate.MaxFeePerGas < baseFee ? "underpriced"
                    : candidate.Type == TxType.Blob && snapshot.BlobBaseFee is { } blobFee && candidate.MaxFeePerBlobGas < blobFee ? "blob_underpriced" : null;
                if (reason is not null)
                {
                    blocker = new BlockingTransaction(candidate.Nonce, candidate.Hash, reason);
                    break;
                }
                executableUntil++;
            }
            if (transaction is not null)
            {
                JsonArray gaps = FindNonceGaps(all.Keys, latest, transaction.Nonce ?? latest, out int omitted);
                foreach ((string name, JsonNode? value) in Diagnose(transaction, latest, firstMissing, snapshot, txHash, blocker, gaps, omitted)) result[name] = value?.DeepClone();
            }
            else
            {
                JsonArray transactions = [];
                JsonArray gaps = [];
                int gapCount = 0;
                Dictionary<string, int> listed = [];
                int staleCount = 0;
                ulong next = latest;
                foreach ((ulong nonce, Transaction candidate) in all)
                {
                    token.ThrowIfCancellationRequested();
                    if (nonce < latest) staleCount++;
                    if (nonce > next) AddNonceGap(gaps, ref gapCount, next, nonce - 1);
                    if (transactions.Count < MaxTransactions)
                    {
                        JsonObject item = Diagnose(PoolTransaction(candidate), latest, firstMissing, snapshot, blockedBy: blocker,
                            nonceGaps: gaps, nonceGapsOmitted: Math.Max(0, gapCount - gaps.Count));
                        transactions.Add(item);
                        string status = item["status"]!.GetValue<string>();
                        listed[status] = listed.GetValueOrDefault(status) + 1;
                    }
                    if (nonce >= next) next = nonce == ulong.MaxValue ? nonce : nonce + 1;
                }
                result["transactions"] = transactions;
                result["omitted"] = all.Count - transactions.Count;
                result["nonceGaps"] = gaps;
                result["nonceGapsOmitted"] = Math.Max(0, gapCount - gaps.Count);
                result["recentTips"] = Tips(snapshot);
                JsonArray notes = Strings(snapshot.Notes);
                if (staleCount > 0) notes.Add("Below-latest pool entries are stale and cannot execute; they are counted separately from pending and queued transactions.");
                result["notes"] = notes;
                result["stale"] = staleCount;
                result["summary"] = $"Address {McpEthHelpers.Checksum(sender)}: showing {transactions.Count} of {all.Count} pool entries ({all.Count - transactions.Count} omitted): "
                    + $"{listed.GetValueOrDefault("pending_ready")} pending and {listed.GetValueOrDefault("pending_queued")} queued, "
                    + $"{listed.GetValueOrDefault("pending_blocked")} blocked, {listed.GetValueOrDefault("underpriced")} underpriced, "
                    + $"{listed.GetValueOrDefault("blob_underpriced")} blob-underpriced, {listed.GetValueOrDefault("fee_unavailable")} with unknown fees, "
                    + $"plus {listed.GetValueOrDefault("nonce_too_low")} stale entries. Latest nonce is {latestResult.Data}; pending nonce is {pendingResult.Data}. This tool cannot send transactions.";
                if (blocker is not null) ((JsonArray)result["recommendations"]!).Insert(0, BlockedAdvice(blocker));
                if (gaps.Count > 0) ((JsonArray)result["recommendations"]!).Insert(blocker is null ? 0 : 1, $"Submit the missing transaction at nonce {firstMissing}; later nonces cannot execute first.");
            }
            return executor.Success(result);

            void Add(Transaction[] candidates)
            {
                foreach (Transaction candidate in candidates)
                {
                    token.ThrowIfCancellationRequested();
                    all[candidate.Nonce] = candidate;
                }
            }
        }, cancellationToken);
    }

    internal sealed record FeeSnapshot(UInt256? NextBaseFee, UInt256? BlobBaseFee, UInt256? P25, UInt256? P50,
        UInt256? P75, bool BlobFeeIsNext = true, string[]? Notes = null);

    internal sealed record BlockingTransaction(ulong Nonce, Hash256? Hash, string Reason);

    private static FeeSnapshot ReadFees(IEthRpcModule eth)
    {
        List<string> notes = [];
        UInt256? nextBase = null;
        UInt256? nextBlob = null;
        UInt256?[] percentiles = new UInt256?[3];
        using (ResultWrapper<FeeHistoryResults> result = eth.eth_feeHistory(20, BlockParameter.Latest, [25, 50, 75]))
        {
            if (result.Result.ResultType == ResultType.Success && result.Data is { } history)
            {
                // FeeHistoryOracle appends the next-block estimates after the sampled block fees.
                if (history.BaseFeePerGas.Count > 1) nextBase = history.BaseFeePerGas[^1];
                if (history.BaseFeePerBlobGas.Count > 1) nextBlob = history.BaseFeePerBlobGas[^1];
                for (int p = 0; p < 3; p++)
                {
                    List<UInt256> samples = [];
                    if (history.Reward is { } reward)
                        foreach (ArrayPoolList<UInt256> row in reward)
                            if (row is not null && row.Count > p) samples.Add(row[p]);
                    if (samples.Count == 0) continue;
                    samples.Sort();
                    UInt256 lower = samples[(samples.Count - 1) / 2];
                    percentiles[p] = lower + (samples[samples.Count / 2] - lower) / 2;
                }
            }
            else notes.Add("Recent fee history could not be read; retry when recent blocks and receipts are available.");
        }
        if (nextBase is null)
        {
            using ResultWrapper<UInt256?> fallback = eth.eth_baseFee();
            if (fallback.Result.ResultType == ResultType.Success) nextBase = fallback.Data;
            if (nextBase is null) notes.Add("The next block's base fee is unavailable; fee readiness cannot be established.");
        }
        if (percentiles[0] is null || percentiles[1] is null || percentiles[2] is null)
            notes.Add("Recent tip percentiles are unavailable; a zero tip has not been assumed.");
        bool blobIsNext = nextBlob is not null;
        if (nextBlob is null)
        {
            using ResultWrapper<UInt256?> fallback = eth.eth_blobBaseFee();
            if (fallback.Result.ResultType == ResultType.Success) nextBlob = fallback.Data;
        }
        return new FeeSnapshot(nextBase, nextBlob, percentiles[0], percentiles[1], percentiles[2], blobIsNext, [.. notes]);
    }

    internal static JsonObject Diagnose(LegacyTransactionForRpc tx, ulong latest, ulong firstMissing, FeeSnapshot snapshot, Hash256? hash = null, BlockingTransaction? blockedBy = null, JsonArray? nonceGaps = null, int nonceGapsOmitted = 0)
    {
        ulong nonce = tx.Nonce ?? 0;
        bool legacy = tx is not EIP1559TransactionForRpc;
        bool blob = tx is BlobTransactionForRpc;
        BigInteger cap = (BigInteger)(tx is EIP1559TransactionForRpc dynamic ? dynamic.MaxFeePerGas ?? 0 : tx.GasPrice ?? 0);
        BigInteger tip = (BigInteger)(tx is EIP1559TransactionForRpc dynamicTip ? dynamicTip.MaxPriorityFeePerGas ?? 0 : tx.GasPrice ?? 0);
        BigInteger blobCap = (BigInteger)(tx is BlobTransactionForRpc blobTx ? blobTx.MaxFeePerBlobGas ?? 0 : UInt256.Zero);
        BigInteger? baseFee = Number(snapshot.NextBaseFee);
        BigInteger? blobFee = Number(snapshot.BlobBaseFee);
        BigInteger? median = Number(snapshot.P50);
        BigInteger? effective = baseFee is { } knownBase ? BigInteger.Max(0, BigInteger.Min(tip, cap - knownBase)) : null;
        bool? lowTip = effective is { } knownTip && median is { } knownMedian ? knownTip < knownMedian : null;
        string ownFeeStatus = baseFee is { } gasBase && cap < gasBase ? "underpriced"
            : blob && blobFee is { } knownBlob && blobCap < knownBlob ? "blob_underpriced"
            : baseFee is null || snapshot.P25 is null || snapshot.P50 is null || snapshot.P75 is null || blob && blobFee is null ? "fee_unavailable"
            : "pending_ready";
        string status = nonce < latest ? "nonce_too_low" : blockedBy is not null && nonce > blockedBy.Nonce ? "pending_blocked"
            : nonce > firstMissing ? "pending_queued" : ownFeeStatus;
        nonceGaps ??= firstMissing < nonce && nonce >= latest
            ? new JsonArray(new JsonObject { ["from"] = firstMissing.ToString(), ["to"] = firstMissing.ToString() }) : [];
        JsonObject fees = [];
        if (legacy) SetFee(fees, "gasPrice", cap);
        else
        {
            SetFee(fees, "maxFeePerGas", cap);
            SetFee(fees, "maxPriorityFeePerGas", tip);
        }
        SetFee(fees, "nextBaseFee", baseFee);
        SetFee(fees, "effectiveTip", effective);
        List<string> notes = snapshot.Notes is null ? [] : [.. snapshot.Notes];
        if (lowTip == true) notes.Add("The effective tip is below the recent median; this may delay inclusion but does not make the transaction ineligible.");
        if (blob)
        {
            SetFee(fees, "maxFeePerBlobGas", blobCap);
            SetFee(fees, "blobBaseFee", blobFee);
            fees["blobBaseFeeIsNext"] = snapshot.BlobFeeIsNext;
            notes.Add("A blob replacement must not carry fewer blobs. The fee minimum below applies to the same wrapper version; a newer wrapper version has a separate pool acceptance rule.");
            if (blobFee is null) notes.Add("The blob base fee is unavailable; blob fee readiness cannot be established.");
            else if (snapshot.BlobFeeIsNext) notes.Add("The blob recommendation is at least twice the next block's blob base fee, or twice the old blob cap, whichever is larger.");
            else if (!snapshot.BlobFeeIsNext) notes.Add("Only the head block's blob base fee is available; the recommendation adds 12.5% headroom.");
        }

        // Keep the rounding and zero-fee exception in sync with CompareReplacedTxByFee and CompareReplacedBlobTx.
        BigInteger minFee = blob ? cap * 2 : cap + cap / 10;
        BigInteger minTip = blob ? tip * 2 : tip + tip / 10;
        if (!blob && cap > 0)
        {
            if (legacy && cap / 10 == 0) minFee++;
            if (!legacy && (cap / 10 == 0 || tip / 10 == 0)) minTip++;
        }
        if (!blob && cap.IsZero) (minFee, minTip) = (0, 0);
        if (!legacy) minFee = BigInteger.Max(minFee, minTip);
        JsonObject minimum = FeeOffer(minFee, minTip, blobCap * 2);
        JsonObject? recommended = null;
        if (baseFee is { } next && median is { } middle && (!blob || blobFee is not null))
        {
            BigInteger recommendedTip = BigInteger.Max(minTip, middle);
            BigInteger recommendedFee = BigInteger.Max(minFee, BigInteger.Max(2 * next + middle, recommendedTip));
            BigInteger recommendedBlob = BigInteger.Max(blobCap * 2, blobFee is { } currentBlob
                ? snapshot.BlobFeeIsNext ? currentBlob * 2 : (currentBlob * 9 + 7) / 8 : 0);
            if (recommendedFee <= (BigInteger)UInt256.MaxValue && recommendedTip <= (BigInteger)UInt256.MaxValue && recommendedBlob <= (BigInteger)UInt256.MaxValue)
                recommended = FeeOffer(recommendedFee, recommendedTip, recommendedBlob);
            else notes.Add("The required replacement fees exceed uint256; no valid replacement recommendation can be given.");
        }
        string feeName = legacy ? "gasPrice" : "maxFeePerGas";
        string detail = status switch
        {
            "underpriced" => $"is underpriced: {feeName} {Gwei(cap)} gwei < next base fee {Gwei(baseFee)} gwei",
            "blob_underpriced" => $"is blob underpriced: maxFeePerBlobGas {Gwei(blobCap)} gwei < blob base fee {Gwei(blobFee)} gwei",
            "pending_blocked" => $"is blocked by {blockedBy!.Reason} transaction {Short(blockedBy.Hash)} at nonce {blockedBy.Nonce}",
            "pending_queued" => $"is queued behind missing nonce {firstMissing}",
            "nonce_too_low" => $"has a nonce below the latest mined nonce {latest}; it is no longer executable",
            "fee_unavailable" => "has incomplete fee data; readiness is unknown",
            _ => $"is ready: {feeName} {Gwei(cap)} gwei, next base fee {Gwei(baseFee)} gwei, effective tip {Gwei(effective)} gwei versus recent median {Gwei(median)} gwei"
        };
        if (nonce >= latest && status != ownFeeStatus)
        {
            if (ownFeeStatus == "underpriced") detail += $"; its own {feeName} {Gwei(cap)} gwei is below next base fee {Gwei(baseFee)} gwei";
            else if (ownFeeStatus == "blob_underpriced") detail += $"; its own maxFeePerBlobGas {Gwei(blobCap)} gwei is below blob base fee {Gwei(blobFee)} gwei";
            else if (ownFeeStatus == "fee_unavailable") detail += "; its own fee readiness is unknown because fee data is unavailable";
        }
        if (nonceGaps.Count > 0) detail += "; missing nonce ranges " + string.Join(", ", nonceGaps.Select(static gap => $"{gap!["from"]}..{gap["to"]}"));
        JsonObject result = NewResult(status, $"{Short(hash ?? tx.Hash)} (nonce {nonce}) {detail}. This tool cannot send transactions.");
        result["ownFeeStatus"] = ownFeeStatus;
        result["nonceGaps"] = nonceGaps.DeepClone();
        result["nonceGapsOmitted"] = nonceGapsOmitted;
        result["hash"] = (hash ?? tx.Hash)?.ToString();
        result["nonce"] = nonce.ToString();
        result["type"] = (int)(tx.Type ?? TxType.Legacy);
        if (status == "pending_blocked") result["blockedBy"] = new JsonObject
        {
            ["nonce"] = blockedBy!.Nonce.ToString(), ["hash"] = blockedBy.Hash?.ToString(), ["reason"] = blockedBy.Reason
        };
        result["fees"] = fees;
        result["recentTips"] = Tips(snapshot);
        result["lowTip"] = lowTip;
        result["replacementMinimum"] = minimum;
        result["recommended"] = recommended;
        result["notes"] = Strings(notes);
        JsonArray advice = (JsonArray)result["recommendations"]!;
        if (nonce >= latest && recommended is not null && (ownFeeStatus is "underpriced" or "blob_underpriced" || ownFeeStatus == "pending_ready" && effective < Number(snapshot.P25)))
        {
            string recommendation = $"Replace nonce {nonce} with {feeName} at least {recommended[feeName + "Wei"]} wei ({recommended[feeName + "Gwei"]} gwei)";
            if (legacy) recommendation = $"Replace nonce {nonce}: for a type-{(int)(tx.Type ?? TxType.Legacy)} replacement set gasPrice at least {recommended["gasPriceWei"]} wei ({recommended["gasPriceGwei"]} gwei)";
            else recommendation += $" and maxPriorityFeePerGas at least {recommended["maxPriorityFeePerGasWei"]} wei ({recommended["maxPriorityFeePerGasGwei"]} gwei)";
            if (blob) recommendation += $", maxFeePerBlobGas at least {recommended["maxFeePerBlobGasWei"]} wei ({recommended["maxFeePerBlobGasGwei"]} gwei), retaining at least the same number of blobs";
            advice.Insert(0, recommendation + ".");
            result["summary"] = $"{Short(hash ?? tx.Hash)} (nonce {nonce}) {detail}. {recommendation}. This tool cannot send transactions.";
        }
        else if (status == "pending_ready") advice.Insert(0, "The fees are adequate relative to recent blocks; it should be included soon, subject to block capacity and propagation.");
        else if (ownFeeStatus == "fee_unavailable") advice.Insert(0, "Retry when fee history and the required base fees are available; readiness has not been assumed.");
        if (status == "nonce_too_low") advice.Insert(0, $"Check the canonical history for nonce {nonce}; the account's latest nonce is {latest}.");
        if (nonceGaps.Count > 0) advice.Insert(0, $"Submit the missing transaction at nonce {nonceGaps[0]!["from"]} before nonce {nonce} can execute.");
        if (status == "pending_blocked") advice.Insert(0, BlockedAdvice(blockedBy!));
        return result;

        JsonObject FeeOffer(BigInteger fee, BigInteger priority, BigInteger blobPrice)
        {
            JsonObject offer = [];
            if (legacy) SetFee(offer, "gasPrice", fee);
            else
            {
                SetFee(offer, "maxFeePerGas", fee);
                SetFee(offer, "maxPriorityFeePerGas", priority);
            }
            if (blob)
            {
                SetFee(offer, "maxFeePerBlobGas", blobPrice);
                offer["minimumBlobCount"] = ((BlobTransactionForRpc)tx).BlobVersionedHashes?.Length;
            }
            return offer;
        }
    }

    private static JsonArray FindNonceGaps(IEnumerable<ulong> nonces, ulong latest, ulong target, out int omitted)
    {
        JsonArray gaps = [];
        int count = 0;
        ulong next = latest;
        foreach (ulong nonce in nonces)
        {
            if (nonce >= target) break;
            if (nonce > next) AddNonceGap(gaps, ref count, next, nonce - 1);
            if (nonce >= next) next = nonce + 1;
        }
        if (next < target) AddNonceGap(gaps, ref count, next, target - 1);
        omitted = Math.Max(0, count - gaps.Count);
        return gaps;
    }

    private static void AddNonceGap(JsonArray gaps, ref int count, ulong from, ulong to)
    {
        if (count++ < 10) gaps.Add(new JsonObject { ["from"] = from.ToString(), ["to"] = to.ToString() });
    }

    private static string BlockedAdvice(BlockingTransaction blocker) =>
        $"Replace transaction nonce {blocker.Nonce} first ({Short(blocker.Hash)}, {blocker.Reason}); later nonces cannot execute before it.";

    private static JsonObject Mined(IEthRpcModule eth, TransactionForRpc tx, Hash256 hash)
    {
        using ResultWrapper<ReceiptForRpc?> receipt = eth.eth_getTransactionReceipt(hash);
        string nonce = tx is LegacyTransactionForRpc legacy ? legacy.Nonce?.ToString() ?? "unknown" : "not applicable";
        JsonObject result = NewResult("mined", $"{Short(hash)} (nonce {nonce}) was mined in block {tx.BlockNumber}. Open explain_transaction for its effects. This tool cannot send transactions.");
        result["hash"] = hash.ToString();
        result["blockNumber"] = (long)tx.BlockNumber!.Value;
        if (tx.TransactionIndex is { } index) result["transactionIndex"] = index;
        result["succeeded"] = receipt.Result.ResultType == ResultType.Success ? receipt.Data?.Status switch { 0 => false, 1 => true, _ => (bool?)null } : null;
        ((JsonArray)result["recommendations"]!).Insert(0, $"Call explain_transaction with hash {hash} for transfers, fees and the outcome.");
        return result;
    }

    private static LegacyTransactionForRpc PoolTransaction(Transaction transaction)
    {
        LegacyTransactionForRpc result = transaction.Type switch
        {
            TxType.Blob => new BlobTransactionForRpc { MaxFeePerBlobGas = transaction.MaxFeePerBlobGas, BlobVersionedHashes = transaction.BlobVersionedHashes?.Select(static hash => hash!).ToArray() },
            TxType.SetCode => new SetCodeTransactionForRpc(),
            TxType.EIP1559 => new EIP1559TransactionForRpc(),
            TxType.AccessList => new AccessListTransactionForRpc(),
            _ => new LegacyTransactionForRpc()
        };
        result.Nonce = transaction.Nonce;
        result.Hash = transaction.Hash;
        result.From = transaction.SenderAddress;
        result.GasPrice = transaction.GasPrice;
        if (result is EIP1559TransactionForRpc dynamic)
        {
            dynamic.MaxFeePerGas = transaction.MaxFeePerGas;
            dynamic.MaxPriorityFeePerGas = transaction.MaxPriorityFeePerGas;
        }
        return result;
    }

    private static JsonObject NewResult(string status, string summary) => new()
    {
        ["status"] = status, ["summary"] = summary, ["canSend"] = false,
        ["recommendations"] = new JsonArray("This read-only tool cannot sign or send transactions; use a wallet or your own transaction sender to act.")
    };

    private static BigInteger? Number(UInt256? value) => value is { } number ? (BigInteger)number : null;
    private static string Gwei(BigInteger? value) => value is { } number ? McpTokenMetadata.FormatUnits(number, 9) : "unknown";
    private static string Short(Hash256? hash) => hash is null ? "Unknown hash" : hash.ToString()[..10] + "…";
    private static void SetFee(JsonObject target, string name, BigInteger? value)
    {
        target[name + "Wei"] = value?.ToString();
        target[name + "Gwei"] = value is null ? null : Gwei(value);
    }
    private static JsonObject Tips(FeeSnapshot snapshot) => new()
    {
        ["p25"] = Tip(snapshot.P25), ["p50"] = Tip(snapshot.P50), ["p75"] = Tip(snapshot.P75)
    };
    private static JsonObject Tip(UInt256? amount) => new() { ["wei"] = amount?.ToString(), ["gwei"] = amount is null ? null : Gwei(Number(amount)) };
    private static JsonArray Strings(IEnumerable<string>? values)
    {
        JsonArray result = [];
        if (values is not null) foreach (string value in values) result.Add(value);
        return result;
    }
}
