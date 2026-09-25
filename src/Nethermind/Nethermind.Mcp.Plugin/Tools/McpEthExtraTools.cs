// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Serialization.Json;
using Nethermind.State.Proofs;

namespace Nethermind.Mcp.Plugin.Tools;

/// <summary>Read-only MCP tools for storage, proofs, gas estimation, fee suggestions and block receipts.</summary>
/// <param name="executor">Runs tool bodies against rented modules under the MCP limits.</param>
/// <param name="blockFinder">Resolves block selectors to numbers for availability checks.</param>
/// <param name="capabilities">Reports which blocks have state and receipts on this node.</param>
/// <param name="chainProfile">The native currency and network name used in human-friendly fields.</param>
/// <param name="config">The MCP limits.</param>
/// <param name="rpcConfig">The JSON-RPC configuration; <see cref="IJsonRpcConfig.GasCap"/> also caps <c>estimate_gas</c>.</param>
[McpServerToolType]
internal sealed class McpEthExtraTools(
    McpToolExecutor executor,
    IBlockFinder blockFinder,
    McpNodeCapabilities capabilities,
    McpChainProfile chainProfile,
    IMcpConfig config,
    IJsonRpcConfig rpcConfig) : IMcpToolSet
{
    /// <summary>The maximum number of storage keys accepted by <c>get_proof</c>.</summary>
    public const int MaxProofStorageKeys = 64;

    /// <summary>The maximum number of blocks <c>fee_estimate</c> samples (the <c>eth_feeHistory</c> limit).</summary>
    public const int MaxFeeHistoryBlocks = 1024;

    /// <summary>The maximum number of reward percentiles accepted by <c>fee_estimate</c>.</summary>
    public const int MaxFeePercentiles = 10;

    /// <summary>The maximum number of receipts returned by one <c>get_block_receipts</c> page.</summary>
    public const int MaxReceiptsPage = 1000;

    private const int DefaultFeeHistoryBlocks = 20;
    private const int DefaultReceiptsPage = 100;
    private const int RecentRatioCount = 10;
    private const ulong TransferGas = 21_000;
    private const ulong GasPerBlob = 131_072;
    private const double TrendThreshold = 0.1;
    private static readonly double[] DefaultPercentiles = [10, 50, 90];

    // Values below 2^64 are far more likely counters, flags or amounts than addresses.
    private static readonly UInt256 MinAddressLike = UInt256.One << 64;

    private readonly ulong _maxCallGas = Math.Min((ulong)Math.Max(1, config.MaxCallGas), rpcConfig.GasCap.EffectiveGasCap());
    private readonly int _maxCallDataSize = Math.Max(0, config.MaxCallDataSize);
    private readonly int _maxResultSize = Math.Max(1, config.MaxResultSize);

    /// <inheritdoc/>
    public IEnumerable<McpServerTool> CreateServerTools() =>
        McpToolFactory.Create(this, DescribeLimits, _maxResultSize);

    private string DescribeLimits(MethodInfo method) => method.Name switch
    {
        nameof(EstimateGas) => $" Limits on this node: estimates above {_maxCallGas} gas fail; data at most {_maxCallDataSize} bytes.",
        _ => string.Empty
    };

    /// <summary>Returns one storage slot of a contract.</summary>
    [McpServerTool(Name = "get_storage_at", Title = "Get storage slot", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Reads one raw 32-byte storage slot of a contract (eth_getStorageAt). Returns {address, slot, value (32-byte hex), asUint (decimal string), " +
        "asAddress (EIP-55, only when the upper 12 bytes are zero and the value is at least 2^64, i.e. it looks like an address), blockNumber}. " +
        "Use it for low-level inspection, such as EIP-1967 proxy implementation slots (0x360894a13ba1a3210667c828492db98dca3e2076cc3735a920a3ca505d382bbc); " +
        "for token balances or other public getters prefer call_function or token_balances. " +
        "Fails with unavailable if the node has no state for the block.")]
    [McpToolOutputSchema("""
        {"type":"object","required":["result"],"properties":{"result":{"type":"object","required":["address","slot","value","asUint","blockNumber"],
          "properties":{"address":{"type":"string"},"slot":{"type":"string"},"value":{"type":"string"},"asUint":{"type":"string"},
            "asAddress":{"type":"string"},"blockNumber":{"type":"string"}}}}}
        """)]
    public Task<CallToolResult> GetStorageAt(
        [Description(McpEthTools.AddressDescription)] string address,
        [Description("Storage slot: 0x followed by up to 64 hex characters (for example a keccak-derived mapping slot), or a decimal slot index such as \"0\".")] string slot,
        [Description(McpEthTools.BlockSelectorDescription + " Default \"latest\".")] string block = "latest",
        CancellationToken cancellationToken = default)
    {
        if (!McpToolInput.TryParseAddress(address, nameof(address), out Address? account, out string? error)
            || !McpToolInput.TryParseStorageSlot(slot, nameof(slot), out UInt256 slotIndex, out error)
            || !McpToolInput.TryParseBlock(block, nameof(block), out BlockParameter? blockParameter, out error))
        {
            return McpEthHelpers.InvalidInput(error);
        }

        return executor.ExecuteAsync("get_storage_at", nameof(IEthRpcModule.eth_getStorageAt), (eth, _) =>
        {
            if (!TryResolveState(blockParameter, nameof(block), out BlockParameter? query, out ulong number, out CallToolResult? failure))
            {
                return Task.FromResult(failure);
            }

            using ResultWrapper<byte[]> result = eth.eth_getStorageAt(account, new StorageIndex(slotIndex), query);
            if (result.Result.ResultType != ResultType.Success)
            {
                return Task.FromResult(executor.Failure("get_storage_at", result));
            }

            UInt256 value = new(result.Data, isBigEndian: true);
            return Task.FromResult(executor.Success((Account: account, Slot: slotIndex, Value: value, Number: number), static (writer, state) =>
            {
                writer.WriteStartObject();
                writer.WriteString("address"u8, McpEthHelpers.Checksum(state.Account));
                writer.WriteString("slot"u8, Word(state.Slot));
                writer.WriteString("value"u8, Word(state.Value));
                writer.WriteString("asUint"u8, state.Value.ToString());
                if (state.Value >= MinAddressLike && state.Value < (UInt256.One << 160))
                {
                    writer.WriteString("asAddress"u8, McpEthHelpers.Checksum(new Address(state.Value.ToBigEndian()[12..])));
                }

                writer.WritePropertyName("blockNumber"u8);
                McpToolExecutor.WriteValue(writer, state.Number);
                writer.WriteEndObject();
                return null;
            }));
        }, cancellationToken);
    }

    /// <summary>Estimates the gas a transaction would use.</summary>
    [McpServerTool(Name = "estimate_gas", Title = "Estimate gas", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Estimates the gas a transaction would need if sent now (eth_estimateGas), without signing or broadcasting anything. " +
        "Returns {gas (hex), gasUnits (integer), blockNumber}. Omit 'to' to estimate a contract deployment (then 'data' is the init code). " +
        "Combine with fee_estimate to price the transaction; use simulate_transaction to see what it would do. " +
        "If the transaction would revert, fails with execution_reverted: the revert data is in error.data and the decoded reason in error.reason; " +
        "insufficient balance for 'value' fails with invalid_input.")]
    [McpToolOutputSchema("""
        {"type":"object","required":["result"],"properties":{"result":{"type":"object","required":["gas","gasUnits","blockNumber"],
          "properties":{"gas":{"type":"string"},"gasUnits":{"type":"integer"},"blockNumber":{"type":"string"}}}}}
        """)]
    public Task<CallToolResult> EstimateGas(
        [Description("Optional sender address (0x followed by 40 hex characters); defaults to the zero address. Needs enough balance for 'value'.")] string? from = null,
        [Description("Optional recipient or contract address (0x followed by 40 hex characters); omit for a contract deployment.")] string? to = null,
        [Description("Optional ABI-encoded call data or init code as 0x-prefixed hex; default \"0x\".")] string? data = null,
        [Description("Optional value to send in wei, as a 0x-hex or decimal string. Default 0.")] string? value = null,
        [Description(McpEthTools.BlockSelectorDescription + " Default \"latest\".")] string block = "latest",
        CancellationToken cancellationToken = default)
    {
        Address? sender = null;
        Address? target = null;
        byte[]? input = null;
        UInt256 callValue = UInt256.Zero;
        if ((from is not null && !McpToolInput.TryParseAddress(from, nameof(from), out sender, out string? error))
            || (to is not null && !McpToolInput.TryParseAddress(to, nameof(to), out target, out error))
            || (data is not null && !McpToolInput.TryParseData(data, nameof(data), _maxCallDataSize, out input, out error))
            || (value is not null && !McpToolInput.TryParseUInt256(value, nameof(value), out callValue, out error))
            || !McpToolInput.TryParseBlock(block, nameof(block), out BlockParameter? blockParameter, out error))
        {
            return McpEthHelpers.InvalidInput(error!);
        }

        input ??= [];
        if (target is null && input.Length == 0)
        {
            return McpEthHelpers.InvalidInput("Pass 'to' for a call or transfer, or 'data' with init code for a contract deployment.");
        }

        return executor.ExecuteAsync("estimate_gas", nameof(IEthRpcModule.eth_estimateGas), (eth, _) =>
        {
            if (!TryResolveState(blockParameter, nameof(block), out BlockParameter? query, out ulong number, out CallToolResult? failure))
            {
                return Task.FromResult(failure);
            }

            // The binary search is bounded by the transaction gas, so the MCP cap also bounds the work.
            ulong blockGasLimit = blockFinder.FindHeader(query)?.GasLimit ?? _maxCallGas;
            LegacyTransactionForRpc transaction = new()
            {
                From = sender,
                To = target,
                Input = input,
                Value = callValue,
                Gas = Math.Max(1, Math.Min(_maxCallGas, blockGasLimit))
            };

            using ResultWrapper<UInt256?> result = eth.eth_estimateGas(transaction, query);
            if (result.Result.ResultType != ResultType.Success)
            {
                return Task.FromResult(McpEthHelpers.WithRevertReason(executor.Failure("estimate_gas", result), result));
            }

            if (result.Data is not { } gas)
            {
                return Task.FromResult(McpToolExecutor.Error(McpToolErrorCodes.InternalError, "The node returned no estimate."));
            }

            return Task.FromResult(executor.Success((Gas: gas, Number: number), static (writer, state) =>
            {
                writer.WriteStartObject();
                writer.WritePropertyName("gas"u8);
                McpToolExecutor.WriteValue(writer, state.Gas);
                writer.WriteNumber("gasUnits"u8, (ulong)state.Gas);
                writer.WritePropertyName("blockNumber"u8);
                McpToolExecutor.WriteValue(writer, state.Number);
                writer.WriteEndObject();
                return null;
            }));
        }, cancellationToken);
    }

    /// <summary>Suggests transaction fees from recent blocks.</summary>
    [McpServerTool(Name = "fee_estimate", Title = "Fee estimate", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Answers \"how much gas should I pay?\": combines eth_feeHistory over recent blocks with the node's gas price oracle. Returns the current and next-block " +
        "base fee, slow/standard/fast suggestions of maxPriorityFeePerGas and maxFeePerGas (EIP-1559 fields, in wei hex and gwei decimal strings), the legacy gasPrice, " +
        "how full recent blocks were (gasUsedRatio average, last values and trend), the cost of a plain 21,000-gas transfer in the native currency (ETH, or xDAI on Gnosis/Chiado), " +
        "a blob fee section (EIP-4844, when active) and a plain-English summary. Suggestions use the reward percentiles of recent blocks: slow = first percentile, " +
        "standard = middle, fast = last; maxFeePerGas leaves headroom for base fee increases (1.25x the next base fee for slow, 2x otherwise). " +
        "On Gnosis base fees are often only a few wei, so the tip dominates the price.")]
    [McpToolOutputSchema("""
        {"type":"object","required":["result"],"properties":{"result":{"type":"object",
          "required":["networkName","nativeCurrency","eip1559","oldestBlock","newestBlock","blocks","percentiles","baseFee","gasPrice","gasPriceGwei",
            "maxPriorityFeePerGas","maxPriorityFeePerGasGwei","suggestions","gasUsedRatio","transferCost","blob","summary"],
          "properties":{
            "networkName":{"type":"string"},"nativeCurrency":{"type":"string"},"eip1559":{"type":"boolean"},
            "oldestBlock":{"type":"string"},"newestBlock":{"type":"string"},"blocks":{"type":"integer"},
            "percentiles":{"type":"array","items":{"type":"number"}},
            "baseFee":{"type":"object","required":["current","currentGwei","next","nextGwei"],
              "properties":{"current":{"type":"string"},"currentGwei":{"type":"string"},"next":{"type":"string"},"nextGwei":{"type":"string"}}},
            "gasPrice":{"type":"string"},"gasPriceGwei":{"type":"string"},
            "maxPriorityFeePerGas":{"type":"string"},"maxPriorityFeePerGasGwei":{"type":"string"},
            "suggestions":{"type":"object","required":["slow","standard","fast"],"properties":{
              "slow":{"$ref":"#/$defs/tier"},"standard":{"$ref":"#/$defs/tier"},"fast":{"$ref":"#/$defs/tier"}}},
            "gasUsedRatio":{"type":"object","required":["average","trend","recent"],"properties":{
              "average":{"type":"number"},"trend":{"type":"string","enum":["rising","falling","stable"]},"recent":{"type":"array","items":{"type":"number"}}}},
            "transferCost":{"type":"object","required":["gas","wei","formatted","symbol"],"properties":{
              "gas":{"type":"integer"},"wei":{"type":"string"},"formatted":{"type":"string"},"symbol":{"type":"string"}}},
            "blob":{"type":"object","required":["available"],"properties":{
              "available":{"type":"boolean"},"baseFeePerBlobGas":{"type":"string"},"baseFeePerBlobGasGwei":{"type":"string"},
              "nextBaseFeePerBlobGas":{"type":"string"},"nextBaseFeePerBlobGasGwei":{"type":"string"},"blobGasUsedRatioAverage":{"type":"number"},
              "costPerBlob":{"type":"string"},"costPerBlobFormatted":{"type":"string"}}},
            "summary":{"type":"string"}}}},
         "$defs":{"tier":{"type":"object","required":["percentile","maxPriorityFeePerGas","maxPriorityFeePerGasGwei","maxFeePerGas","maxFeePerGasGwei"],
           "properties":{"percentile":{"type":"number"},"maxPriorityFeePerGas":{"type":"string"},"maxPriorityFeePerGasGwei":{"type":"string"},
             "maxFeePerGas":{"type":"string"},"maxFeePerGasGwei":{"type":"string"}}}}}
        """)]
    public Task<CallToolResult> FeeEstimate(
        [Description("Number of recent blocks to sample, 1 to 1024. Default 20.")] int blocks = DefaultFeeHistoryBlocks,
        [Description("Optional reward percentiles (0 to 100, ascending, at most 10) used for the slow/standard/fast tips. Default [10, 50, 90].")] double[]? percentiles = null,
        CancellationToken cancellationToken = default)
    {
        if (blocks is < 1 or > MaxFeeHistoryBlocks)
        {
            return McpEthHelpers.InvalidInput($"'blocks' must be between 1 and {MaxFeeHistoryBlocks}.");
        }

        if (!McpToolInput.TryParsePercentiles(percentiles ?? DefaultPercentiles, nameof(percentiles), MaxFeePercentiles, out double[]? rewardPercentiles, out string? error))
        {
            return McpEthHelpers.InvalidInput(error);
        }

        return executor.ExecuteAsync("fee_estimate", nameof(IEthRpcModule.eth_feeHistory), async (eth, _) =>
        {
            using ResultWrapper<FeeHistoryResults> history = eth.eth_feeHistory((ulong)blocks, BlockParameter.Latest, rewardPercentiles);
            if (history.Result.ResultType != ResultType.Success)
            {
                return executor.Failure("fee_estimate", history);
            }

            using ResultWrapper<UInt256?> gasPrice = await eth.eth_gasPrice();
            using ResultWrapper<UInt256?> priority = eth.eth_maxPriorityFeePerGas();
            using ResultWrapper<UInt256?> nextBaseFee = eth.eth_baseFee();
            using ResultWrapper<UInt256?> blobBaseFee = eth.eth_blobBaseFee();

            FeeReport report = BuildFeeReport(
                history.Data,
                rewardPercentiles,
                Ok(gasPrice) ?? UInt256.Zero,
                Ok(priority) ?? UInt256.Zero,
                Ok(nextBaseFee),
                Ok(blobBaseFee));
            return executor.Success(report, static (writer, state) =>
            {
                WriteFeeReport(writer, state);
                return null;
            });
        }, cancellationToken);
    }

    /// <summary>Returns a Merkle proof of an account and some of its storage slots.</summary>
    [McpServerTool(Name = "get_proof", Title = "Get Merkle proof", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Returns the EIP-1186 Merkle proof of an account and selected storage slots against a block's state root, exactly as eth_getProof: " +
        "{address, accountProof: [RLP nodes], balance, codeHash, nonce, storageHash, storageProof: [{key, value, proof}]}. " +
        "Use it to verify state trustlessly (bridges, light clients); to simply read a balance or slot prefer get_balance or get_storage_at. " +
        "Fails with unavailable if the node has no state for the block.")]
    [McpToolOutputSchema("""
        {"type":"object","required":["result"],"properties":{"result":{"type":"object",
          "required":["address","accountProof","balance","codeHash","nonce","storageHash","storageProof"],
          "properties":{"address":{"type":"string"},"accountProof":{"type":"array","items":{"type":"string"}},"balance":{"type":"string"},
            "codeHash":{"type":"string"},"nonce":{"type":"string"},"storageHash":{"type":"string"},
            "storageProof":{"type":"array","items":{"type":"object","required":["key","value","proof"],
              "properties":{"key":{"type":"string"},"value":{"type":"string"},"proof":{"type":"array","items":{"type":"string"}}}}}}}}}
        """)]
    public Task<CallToolResult> GetProof(
        [Description(McpEthTools.AddressDescription)] string address,
        [Description("Storage slots to prove (each 0x followed by up to 64 hex characters, or a decimal index); empty or omitted for an account-only proof. At most 64.")] string[]? storageKeys = null,
        [Description(McpEthTools.BlockSelectorDescription + " Default \"latest\".")] string block = "latest",
        CancellationToken cancellationToken = default)
    {
        if (!McpToolInput.TryParseAddress(address, nameof(address), out Address? account, out string? error)
            || !McpToolInput.TryParseStorageSlots(storageKeys, nameof(storageKeys), MaxProofStorageKeys, out UInt256[]? slots, out error)
            || !McpToolInput.TryParseBlock(block, nameof(block), out BlockParameter? blockParameter, out error))
        {
            return McpEthHelpers.InvalidInput(error);
        }

        StorageKeys keys = [.. slots];

        return executor.ExecuteAsync("get_proof", nameof(IEthRpcModule.eth_getProof), (eth, cancellation) =>
        {
            if (!TryResolveState(blockParameter, nameof(block), out BlockParameter? query, out _, out CallToolResult? failure))
            {
                return Task.FromResult(failure);
            }

            using ResultWrapper<AccountProof> result = eth.eth_getProof(account, keys, query);
            return Task.FromResult(result.Result.ResultType == ResultType.Success
                ? executor.Success(result.Data)
                : executor.Failure("get_proof", result));
        }, cancellationToken);
    }

    /// <summary>Returns a page of the receipts of a block.</summary>
    [McpServerTool(Name = "get_block_receipts", Title = "Get block receipts", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Returns the raw receipts of every transaction in a block (eth_getBlockReceipts format: status, gasUsed, effectiveGasPrice, logs...) in pages: " +
        "{blockNumber, blockHash, total, offset, receipts: [...], truncated, nextOffset (only when truncated)}. A mainnet block has hundreds of receipts, so pass " +
        "offset=nextOffset to continue. For one transaction use get_transaction_receipt; for a readable block overview prefer block_summary. " +
        "Fails with unavailable if the node no longer stores receipts for the block, and not_found if the block is unknown.")]
    [McpToolOutputSchema("""
        {"type":"object","required":["result"],"properties":{"result":{"type":"object","required":["blockNumber","blockHash","total","offset","receipts","truncated"],
          "properties":{"blockNumber":{"type":"string"},"blockHash":{"type":["string","null"]},"total":{"type":"integer"},"offset":{"type":"integer"},
            "receipts":{"type":"array","items":{"type":"object","required":["transactionHash","transactionIndex","blockNumber","gasUsed","cumulativeGasUsed","from","logs","type"],
              "properties":{"transactionHash":{"type":"string"},"transactionIndex":{"type":"string"},"blockNumber":{"type":"string"},"gasUsed":{"type":"string"},
                "cumulativeGasUsed":{"type":"string"},"effectiveGasPrice":{"type":"string"},"from":{"type":"string"},"to":{"type":["string","null"]},
                "contractAddress":{"type":["string","null"]},"logs":{"type":"array"},"status":{"type":"string"},"type":{"type":"string"}}}},
            "truncated":{"type":"boolean"},"nextOffset":{"type":"integer"}}}}}
        """)]
    public Task<CallToolResult> GetBlockReceipts(
        [Description(McpEthTools.BlockSelectorDescription)] string block,
        [Description("Index of the first receipt to return. Default 0.")] int offset = 0,
        [Description("Maximum number of receipts to return, 1 to 1000. Default 100.")] int limit = DefaultReceiptsPage,
        CancellationToken cancellationToken = default)
    {
        if (!McpToolInput.TryParseBlock(block, nameof(block), out BlockParameter? blockParameter, out string? error))
        {
            return McpEthHelpers.InvalidInput(error);
        }

        if (offset < 0)
        {
            return McpEthHelpers.InvalidInput("'offset' must be 0 or greater.");
        }

        if (limit is < 1 or > MaxReceiptsPage)
        {
            return McpEthHelpers.InvalidInput($"'limit' must be between 1 and {MaxReceiptsPage}.");
        }

        return executor.ExecuteAsync("get_block_receipts", nameof(IEthRpcModule.eth_getBlockReceipts), (eth, token) =>
        {
            if (!McpEthHelpers.TryResolveBlockNumber(blockFinder, blockParameter, nameof(block), requireCanonical: false, out ulong number, out CallToolResult? failure))
            {
                return Task.FromResult(failure);
            }

            if (capabilities.CheckReceipts((long)number) is { } unavailable)
            {
                return Task.FromResult(unavailable);
            }

            BlockParameter query = blockParameter.Type == BlockParameterType.BlockHash ? blockParameter : new BlockParameter(number);
            using ResultWrapper<ReceiptForRpc[]?> result = eth.eth_getBlockReceipts(query);
            if (result.Result.ResultType != ResultType.Success)
            {
                return Task.FromResult(executor.Failure("get_block_receipts", result));
            }

            if (result.Data is not { } receipts)
            {
                return Task.FromResult(McpToolExecutor.Error(McpToolErrorCodes.NotFound, $"'{nameof(block)}': block not found or its receipts are not stored."));
            }

            if (offset > receipts.Length)
            {
                return Task.FromResult(McpToolExecutor.Error(McpToolErrorCodes.InvalidInput, $"'offset' ({offset}) is beyond the {receipts.Length} receipts of this block."));
            }

            ReceiptPage page = new(receipts, number, offset, limit, Math.Max(1, _maxResultSize / 4 * 3), token);
            return Task.FromResult(executor.Success(page, static (writer, state) => WriteReceiptPage(writer, state)));
        }, cancellationToken);
    }

    /// <summary>Resolves the block, checks that its state is available, and returns the selector to query it with.</summary>
    private bool TryResolveState(BlockParameter block, string parameter, out BlockParameter query, out ulong number, [NotNullWhen(false)] out CallToolResult? failure)
    {
        query = block;
        if (!McpEthHelpers.TryResolveBlockNumber(blockFinder, block, parameter, requireCanonical: false, out number, out failure))
        {
            return false;
        }

        failure = capabilities.CheckState((long)number);
        if (failure is not null)
        {
            return false;
        }

        // Tags are pinned to the checked number, so the query cannot observe a newer head than the availability check.
        query = block.Type == BlockParameterType.BlockHash ? block : new BlockParameter(number);
        return true;
    }

    private static CallToolResult? WriteReceiptPage(Utf8JsonWriter writer, ReceiptPage page)
    {
        ReceiptForRpc[] receipts = page.Receipts;
        int end = (int)Math.Min((long)page.Offset + page.Limit, receipts.Length);
        long bytes = 0;
        int index = page.Offset;

        writer.WriteStartObject();
        writer.WritePropertyName("blockNumber"u8);
        McpToolExecutor.WriteValue(writer, page.Number);
        writer.WritePropertyName("blockHash"u8);
        McpToolExecutor.WriteValue(writer, receipts.Length > 0 ? receipts[0].BlockHash : null);
        writer.WriteNumber("total"u8, receipts.Length);
        writer.WriteNumber("offset"u8, page.Offset);
        writer.WritePropertyName("receipts"u8);
        writer.WriteStartArray();
        for (; index < end; index++)
        {
            page.Token.ThrowIfCancellationRequested();
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(receipts[index], EthereumJsonSerializer.JsonOptions);
            if (index > page.Offset && bytes + json.Length > page.ByteBudget)
            {
                break;
            }

            writer.WriteRawValue(json, skipInputValidation: true);
            bytes += json.Length;
        }

        writer.WriteEndArray();
        bool truncated = index < receipts.Length;
        writer.WriteBoolean("truncated"u8, truncated);
        if (truncated)
        {
            writer.WriteNumber("nextOffset"u8, index);
        }

        writer.WriteEndObject();
        return null;
    }

    private FeeReport BuildFeeReport(FeeHistoryResults history, double[] rewardPercentiles, UInt256 gasPrice, UInt256 oraclePriority, UInt256? nextBaseFee, UInt256? blobBaseFee)
    {
        int count = history.GasUsedRatio.Count;
        UInt256 currentBase = count > 0 && history.BaseFeePerGas.Count >= count ? history.BaseFeePerGas[count - 1] : UInt256.Zero;
        bool eip1559 = nextBaseFee is not null;
        UInt256 nextBase = nextBaseFee ?? UInt256.Zero;

        // Tips of empty blocks are zero by definition, so only blocks that carried transactions vote.
        UInt256[] tips = new UInt256[rewardPercentiles.Length];
        List<UInt256> samples = new(count);
        for (int p = 0; p < rewardPercentiles.Length; p++)
        {
            samples.Clear();
            for (int i = 0; i < count; i++)
            {
                if (history.GasUsedRatio[i] > 0 && history.Reward is { } reward && i < reward.Count && p < reward[i].Count)
                {
                    samples.Add(reward[i][p]);
                }
            }

            tips[p] = samples.Count == 0 ? oraclePriority : Median(samples);
            if (p > 0 && tips[p] < tips[p - 1])
            {
                tips[p] = tips[p - 1];
            }
        }

        int standardIndex = rewardPercentiles.Length / 2;
        FeeTier slow = new(rewardPercentiles[0], tips[0], nextBase + nextBase / 4 + tips[0]);
        FeeTier standard = new(rewardPercentiles[standardIndex], tips[standardIndex], nextBase + nextBase + tips[standardIndex]);
        FeeTier fast = new(rewardPercentiles[^1], tips[^1], nextBase + nextBase + tips[^1]);

        double sum = 0;
        for (int i = 0; i < count; i++)
        {
            sum += history.GasUsedRatio[i];
        }

        double average = count == 0 ? 0 : sum / count;
        string trend = Trend(history.GasUsedRatio);
        double[] recent = new double[Math.Min(RecentRatioCount, count)];
        for (int i = 0; i < recent.Length; i++)
        {
            recent[i] = Math.Round(history.GasUsedRatio[count - recent.Length + i], 4);
        }

        UInt256 transferWei = (UInt256)TransferGas * (eip1559 ? nextBase + standard.Priority : standard.Priority);

        BlobFees? blob = null;
        if (blobBaseFee is { } currentBlob)
        {
            UInt256 nextBlob = history.BaseFeePerBlobGas is { Count: > 0 } blobFees ? blobFees[blobFees.Count - 1] : currentBlob;
            double blobSum = 0;
            int blobCount = history.BlobGasUsedRatio?.Count ?? 0;
            for (int i = 0; i < blobCount; i++)
            {
                blobSum += history.BlobGasUsedRatio![i];
            }

            blob = new BlobFees(currentBlob, nextBlob, blobCount == 0 ? 0 : Math.Round(blobSum / blobCount, 4), (UInt256)GasPerBlob * nextBlob);
        }

        ulong newest = history.OldestBlock + (ulong)Math.Max(0, count - 1);
        return new FeeReport(
            chainProfile.NetworkName,
            chainProfile.NativeCurrencySymbol,
            eip1559,
            history.OldestBlock,
            newest,
            count,
            rewardPercentiles,
            currentBase,
            nextBase,
            gasPrice,
            oraclePriority,
            slow,
            standard,
            fast,
            Math.Round(average, 4),
            trend,
            recent,
            transferWei,
            blob);
    }

    private static void WriteFeeReport(Utf8JsonWriter writer, FeeReport report)
    {
        writer.WriteStartObject();
        writer.WriteString("networkName"u8, report.NetworkName);
        writer.WriteString("nativeCurrency"u8, report.Symbol);
        writer.WriteBoolean("eip1559"u8, report.Eip1559);
        writer.WritePropertyName("oldestBlock"u8);
        McpToolExecutor.WriteValue(writer, report.OldestBlock);
        writer.WritePropertyName("newestBlock"u8);
        McpToolExecutor.WriteValue(writer, report.NewestBlock);
        writer.WriteNumber("blocks"u8, report.Blocks);
        writer.WritePropertyName("percentiles"u8);
        writer.WriteStartArray();
        foreach (double percentile in report.Percentiles)
        {
            writer.WriteNumberValue(percentile);
        }

        writer.WriteEndArray();

        writer.WritePropertyName("baseFee"u8);
        writer.WriteStartObject();
        WriteWei(writer, "current", report.CurrentBaseFee);
        WriteWei(writer, "next", report.NextBaseFee);
        writer.WriteEndObject();

        WriteWei(writer, "gasPrice", report.GasPrice);
        WriteWei(writer, "maxPriorityFeePerGas", report.OraclePriority);

        writer.WritePropertyName("suggestions"u8);
        writer.WriteStartObject();
        WriteTier(writer, "slow"u8, report.Slow);
        WriteTier(writer, "standard"u8, report.Standard);
        WriteTier(writer, "fast"u8, report.Fast);
        writer.WriteEndObject();

        writer.WritePropertyName("gasUsedRatio"u8);
        writer.WriteStartObject();
        writer.WriteNumber("average"u8, report.AverageRatio);
        writer.WriteString("trend"u8, report.Trend);
        writer.WritePropertyName("recent"u8);
        writer.WriteStartArray();
        foreach (double ratio in report.RecentRatios)
        {
            writer.WriteNumberValue(ratio);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();

        writer.WritePropertyName("transferCost"u8);
        writer.WriteStartObject();
        writer.WriteNumber("gas"u8, TransferGas);
        writer.WritePropertyName("wei"u8);
        McpToolExecutor.WriteValue(writer, report.TransferWei);
        writer.WriteString("formatted"u8, McpEthHelpers.FormatNative(report.TransferWei));
        writer.WriteString("symbol"u8, report.Symbol);
        writer.WriteEndObject();

        writer.WritePropertyName("blob"u8);
        writer.WriteStartObject();
        writer.WriteBoolean("available"u8, report.Blob is not null);
        if (report.Blob is { } blob)
        {
            WriteWei(writer, "baseFeePerBlobGas", blob.Current);
            WriteWei(writer, "nextBaseFeePerBlobGas", blob.Next);
            writer.WriteNumber("blobGasUsedRatioAverage"u8, blob.AverageRatio);
            writer.WritePropertyName("costPerBlob"u8);
            McpToolExecutor.WriteValue(writer, blob.CostPerBlob);
            writer.WriteString("costPerBlobFormatted"u8, McpEthHelpers.FormatNative(blob.CostPerBlob));
        }

        writer.WriteEndObject();
        writer.WriteString("summary"u8, Summarize(report));
        writer.WriteEndObject();
    }

    private static string Summarize(FeeReport report)
    {
        StringBuilder text = new();
        text.Append(CultureInfo.InvariantCulture, $"{report.NetworkName} ({report.Symbol}), last {report.Blocks} blocks up to #{report.NewestBlock}: ");
        if (report.Eip1559)
        {
            text.Append(CultureInfo.InvariantCulture, $"next block base fee {McpEthHelpers.Gwei(report.NextBaseFee)} gwei (current {McpEthHelpers.Gwei(report.CurrentBaseFee)} gwei). ");
        }
        else
        {
            text.Append(CultureInfo.InvariantCulture, $"EIP-1559 is not active for the next block, so use a legacy gasPrice of about {McpEthHelpers.Gwei(report.Standard.Priority)} gwei. ");
        }

        text.Append(CultureInfo.InvariantCulture, $"Blocks were {report.AverageRatio * 100:0.#}% full on average and usage is {report.Trend}. ");
        text.Append(CultureInfo.InvariantCulture,
            $"Suggested priority fee (tip) slow/standard/fast: {McpEthHelpers.Gwei(report.Slow.Priority)} / {McpEthHelpers.Gwei(report.Standard.Priority)} / {McpEthHelpers.Gwei(report.Fast.Priority)} gwei; ");
        text.Append(CultureInfo.InvariantCulture, $"standard maxFeePerGas {McpEthHelpers.Gwei(report.Standard.MaxFee)} gwei. ");
        text.Append(CultureInfo.InvariantCulture,
            $"A plain {TransferGas:N0}-gas transfer costs about {McpEthHelpers.FormatNative(report.TransferWei)} {report.Symbol} at the standard tip.");
        if (report.Blob is { } blob)
        {
            text.Append(CultureInfo.InvariantCulture,
                $" Blob base fee {McpEthHelpers.Gwei(blob.Next)} gwei per blob gas (about {McpEthHelpers.FormatNative(blob.CostPerBlob)} {report.Symbol} per blob).");
        }

        return text.ToString();
    }

    private static void WriteTier(Utf8JsonWriter writer, ReadOnlySpan<byte> name, FeeTier tier)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        writer.WriteNumber("percentile"u8, tier.Percentile);
        WriteWei(writer, "maxPriorityFeePerGas", tier.Priority);
        WriteWei(writer, "maxFeePerGas", tier.MaxFee);
        writer.WriteEndObject();
    }

    private static void WriteWei(Utf8JsonWriter writer, string name, in UInt256 wei)
    {
        writer.WritePropertyName(name);
        McpToolExecutor.WriteValue(writer, wei);
        writer.WriteString(name + "Gwei", McpEthHelpers.Gwei(wei));
    }

    private static string Trend(ArrayPoolList<double> ratios)
    {
        int count = ratios.Count;
        if (count < 2)
        {
            return "stable";
        }

        int half = count / 2;
        double older = 0;
        double newer = 0;
        for (int i = 0; i < half; i++)
        {
            older += ratios[i];
        }

        for (int i = count - half; i < count; i++)
        {
            newer += ratios[i];
        }

        double delta = (newer - older) / half;
        return delta > TrendThreshold ? "rising" : delta < -TrendThreshold ? "falling" : "stable";
    }

    private static UInt256 Median(List<UInt256> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }

    private static UInt256? Ok(ResultWrapper<UInt256?> result) =>
        result.Result.ResultType == ResultType.Success ? result.Data : null;

    private static string Word(in UInt256 value) => "0x" + Convert.ToHexStringLower(value.ToBigEndian());

    private readonly record struct ReceiptPage(ReceiptForRpc[] Receipts, ulong Number, int Offset, int Limit, long ByteBudget, CancellationToken Token);

    private readonly record struct FeeTier(double Percentile, UInt256 Priority, UInt256 MaxFee);

    private sealed record BlobFees(UInt256 Current, UInt256 Next, double AverageRatio, UInt256 CostPerBlob);

    private sealed record FeeReport(
        string NetworkName,
        string Symbol,
        bool Eip1559,
        ulong OldestBlock,
        ulong NewestBlock,
        int Blocks,
        double[] Percentiles,
        UInt256 CurrentBaseFee,
        UInt256 NextBaseFee,
        UInt256 GasPrice,
        UInt256 OraclePriority,
        FeeTier Slow,
        FeeTier Standard,
        FeeTier Fast,
        double AverageRatio,
        string Trend,
        double[] RecentRatios,
        UInt256 TransferWei,
        BlobFees? Blob);
}
