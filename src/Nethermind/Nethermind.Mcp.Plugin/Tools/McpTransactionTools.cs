// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Exceptions;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Mcp.Plugin.Tools.Abi;
using ResultType = Nethermind.Core.ResultType;

namespace Nethermind.Mcp.Plugin.Tools;

/// <summary>
/// Task-level transaction tools: <c>explain_transaction</c>, <c>simulate_transaction</c> and <c>block_summary</c>.
/// </summary>
/// <remarks>
/// These tools combine several JSON-RPC reads (transaction, receipt, header, call trace, token metadata) into one
/// human-oriented answer. Optional parts (trace, receipts, token metadata) degrade into <c>notes</c> instead of failing
/// the call, so a pruned or partially synced node still answers what it can.
/// </remarks>
/// <param name="executor">Runs tool bodies against rented modules under the MCP limits.</param>
/// <param name="config">The MCP limits.</param>
/// <param name="rpcConfig">The JSON-RPC configuration; its gas cap also bounds simulations.</param>
/// <param name="profile">The chain profile: native currency and well-known contracts.</param>
/// <param name="capabilities">Reports which blocks still have state, bodies and receipts.</param>
/// <param name="tokenMetadata">Reads token symbols and decimals.</param>
/// <param name="specProvider">Tells where base fees go (burnt, or a fee collector on Gnosis).</param>
[McpServerToolType]
internal sealed class McpTransactionTools(
    McpToolExecutor executor,
    IMcpConfig config,
    IJsonRpcConfig rpcConfig,
    McpChainProfile profile,
    McpNodeCapabilities capabilities,
    McpTokenMetadata tokenMetadata,
    ISpecProvider specProvider) : IMcpToolSet
{
    /// <summary>The maximum number of distinct tokens whose metadata one call reads.</summary>
    public const int MaxTokenLookups = 20;

    /// <summary>The maximum number of token transfers listed by <c>explain_transaction</c>.</summary>
    public const int MaxTokenTransfers = 50;

    /// <summary>The maximum number of internal native transfers listed by <c>explain_transaction</c>.</summary>
    public const int MaxInternalTransfers = 50;

    /// <summary>The maximum number of calls <c>simulate_transaction</c> accepts.</summary>
    public const int MaxSimulateCalls = 8;

    /// <summary>The maximum number of accounts in <c>stateOverrides</c>.</summary>
    public const int MaxOverrideAccounts = 8;

    /// <summary>The maximum number of storage slots per account in <c>stateOverrides</c>.</summary>
    public const int MaxOverrideSlots = 64;

    /// <summary>The maximum override code size, the EIP-170 contract size limit.</summary>
    public const int MaxOverrideCodeSize = 24_576;

    /// <summary>The maximum number of logs listed per simulated call.</summary>
    public const int MaxSimulateLogs = 50;

    /// <summary>The most characters of a revert string quoted in a summary sentence.</summary>
    private const int MaxSummaryRevertLength = 120;

    /// <summary>The number of entries in each <c>block_summary</c> top list.</summary>
    public const int BlockTopCount = 10;

    private const int MaxReturnDataBytes = 1024;
    private const int MaxAuthorizations = 16;
    private const int MaxNetFlows = 20;
    private const ulong IntrinsicTransferGas = 21_000;

    private const string AmountSchema = """{"type":"object","properties":{"wei":{"type":"string"},"formatted":{"type":"string"},"symbol":{"type":"string"}},"required":["wei","formatted","symbol"]}""";

    private const string ExplainOutputSchema = """
        {"type":"object","properties":{"result":{"type":"object","properties":{
          "hash":{"type":"string"},
          "status":{"type":"string","enum":["success","failed","pending","unknown"]},
          "summary":{"type":"string"},
          "block":{"type":"object","properties":{"number":{"type":"integer"},"hash":{"type":"string"},"timestamp":{"type":"integer"},"timestampIso":{"type":"string"},"transactionIndex":{"type":"integer"}}},
          "from":{"type":"string"},"to":{"type":"string"},"toLabel":{"type":"string"},"contractCreated":{"type":"string"},
          "type":{"type":"object","properties":{"id":{"type":"integer"},"name":{"type":"string"}},"required":["id","name"]},
          "nonce":{"type":"integer"},
          "value":
        """ + AmountSchema + """
        ,
          "method":{"type":"object","properties":{"selector":{"type":"string"},"signature":{"type":"string"},"name":{"type":"string"}},"required":["selector"]},
          "inputSize":{"type":"integer"},
          "fees":{"type":"object"},
          "authorizations":{"type":"object"},
          "tokenTransfers":{"type":"array","items":{"type":"object"}},
          "tokenTransfersOmitted":{"type":"integer"},
          "netTokenFlows":{"type":"array","items":{"type":"object"}},
          "internalTransfers":{"type":"array","items":{"type":"object"}},
          "internalTransfersTotal":{"type":"integer"},
          "failure":{"type":"object"},
          "logCount":{"type":"integer"},
          "notes":{"type":"array","items":{"type":"string"}}},
          "required":["hash","status","summary","from","type","value","notes"]}},
        "required":["result"]}
        """;

    private const string SimulateOutputSchema = """
        {"type":"object","properties":{"result":{"type":"object","properties":{
          "status":{"type":"string","enum":["success","reverted","failed"]},
          "summary":{"type":"string"},
          "broadcast":{"type":"boolean"},
          "baseBlock":{"type":"object","properties":{"number":{"type":"integer"},"hash":{"type":"string"}}},
          "simulatedBlockNumber":{"type":"integer"},
          "calls":{"type":"array","items":{"type":"object","properties":{
            "index":{"type":"integer"},"status":{"type":"string"},"gasUsed":{"type":"integer"},
            "returnData":{"type":"string"},"returnDataSize":{"type":"integer"},"error":{"type":"object"},
            "logs":{"type":"array"},"logsOmitted":{"type":"integer"},"tokenTransfers":{"type":"array"},"tokenTransfersOmitted":{"type":"integer"},
            "nativeTransfers":{"type":"array"},"nativeTransfersOmitted":{"type":"integer"}},
            "required":["index","status"]}},
          "netTokenFlows":{"type":"array","items":{"type":"object"}},
          "notes":{"type":"array","items":{"type":"string"}}},
          "required":["status","summary","broadcast","calls","notes"]}},
        "required":["result"]}
        """;

    private const string BlockSummaryOutputSchema = """
        {"type":"object","properties":{"result":{"type":"object","properties":{
          "number":{"type":"integer"},"hash":{"type":"string"},"parentHash":{"type":"string"},
          "timestamp":{"type":"integer"},"timestampIso":{"type":"string"},"ageSeconds":{"type":"integer"},
          "feeRecipient":{"type":"object","properties":{"address":{"type":"string"},"label":{"type":"string"}}},
          "transactionCount":{"type":"integer"},
          "transactionTypes":{"type":"object"},
          "contractCreations":{"type":"integer"},
          "gasUsed":{"type":"integer"},"gasLimit":{"type":"integer"},"gasUsedPercent":{"type":"number"},
          "baseFeePerGas":{"type":"string"},"baseFeeGwei":{"type":"string"},
          "baseFees":{"type":"object"},
          "blobs":{"type":"object"},
          "withdrawals":{"type":"object"},
          "topRecipients":{"type":"array","items":{"type":"object"}},
          "receipts":{"type":["object","null"]},
          "summary":{"type":"string"},
          "notes":{"type":"array","items":{"type":"string"}}},
          "required":["number","hash","timestamp","timestampIso","transactionCount","gasUsed","gasLimit","summary","notes"]}},
        "required":["result"]}
        """;

    private readonly ulong _maxCallGas = Math.Min((ulong)Math.Max(1, config.MaxCallGas), rpcConfig.GasCap.EffectiveGasCap());
    private readonly int _maxCallDataSize = Math.Max(0, config.MaxCallDataSize);
    private readonly bool _tracingEnabled = config.EnableTracing;
    private readonly int _maxResultSize = Math.Max(1, config.MaxResultSize);
    private readonly TimeSpan _timeout = TimeSpan.FromMilliseconds(Math.Max(1, config.ToolTimeout));

    // explain_transaction degrades into notes before the executor's hard timeout would discard the whole answer: token metadata
    // stops at 30% of the tool timeout, the call trace gets at most half of it, and everything must be done by 85%.
    private readonly TimeSpan _tokenDeadline = TimeSpan.FromMilliseconds(Math.Max(1, config.ToolTimeout) * 0.3);
    private readonly TimeSpan _traceBudget = TimeSpan.FromMilliseconds(Math.Max(1, config.ToolTimeout) * 0.5);
    private readonly TimeSpan _explainDeadline = TimeSpan.FromMilliseconds(Math.Max(1, config.ToolTimeout) * 0.85);
    private readonly TimeSpan _minTraceTime = TimeSpan.FromMilliseconds(Math.Max(1, config.ToolTimeout) * 0.05);

    /// <inheritdoc/>
    public IEnumerable<McpServerTool> CreateServerTools() => McpToolFactory.Create(this, method => method.Name switch
    {
        nameof(SimulateTransaction) => $" Limits on this node: gas at most {_maxCallGas} per call, data at most {_maxCallDataSize} bytes per call, " +
            $"{MaxSimulateCalls} calls, {MaxOverrideAccounts} overridden accounts with {MaxOverrideSlots} slots each.",
        _ => string.Empty
    }, _maxResultSize);

    /// <summary>Explains what a transaction did in one call.</summary>
    [McpServerTool(Name = "explain_transaction", Title = "Explain transaction", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [McpToolOutputSchema(ExplainOutputSchema)]
    [Description("The \"what happened?\" tool: explains a transaction in one call. Returns a plain-English `summary` plus structured facts: " +
        "status (success/failed/pending), block number/hash/time (timestampIso), from, to (or the created contract), type (legacy, EIP-2930, " +
        "EIP-1559, blob, EIP-7702 with its authorization list), nonce, value in the native currency (ETH, or xDAI on Gnosis), the called method " +
        "(4-byte selector and name when well known), fees (gas used vs limit, effective gas price in gwei, total fee, base fee burnt, or sent to the " +
        "fee collector on Gnosis, vs priority tip, blob fees), token movements decoded from ERC-20/721/1155 and WETH events with symbols and " +
        "amounts and net flows per address, internal native transfers from a call trace, and for failed transactions the decoded revert reason, " +
        "the failing call frame and out-of-gas detection. Prefer it over get_transaction/get_transaction_receipt for questions like \"what did " +
        "this tx do\" or \"why did it fail\"; use trace_transaction for the full call tree. Parts the node cannot serve (pruned receipts or state, " +
        "tracing unavailable) are listed in `notes` instead of failing. Amounts: wei as hex strings with formatted decimals; gas values are integers.")]
    public Task<CallToolResult> ExplainTransaction(
        [Description("32-byte transaction hash: 0x followed by 64 hex characters.")] string hash,
        CancellationToken cancellationToken = default)
    {
        if (!McpToolInput.TryParseHash(hash, nameof(hash), out Hash256? txHash, out string? error))
        {
            return McpEthHelpers.InvalidInput(error);
        }

        return executor.ExecuteAsync("explain_transaction", nameof(IEthRpcModule.eth_getTransactionByHash), async (eth, token) =>
        {
            using ResultWrapper<TransactionForRpc?> txResult = eth.eth_getTransactionByHash(txHash);
            if (txResult.Result.ResultType != ResultType.Success)
            {
                return executor.Failure("explain_transaction", txResult);
            }

            if (txResult.Data is not LegacyTransactionForRpc tx)
            {
                return McpToolExecutor.Error(McpToolErrorCodes.NotFound,
                    $"Transaction {txHash} is not known to this node: it is neither in a canonical block nor in the mempool. " +
                    "Check the hash and the network (chain_info)." + capabilities.DescribeTransactionHistoryLimit());
            }

            ExplainContext context = new(txHash, tx);
            if (tx.BlockNumber is not { } blockNumber)
            {
                return executor.Success(ExplainPending(context));
            }

            await ExplainMinedAsync(eth, context, blockNumber, token);
            return executor.Success(Ordered(context.Json));
        }, cancellationToken);
    }

    /// <summary>Dry-runs one or more calls without broadcasting them.</summary>
    [McpServerTool(Name = "simulate_transaction", Title = "Simulate transaction", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [McpToolOutputSchema(SimulateOutputSchema)]
    [Description("Dry-runs a transaction (or a short sequence, such as approve then swap) on top of a block and reports what WOULD happen: " +
        "success or revert with the decoded reason, gas used, return data, decoded events, token transfers and native-currency transfers " +
        "(ETH, or xDAI on Gnosis). It is read-only: nothing is signed, broadcast or stored, and fees are not charged (gas price 0, no " +
        "signature, nonce or balance-for-gas checks), like eth_call. Uses eth_simulateV1 with transfer tracing. Give either a single call " +
        "(to, data, value, gas, from) or `calls`, an array of up to 8 objects {to, data?, value?, gas?, from?} executed in order in the same " +
        "simulated block, so later calls see earlier effects. Optional `stateOverrides` sets balance, nonce, code or individual storage slots " +
        "(stateDiff) for up to 8 accounts before execution, e.g. {\"0xabc...\": {\"balance\": \"0xde0b6b3a7640000\"}}. Amounts are wei as 0x-hex or decimal strings.")]
    public Task<CallToolResult> SimulateTransaction(
        [Description("Target address (0x followed by 40 hex characters). Required unless `calls` is given.")] string? to = null,
        [Description("ABI-encoded call data as 0x-prefixed hex (\"0x\" or omitted for a plain transfer).")] string? data = null,
        [Description("Sender address; defaults to the zero address. Also the default sender for entries of `calls`.")] string? from = null,
        [Description("Value to send in wei, as 0x-hex or decimal. Default 0.")] string? value = null,
        [Description("Gas limit per call, as 0x-hex or decimal; defaults to the block gas limit (capped by this node) split across the calls.")] string? gas = null,
        [Description(McpEthTools.BlockSelectorDescription + " The simulation runs on top of this block. Default \"latest\".")] string block = "latest",
        [Description("Optional state overrides: an object mapping up to 8 addresses to {balance?, nonce?, code?, stateDiff?}; stateDiff maps " +
            "32-byte storage slots to 32-byte values (0x hex). Full-state replacement and precompile moves are not supported.")] JsonElement? stateOverrides = null,
        [Description("Optional sequence of up to 8 calls {to, data?, value?, gas?, from?} to run in order instead of the single call.")] JsonElement[]? calls = null,
        CancellationToken cancellationToken = default)
    {
        Address? defaultSender = null;
        if ((from is not null && !McpToolInput.TryParseAddress(from, nameof(from), out defaultSender, out string? error))
            || !McpToolInput.TryParseBlock(block, nameof(block), out BlockParameter? blockParameter, out error)
            || !TryParseOverrides(stateOverrides, out Dictionary<Address, AccountOverride>? overrides, out error)
            || !TryParseCalls(to, data, value, gas, calls, defaultSender, out List<SimulateCallInput>? inputs, out error))
        {
            return McpEthHelpers.InvalidInput(error!);
        }

        return executor.ExecuteAsync("simulate_transaction", nameof(IEthRpcModule.eth_simulateV1), (eth, token) =>
            Task.FromResult(Simulate(eth, blockParameter, overrides, inputs, token)), cancellationToken);
    }

    /// <summary>Summarises a block.</summary>
    [McpServerTool(Name = "block_summary", Title = "Block summary", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [McpToolOutputSchema(BlockSummaryOutputSchema)]
    [Description("Summarises a block for a human: number, hash, time (timestampIso) and age, fee recipient (labelled when well known), " +
        "transaction count by type, contract creations, gas used vs limit (%), base fee in gwei, base fees burnt (or sent to the fee collector " +
        "on Gnosis) in the native currency (ETH, or xDAI on Gnosis), blob count and blob gas, withdrawals (count and total: ETH on Ethereum, " +
        "GNO on Gnosis where they are paid by the deposit contract, not in xDAI), the top 10 `to` addresses by transaction count and, from " +
        "receipts, failed transactions, total and priority fees and the top 10 tokens by transfer count. Includes a plain-English `summary`. " +
        "Prefer it over get_block when the question is \"what happened in this block\". Receipt-based parts are omitted with a note when the " +
        "node has no receipts for the block.")]
    public Task<CallToolResult> BlockSummary(
        [Description(McpEthTools.BlockSelectorDescription)] string block,
        CancellationToken cancellationToken = default)
    {
        if (!McpToolInput.TryParseBlock(block, nameof(block), out BlockParameter? blockParameter, out string? error))
        {
            return McpEthHelpers.InvalidInput(error);
        }

        bool byHash = blockParameter.Type == BlockParameterType.BlockHash;
        return executor.ExecuteAsync("block_summary",
            byHash ? nameof(IEthRpcModule.eth_getBlockByHash) : nameof(IEthRpcModule.eth_getBlockByNumber),
            (eth, token) => Task.FromResult(SummarizeBlock(eth, blockParameter, token)), cancellationToken);
    }

    // ---- explain_transaction ----

    private JsonObject ExplainPending(ExplainContext context)
    {
        LegacyTransactionForRpc tx = context.Tx;
        JsonObject json = context.Json;
        json["status"] = "pending";
        WriteCommonFields(context);

        JsonObject fees = new() { ["gasLimit"] = tx.Gas ?? 0 };
        UInt256 maxPrice = tx is EIP1559TransactionForRpc { MaxFeePerGas: { } maxFee } ? maxFee : tx.GasPrice ?? UInt256.Zero;
        if (tx is EIP1559TransactionForRpc eip1559)
        {
            if (eip1559.MaxFeePerGas is { } mf) fees["maxFeePerGasGwei"] = McpEthHelpers.Gwei(mf);
            if (eip1559.MaxPriorityFeePerGas is { } mp) fees["maxPriorityFeePerGasGwei"] = McpEthHelpers.Gwei(mp);
        }
        else if (tx.GasPrice is { } gasPrice)
        {
            fees["gasPriceGwei"] = McpEthHelpers.Gwei(gasPrice);
        }

        UInt256 maxCost = maxPrice * (UInt256)(tx.Gas ?? 0);
        if (tx is BlobTransactionForRpc { MaxFeePerBlobGas: { } maxBlobPrice } && context.BlobCount > 0)
        {
            UInt256 maxBlobFee = maxBlobPrice * (UInt256)((ulong)context.BlobCount * Eip4844Constants.GasPerBlob);
            fees["maxFeePerBlobGasGwei"] = McpEthHelpers.Gwei(maxBlobPrice);
            fees["maxBlobFee"] = Amount(maxBlobFee);
            maxCost += maxBlobFee;
        }

        fees["maxFee"] = Amount(maxCost);
        json["fees"] = fees;
        context.Notes.Add("The transaction is in this node's mempool and has not been mined; status, fees paid and effects are not known yet. Use simulate_transaction to preview it.");

        StringBuilder summary = new();
        summary.Append("Pending in the mempool: ").Append(tx.From is null ? "unknown sender" : McpTxFormat.Short(tx.From)).Append(' ');
        AppendAction(summary, context, null, [], []);
        summary.Append("; it can pay at most ").Append(Human(maxCost)).Append(" in fees.");
        json["summary"] = summary.ToString();
        json["notes"] = NotesJson(context.Notes);
        return Ordered(json);
    }

    private async Task ExplainMinedAsync(IEthRpcModule eth, ExplainContext context, ulong blockNumber, CancellationToken token)
    {
        LegacyTransactionForRpc tx = context.Tx;
        JsonObject json = context.Json;

        using ResultWrapper<BlockHeaderForRpc?> headerResult = eth.eth_getHeaderByNumber(new BlockParameter(blockNumber));
        BlockHeaderForRpc? header = headerResult.Result.ResultType == ResultType.Success ? headerResult.Data : null;
        ulong timestamp = tx.BlockTimestamp ?? (header is null ? 0 : (ulong)header.Timestamp);
        json["block"] = new JsonObject
        {
            ["number"] = blockNumber,
            ["hash"] = tx.BlockHash?.ToString(),
            ["timestamp"] = timestamp,
            ["timestampIso"] = McpTxFormat.Iso(timestamp),
            ["transactionIndex"] = tx.TransactionIndex
        };

        WriteCommonFields(context);

        ReceiptForRpc? receipt = null;
        if (capabilities.CheckReceipts((long)blockNumber) is { } receiptsUnavailable)
        {
            context.Notes.Add($"Receipt unavailable: {ErrorMessage(receiptsUnavailable)} Status, fees and token movements are unknown.");
        }
        else
        {
            using ResultWrapper<ReceiptForRpc?> receiptResult = eth.eth_getTransactionReceipt(context.Hash);
            receipt = receiptResult.Result.ResultType == ResultType.Success ? receiptResult.Data : null;
            if (receipt is null)
            {
                context.Notes.Add("The node returned no receipt for this transaction (receipts may be pruned); status, fees and token movements are unknown.");
            }
        }

        bool? succeeded = receipt?.Status switch
        {
            1 => true,
            0 => false,
            _ => null
        };
        json["status"] = succeeded switch { true => "success", false => "failed", null => "unknown" };
        if (receipt is not null && succeeded is null)
        {
            context.Notes.Add("This pre-Byzantium receipt carries a state root instead of a status, so success cannot be read from it.");
        }

        if (receipt?.ContractAddress is { } created)
        {
            json["contractCreated"] = McpEthHelpers.Checksum(created);
        }

        if (receipt is not null)
        {
            json["fees"] = Fees(tx, receipt, header, blockNumber, timestamp);
            json["logCount"] = receipt.Logs?.Length ?? 0;
        }

        // Token movements from the receipt's logs.
        List<McpTokenMovement> movements = [];
        if (receipt?.Logs is { } logs)
        {
            foreach (LogEntryForRpc log in logs)
            {
                McpTxTokens.Extract(log.ToLogEntry(), movements, profile.WrappedNativeToken);
            }
        }

        Dictionary<AddressAsKey, McpTokenInfo> tokens = [];
        if (movements.Count > 0)
        {
            (tokens, int skipped) = McpTxTokens.LookUp(tokenMetadata, eth, TokensOf(movements), MaxTokenLookups, token, () => context.Clock.Elapsed > _tokenDeadline);
            if (skipped > 0)
            {
                context.Notes.Add($"Token metadata was not read for {skipped} token(s) (at most {MaxTokenLookups} per call, within the time budget); they show raw amounts.");
            }

            JsonArray transfers = [];
            for (int i = 0; i < movements.Count && i < MaxTokenTransfers; i++)
            {
                tokens.TryGetValue(movements[i].Token, out McpTokenInfo? info);
                transfers.Add(McpTxTokens.MovementJson(movements[i], info));
            }

            json["tokenTransfers"] = transfers;
            if (movements.Count > MaxTokenTransfers) json["tokenTransfersOmitted"] = movements.Count - MaxTokenTransfers;
            JsonArray flows = McpTxTokens.NetFlows(movements, tokens, MaxNetFlows);
            if (flows.Count > 0) json["netTokenFlows"] = flows;
        }

        // The call trace gives internal transfers and the failing frame; skip it when no code can have run.
        TraceFacts? trace = null;
        bool plainTransfer = tx.To is not null && (tx.Input is null || tx.Input.Length == 0)
            && receipt is not null && receipt.GasUsed == IntrinsicTransferGas && (receipt.Logs?.Length ?? 0) == 0;
        if (!plainTransfer)
        {
            trace = await TraceAsync(context, blockNumber, token);
        }

        if (trace is not null)
        {
            JsonArray internals = [];
            foreach (McpValueTransfer transfer in trace.Transfers)
            {
                internals.Add(new JsonObject
                {
                    ["type"] = transfer.Type,
                    ["from"] = McpEthHelpers.Checksum(transfer.From),
                    ["to"] = McpEthHelpers.Checksum(transfer.To),
                    ["value"] = McpTxFormat.Hex(transfer.Value),
                    ["valueFormatted"] = McpEthHelpers.FormatNative(transfer.Value),
                    ["depth"] = transfer.Depth
                });
            }

            json["internalTransfers"] = internals;
            json["internalTransfersTotal"] = trace.TransfersTotal;
        }

        if (succeeded == false)
        {
            json["failure"] = Failure(context, receipt!, trace);
        }

        json["summary"] = Summarize(context, receipt, succeeded, blockNumber, movements, tokens, trace);
        json["notes"] = NotesJson(context.Notes);
    }

    private void WriteCommonFields(ExplainContext context)
    {
        LegacyTransactionForRpc tx = context.Tx;
        JsonObject json = context.Json;
        json["from"] = tx.From is null ? null : McpEthHelpers.Checksum(tx.From);
        if (tx.To is not null)
        {
            json["to"] = McpEthHelpers.Checksum(tx.To);
            if (Label(tx.To) is { } label) json["toLabel"] = label;
        }

        TxType type = tx.Type ?? TxType.Legacy;
        json["type"] = new JsonObject { ["id"] = (int)type, ["name"] = TypeName(type) };
        json["nonce"] = tx.Nonce;
        json["value"] = Amount(tx.Value ?? UInt256.Zero);

        byte[] input = tx.Input ?? [];
        json["inputSize"] = input.Length;
        if (tx.To is not null && input.Length >= 4)
        {
            JsonObject method = new() { ["selector"] = McpTxFormat.HexPrefix(input, 4) };
            if (McpTxMethods.TryName(input) is { } signature)
            {
                method["signature"] = signature;
                method["name"] = McpTxMethods.ShortName(signature);
            }

            json["method"] = method;
        }

        if (tx is SetCodeTransactionForRpc { AuthorizationList: { } authorizations })
        {
            JsonArray entries = [];
            int count = 0;
            foreach (AuthorizationListForRpc.RpcAuthTuple tuple in authorizations)
            {
                if (count++ < MaxAuthorizations)
                {
                    entries.Add(new JsonObject
                    {
                        ["chainId"] = tuple.ChainId.ToString(),
                        ["delegateTo"] = McpEthHelpers.Checksum(tuple.Address),
                        ["revokes"] = tuple.Address == Address.Zero,
                        ["nonce"] = tuple.Nonce
                    });
                }
            }

            json["authorizations"] = new JsonObject { ["count"] = count, ["entries"] = entries };
            context.Notes.Add("EIP-7702: each authorization lets its signer's account run the delegateTo contract's code (the zero address revokes a delegation). Authorities whose signature or nonce is invalid are skipped by the protocol without failing the transaction.");
        }

        if (tx is BlobTransactionForRpc { BlobVersionedHashes: { } blobHashes })
        {
            context.BlobCount = blobHashes.Length;
        }
    }

    internal JsonObject Fees(LegacyTransactionForRpc tx, ReceiptForRpc receipt, BlockHeaderForRpc? header, ulong blockNumber, ulong timestamp)
    {
        ulong gasLimit = tx.Gas ?? 0;
        ulong gasUsed = receipt.GasUsed;
        UInt256 price = receipt.EffectiveGasPrice ?? tx.GasPrice ?? UInt256.Zero;
        UInt256 total = price * (UInt256)gasUsed;
        UInt256 blobFee = BlobFee(receipt);

        JsonObject fees = new()
        {
            ["gasLimit"] = gasLimit,
            ["gasUsed"] = gasUsed,
            ["gasUsedPercent"] = McpTxFormat.Percent(gasUsed, gasLimit),
            ["effectiveGasPrice"] = McpTxFormat.Hex(price),
            ["effectiveGasPriceGwei"] = McpEthHelpers.Gwei(price),
            ["total"] = Amount(total + blobFee)
        };

        if (!blobFee.IsZero)
        {
            // The blob fee is burnt separately from execution gas, so the total is split into both parts.
            fees["executionFee"] = Amount(total);
        }

        if (tx is EIP1559TransactionForRpc eip1559)
        {
            if (eip1559.MaxFeePerGas is { } mf) fees["maxFeePerGasGwei"] = McpEthHelpers.Gwei(mf);
            if (eip1559.MaxPriorityFeePerGas is { } mp) fees["maxPriorityFeePerGasGwei"] = McpEthHelpers.Gwei(mp);
        }

        if (header?.BaseFeePerGas is { } baseFee)
        {
            UInt256 baseFeePaid = baseFee * (UInt256)gasUsed;
            UInt256 tip = total > baseFeePaid ? total - baseFeePaid : UInt256.Zero;
            Address? collector = specProvider.GetSpec(blockNumber, timestamp).FeeCollector;
            fees["baseFeePerGasGwei"] = McpEthHelpers.Gwei(baseFee);
            JsonObject baseFeeJson = Amount(baseFeePaid);
            baseFeeJson["destination"] = collector is null ? "burnt" : $"fee collector {McpEthHelpers.Checksum(collector)}";
            fees["baseFee"] = baseFeeJson;
            fees["priorityFee"] = Amount(tip);
        }
        else
        {
            // Before London the whole fee went to the block producer.
            fees["priorityFee"] = Amount(total);
        }

        if (receipt.BlobGasUsed is { } blobGasUsed && blobGasUsed > 0)
        {
            UInt256 blobPrice = receipt.BlobGasPrice ?? UInt256.Zero;
            fees["blob"] = new JsonObject
            {
                ["blobGasUsed"] = blobGasUsed,
                ["blobGasPriceGwei"] = McpEthHelpers.Gwei(blobPrice),
                ["fee"] = Amount(blobFee)
            };
        }

        return fees;
    }

    /// <summary>Returns everything a mined transaction paid: execution gas at the effective price plus the EIP-4844 blob fee.</summary>
    internal static UInt256 PaidFee(LegacyTransactionForRpc tx, ReceiptForRpc receipt) =>
        (receipt.EffectiveGasPrice ?? tx.GasPrice ?? UInt256.Zero) * (UInt256)receipt.GasUsed + BlobFee(receipt);

    private static UInt256 BlobFee(ReceiptForRpc receipt) =>
        receipt is { BlobGasUsed: { } blobGas and > 0, BlobGasPrice: { } blobPrice } ? blobPrice * (UInt256)blobGas : UInt256.Zero;

    private async Task<TraceFacts?> TraceAsync(ExplainContext context, ulong blockNumber, CancellationToken token)
    {
        if (!_tracingEnabled)
        {
            context.Notes.Add("Call trace skipped: tracing is disabled on this node (Mcp.EnableTracing=false). Internal transfers and the failing call frame are unknown.");
            return null;
        }

        if (blockNumber > 0 && capabilities.CheckState((long)blockNumber - 1) is { } stateUnavailable)
        {
            context.Notes.Add($"Call trace skipped: {ErrorMessage(stateUnavailable)} Internal transfers and the failing call frame are unknown.");
            return null;
        }

        token.ThrowIfCancellationRequested();
        TimeSpan traceTimeout = TraceTimeout(context.Clock.Elapsed);
        if (traceTimeout < _minTraceTime)
        {
            context.Notes.Add("Call trace skipped: the rest of this call used up the time budget. Use trace_transaction for internal transfers and the failing frame.");
            return null;
        }

        // The native tracer is bounded only by JsonRpc.Timeout, so it runs on its own task and the answer stops waiting for it
        // after the trace budget; an abandoned trace keeps its debug module until it finishes and stays tracked for shutdown.
        Task<(bool Available, TraceFacts? Facts, IResultWrapper? Failure)> trace = Task.Run(() => RunTraceAsync(context.Hash), CancellationToken.None);
        using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            Task finished = await Task.WhenAny(trace, Task.Delay(traceTimeout, wait.Token));
            await wait.CancelAsync();
            if (finished != trace)
            {
                executor.TrackDetached(trace);
                token.ThrowIfCancellationRequested();
                context.Notes.Add($"Call trace did not finish within {(long)traceTimeout.TotalMilliseconds} ms and was left out. Use trace_transaction for internal transfers and the failing frame.");
                return null;
            }

            (bool available, TraceFacts? facts, IResultWrapper? failure) = await trace;
            if (!available)
            {
                context.Notes.Add("Call trace skipped: the debug module is not available on this node.");
                return null;
            }

            if (failure is not null)
            {
                context.Notes.Add($"Call trace failed: {McpToolExecutor.SanitizeMessage(failure.Result.Error)}");
                return null;
            }

            return facts;
        }
        catch (Exception ex) when (ex is ModuleRentalTimeoutException or LimitExceededException)
        {
            context.Notes.Add("Call trace skipped: the node's debug modules are busy; retry later for internal transfers and the failing frame.");
            return null;
        }
    }

    private async Task<(bool Available, TraceFacts? Facts, IResultWrapper? Failure)> RunTraceAsync(Hash256 hash)
    {
        using ModuleLease<IDebugRpcModule> debug = await executor.RentAsync<IDebugRpcModule>(nameof(IDebugRpcModule.debug_traceTransaction));
        if (debug.Module is null)
        {
            return (false, null, null);
        }

        (TraceFacts? facts, IResultWrapper? failure) = McpTxCallTree.Run(debug.Module, hash, _timeout, static root =>
        {
            if (root is null)
            {
                return new TraceFacts([], 0, null, null);
            }

            List<McpValueTransfer> transfers = [];
            int total = McpTxCallTree.CollectValueTransfers(root, transfers, MaxInternalTransfers);
            return new TraceFacts(transfers, total, McpTxCallTree.FindFailingFrame(root), root.Error);
        });
        return (true, facts, failure);
    }

    /// <summary>Returns the time the call trace of <c>explain_transaction</c> may take after <paramref name="elapsed"/> of the call.</summary>
    /// <remarks>At most half of the tool timeout, and never past 85% of it, so a slow trace ends in a note instead of a lost result.</remarks>
    internal TimeSpan TraceTimeout(TimeSpan elapsed)
    {
        TimeSpan left = _explainDeadline - elapsed;
        return left < _traceBudget ? left : _traceBudget;
    }

    private static JsonObject Failure(ExplainContext context, ReceiptForRpc receipt, TraceFacts? trace)
    {
        ulong gasLimit = context.Tx.Gas ?? 0;
        bool allGasUsed = gasLimit > 0 && receipt.GasUsed >= gasLimit;
        string outOfGas = EvmExceptionType.OutOfGas.GetEvmExceptionDescription()!;
        JsonObject failure = [];

        if (trace?.FailingFrame is { } frame)
        {
            bool frameOutOfGas = frame.Error == outOfGas || trace.RootError == outOfGas;
            failure["reason"] = trace.RootError;
            failure["outOfGas"] = frameOutOfGas;
            if (frame.Revert is not null) failure["revert"] = McpTxCallTree.RevertJson(frame.Revert);
            JsonObject frameJson = new() { ["depth"] = frame.Depth, ["type"] = frame.Type };
            if (frame.From is not null) frameJson["from"] = McpEthHelpers.Checksum(frame.From);
            if (frame.To is not null) frameJson["to"] = McpEthHelpers.Checksum(frame.To);
            if (frame.Selector is not null) frameJson["selector"] = frame.Selector;
            if (frame.Method is not null) frameJson["method"] = frame.Method;
            if (frame.Error is not null) frameJson["error"] = frame.Error;
            failure["frame"] = frameJson;
        }
        else
        {
            failure["outOfGas"] = allGasUsed;
            failure["outOfGasInferred"] = true;
            context.Notes.Add(allGasUsed
                ? "Without a call trace, out-of-gas is inferred from gas used equal to the gas limit (an invalid opcode also consumes all gas)."
                : "Without a call trace the revert reason cannot be recovered.");
        }

        return failure;
    }

    private string Summarize(ExplainContext context, ReceiptForRpc? receipt, bool? succeeded, ulong blockNumber,
        List<McpTokenMovement> movements, Dictionary<AddressAsKey, McpTokenInfo> tokens, TraceFacts? trace)
    {
        LegacyTransactionForRpc tx = context.Tx;
        StringBuilder summary = new();
        summary.Append(tx.From is null ? "Unknown sender" : McpTxFormat.Short(tx.From)).Append(' ');
        AppendAction(summary, context, receipt?.ContractAddress, succeeded == false ? [] : SenderMovements(tx.From, movements), tokens);

        if (succeeded != false && tx.From is not null)
        {
            AppendReceived(summary, tx.From, movements, tokens);
        }

        if (receipt is not null)
        {
            summary.Append(" and paid ").Append(Human(PaidFee(tx, receipt))).Append(" in fees");
            if (BlobFee(receipt) is { IsZero: false } blobFee)
            {
                summary.Append(" (").Append(Human(blobFee)).Append(" of it for blob gas)");
            }
        }

        // Bots and smart accounts move tokens from the called contract, not the sender: then its net flows tell the story.
        if (succeeded != false && tx.To is not null && (tx.From is null || McpTxTokens.NetFor(movements, tx.From).Count == 0))
        {
            AppendContractFlows(summary, tx.To, movements, tokens);
        }

        string blockText = $"block {McpTxFormat.Thousands(blockNumber)}";
        switch (succeeded)
        {
            case true:
                summary.Append("; succeeded in ").Append(blockText).Append('.');
                break;
            case false:
                summary.Append("; FAILED in ").Append(blockText);
                if (trace?.FailingFrame is { } frame)
                {
                    if (frame.Error == EvmExceptionType.OutOfGas.GetEvmExceptionDescription() || trace.RootError == EvmExceptionType.OutOfGas.GetEvmExceptionDescription())
                    {
                        summary.Append(" (out of gas)");
                    }
                    else if (frame.Revert is { } revert)
                    {
                        summary.Append(" (reverted: ").Append(SummaryRevert(revert)).Append(')');
                    }
                    else if (frame.Error is not null)
                    {
                        summary.Append(" (").Append(frame.Error).Append(')');
                    }
                }
                else if (receipt is not null && tx.Gas is { } limit && receipt.GasUsed >= limit)
                {
                    summary.Append(" (all gas used; likely out of gas)");
                }

                summary.Append(", so its effects were reverted (the fee was still paid).");
                break;
            default:
                summary.Append("; included in ").Append(blockText).Append(" (outcome unknown).");
                break;
        }

        return summary.ToString();
    }

    private void AppendAction(StringBuilder summary, ExplainContext context, Address? created, List<McpTokenMovement> sent,
        Dictionary<AddressAsKey, McpTokenInfo> tokens)
    {
        LegacyTransactionForRpc tx = context.Tx;
        UInt256 value = tx.Value ?? UInt256.Zero;
        byte[] input = tx.Input ?? [];

        if (tx.To is null)
        {
            summary.Append("deployed a contract");
            if (created is not null) summary.Append(" at ").Append(McpTxFormat.Short(created));
            if (!value.IsZero) summary.Append(" with ").Append(Human(value));
            return;
        }

        List<string> parts = [];
        if (!value.IsZero)
        {
            parts.Add($"sent {Human(value)} to {Display(tx.To)}");
        }

        int shown = 0;
        foreach (McpTokenMovement movement in sent)
        {
            if (shown++ == 2)
            {
                parts.Add($"{sent.Count - 2} more token transfers");
                break;
            }

            parts.Add($"sent {TokenText(movement, tokens)} to {Display(movement.To)}");
        }

        if (input.Length >= 4)
        {
            string? signature = McpTxMethods.TryName(input);
            string call = signature is null
                ? $"called method {McpTxFormat.HexPrefix(input, 4)} on {Display(tx.To)}"
                : $"called {McpTxMethods.ShortName(signature)} on {Display(tx.To)}";
            if (parts.Count == 0 || sent.Count == 0) parts.Insert(0, call);
        }
        else if (parts.Count == 0)
        {
            parts.Add($"sent 0 {profile.NativeCurrencySymbol} to {Display(tx.To)}");
        }

        summary.AppendJoin(", ", parts);
        if (context.BlobCount > 0) summary.Append($" carrying {context.BlobCount} blob(s)");
    }

    private void AppendReceived(StringBuilder summary, Address sender, List<McpTokenMovement> movements, Dictionary<AddressAsKey, McpTokenInfo> tokens)
    {
        int shown = 0;
        foreach (McpTokenMovement movement in movements)
        {
            if (movement.To != sender || movement.From == sender)
            {
                continue;
            }

            if (shown++ == 2)
            {
                break;
            }

            summary.Append(shown == 1 ? ", received " : " and ").Append(TokenText(movement, tokens));
        }
    }

    /// <summary>Appends what <paramref name="contract"/> gave and got in fungible tokens, such as <c>; 0xAb…12 swapped 100 USDC for 0.04 WETH</c>.</summary>
    internal void AppendContractFlows(StringBuilder summary, Address contract, List<McpTokenMovement> movements, Dictionary<AddressAsKey, McpTokenInfo> tokens)
    {
        const int maxPerSide = 2;
        List<string> gave = [];
        List<string> got = [];
        foreach ((Address token, BigInteger change) in McpTxTokens.NetFor(movements, contract))
        {
            List<string> side = change.Sign < 0 ? gave : got;
            if (side.Count < maxPerSide) side.Add(TokenAmountText(token, (UInt256)BigInteger.Abs(change), tokens));
        }

        if (gave.Count == 0 && got.Count == 0)
        {
            return;
        }

        summary.Append("; ").Append(Display(contract));
        if (gave.Count > 0 && got.Count > 0) summary.Append(" swapped ").AppendJoin(" and ", gave).Append(" for ").AppendJoin(" and ", got);
        else if (gave.Count > 0) summary.Append(" sent out ").AppendJoin(" and ", gave);
        else summary.Append(" received ").AppendJoin(" and ", got);
    }

    private static List<McpTokenMovement> SenderMovements(Address? sender, List<McpTokenMovement> movements)
    {
        List<McpTokenMovement> sent = [];
        foreach (McpTokenMovement movement in movements)
        {
            if (movement.From == sender)
            {
                sent.Add(movement);
            }
        }

        return sent;
    }

    private string TokenText(McpTokenMovement movement, Dictionary<AddressAsKey, McpTokenInfo> tokens) =>
        movement.IsFungible
            ? TokenAmountText(movement.Token, movement.Amount, tokens)
            : $"{movement.Standard} {TokenLabel(movement.Token, tokens)} #{movement.TokenId}";

    private string TokenAmountText(Address token, UInt256 amount, Dictionary<AddressAsKey, McpTokenInfo> tokens)
    {
        tokens.TryGetValue(token, out McpTokenInfo? info);
        string formatted = info?.Decimals is { } decimals ? McpTxFormat.Human(McpTokenMetadata.FormatUnits(amount, decimals)) : amount.ToString();
        return $"{formatted} {TokenLabel(token, tokens)}";
    }

    // A symbol is chosen by whoever deployed the token, so only well-known tokens are named without a warning.
    private string TokenLabel(Address token, Dictionary<AddressAsKey, McpTokenInfo> tokens)
    {
        tokens.TryGetValue(token, out McpTokenInfo? info);
        return info?.Symbol is { } symbol && !IsWellKnownToken(token)
            ? $"{symbol} (unverified token {McpTxFormat.Short(token)})"
            : McpTxTokens.Label(token, info);
    }

    // ---- simulate_transaction ----

    private CallToolResult Simulate(IEthRpcModule eth, BlockParameter blockParameter, Dictionary<Address, AccountOverride>? overrides,
        List<SimulateCallInput> inputs, CancellationToken token)
    {
        using ResultWrapper<BlockHeaderForRpc?> headerResult = eth.eth_getHeaderByNumber(blockParameter);
        if (headerResult.Result.ResultType != ResultType.Success)
        {
            return executor.Failure("simulate_transaction", headerResult);
        }

        if (headerResult.Data is not { Number: { } baseNumber } header)
        {
            return McpToolExecutor.Error(McpToolErrorCodes.NotFound, "'block': block not found.");
        }

        if (capabilities.CheckState((long)baseNumber) is { } stateUnavailable)
        {
            return stateUnavailable;
        }

        ulong defaultGas = Math.Max(1, Math.Min(_maxCallGas, header.GasLimit) / (ulong)inputs.Count);
        TransactionForRpc[] calls = new TransactionForRpc[inputs.Count];
        for (int i = 0; i < inputs.Count; i++)
        {
            SimulateCallInput input = inputs[i];
            calls[i] = new LegacyTransactionForRpc
            {
                From = input.From,
                To = input.To,
                Input = input.Data,
                Value = input.Value,
                Gas = input.Gas ?? defaultGas
            };
        }

        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls = [new BlockStateCall<TransactionForRpc> { StateOverrides = overrides, Calls = calls }],
            TraceTransfers = true,
            Validation = false
        };

        // Simulating on a hash selector pins the base block even if the head moves meanwhile.
        using ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result =
            eth.eth_simulateV1(payload, new BlockParameter(header.Hash!));
        if (result.Result.ResultType != ResultType.Success)
        {
            return SimulateFailure(result);
        }

        if (result.Data is not [{ } simulated, ..])
        {
            return McpToolExecutor.Error(McpToolErrorCodes.InternalError, "The simulation returned no block.");
        }

        token.ThrowIfCancellationRequested();
        return executor.Success(BuildSimulation(eth, header, simulated, inputs, token));
    }

    private JsonObject BuildSimulation(IEthRpcModule eth, BlockHeaderForRpc header, SimulateBlockResult<SimulateCallResult> simulated,
        List<SimulateCallInput> inputs, CancellationToken token)
    {
        List<JsonObject> callJsons = [];
        List<McpTokenMovement> allMovements = [];
        List<(int Call, List<McpTokenMovement> Movements)> perCall = [];
        List<string> notes = ["Simulation only: nothing was signed or broadcast, and gas was not charged."];
        string status = "success";
        int? firstFailure = null;
        string? failureText = null;
        ulong totalGas = 0;
        List<(Address From, Address To, UInt256 Value)> allNative = [];

        int index = 0;
        foreach (SimulateCallResult call in simulated.Calls)
        {
            SimulateCallInput input = inputs[Math.Min(index, inputs.Count - 1)];
            bool ok = call.Status == 1;
            JsonObject json = new() { ["index"] = index, ["status"] = ok ? "success" : "reverted" };
            json["to"] = McpEthHelpers.Checksum(input.To);
            if (McpTxMethods.TryName(input.Data ?? []) is { } signature) json["method"] = signature;
            if (call.GasUsed is { } gasUsed)
            {
                json["gasUsed"] = gasUsed;
                totalGas += gasUsed;
            }

            byte[] returnData = call.ReturnData ?? [];
            json["returnData"] = McpTxFormat.HexPrefix(returnData, MaxReturnDataBytes);
            json["returnDataSize"] = returnData.Length;

            if (!ok)
            {
                JsonObject error = new() { ["message"] = call.Error?.Message is { } callError ? McpToolExecutor.SanitizeMessage(callError) : "execution failed" };
                bool reverted = call.Error is null || call.Error.EvmException == EvmExceptionType.Revert;
                if (reverted)
                {
                    McpDecodedRevert revert = McpKnownAbi.DecodeRevert(call.Error?.Data ?? returnData);
                    error["revert"] = McpTxCallTree.RevertJson(revert);
                    failureText ??= $"reverted: {SummaryRevert(revert)}";
                }
                else
                {
                    json["status"] = "failed";
                    failureText ??= McpToolExecutor.SanitizeMessage(call.Error!.Message);
                }

                json["error"] = error;
                if (firstFailure is null)
                {
                    firstFailure = index;
                    status = reverted ? "reverted" : "failed";
                }
            }

            List<McpTokenMovement> movements = [];
            JsonArray logsJson = [];
            JsonArray native = [];
            int nativeOmitted = 0;
            int logCount = 0;
            foreach (Log log in call.Logs)
            {
                if (log.Address == TransferLog.Erc20Sender && log.Topics is [{ } topic0, { } fromTopic, { } toTopic, ..] && topic0 == TransferLog.TransferSignature)
                {
                    Address nativeFrom = new(fromTopic.Bytes[12..]);
                    Address nativeTo = new(toTopic.Bytes[12..]);
                    UInt256 amount = new(log.Data, isBigEndian: true);
                    allNative.Add((nativeFrom, nativeTo, amount));
                    if (native.Count >= MaxSimulateLogs)
                    {
                        nativeOmitted++;
                        continue;
                    }

                    native.Add(new JsonObject
                    {
                        ["from"] = McpEthHelpers.Checksum(nativeFrom),
                        ["to"] = McpEthHelpers.Checksum(nativeTo),
                        ["value"] = McpTxFormat.Hex(amount),
                        ["valueFormatted"] = McpEthHelpers.FormatNative(amount)
                    });
                    continue;
                }

                LogEntry entry = new(log.Address, log.Data, log.Topics);
                McpDecodedLog? decoded = McpTxTokens.Extract(entry, movements, profile.WrappedNativeToken);
                if (++logCount > MaxSimulateLogs)
                {
                    continue;
                }

                JsonObject logJson = new() { ["address"] = McpEthHelpers.Checksum(log.Address) };
                if (decoded is not null)
                {
                    foreach (KeyValuePair<string, JsonNode?> property in McpTxTokens.DecodedJson(decoded))
                    {
                        logJson[property.Key] = property.Value?.DeepClone();
                    }
                }
                else
                {
                    JsonArray topics = [];
                    foreach (Hash256 topic in log.Topics) topics.Add(topic.ToString());
                    logJson["topics"] = topics;
                    logJson["data"] = McpTxFormat.HexPrefix(log.Data, MaxReturnDataBytes);
                }

                logsJson.Add(logJson);
            }

            json["logs"] = logsJson;
            if (logCount > MaxSimulateLogs) json["logsOmitted"] = logCount - MaxSimulateLogs;
            if (native.Count > 0) json["nativeTransfers"] = native;
            if (nativeOmitted > 0) json["nativeTransfersOmitted"] = nativeOmitted;
            perCall.Add((index, movements));
            allMovements.AddRange(movements);
            callJsons.Add(json);
            index++;
        }

        Dictionary<AddressAsKey, McpTokenInfo> tokens = [];
        if (allMovements.Count > 0)
        {
            (tokens, int skipped) = McpTxTokens.LookUp(tokenMetadata, eth, TokensOf(allMovements), MaxTokenLookups, token);
            if (skipped > 0) notes.Add($"Metadata was read for the first {MaxTokenLookups} tokens only.");
            foreach ((int call, List<McpTokenMovement> movements) in perCall)
            {
                if (movements.Count == 0) continue;
                JsonArray transfers = [];
                foreach (McpTokenMovement movement in movements)
                {
                    if (transfers.Count >= MaxSimulateLogs) break;
                    tokens.TryGetValue(movement.Token, out McpTokenInfo? info);
                    transfers.Add(McpTxTokens.MovementJson(movement, info));
                }

                callJsons[call]["tokenTransfers"] = transfers;
                if (movements.Count > transfers.Count) callJsons[call]["tokenTransfersOmitted"] = movements.Count - transfers.Count;
            }
        }

        JsonArray callsArray = [];
        foreach (JsonObject callJson in callJsons) callsArray.Add(callJson);

        JsonObject result = new()
        {
            ["status"] = status,
            ["broadcast"] = false,
            ["baseBlock"] = new JsonObject { ["number"] = header.Number, ["hash"] = header.Hash?.ToString() },
            ["simulatedBlockNumber"] = simulated.Number,
            ["calls"] = callsArray
        };

        JsonArray flows = McpTxTokens.NetFlows(allMovements, tokens, MaxNetFlows);
        if (flows.Count > 0) result["netTokenFlows"] = flows;

        StringBuilder summary = new();
        summary.Append(callJsons.Count == 1 ? "The call" : $"The {callJsons.Count} calls");
        if (firstFailure is null)
        {
            summary.Append(callJsons.Count == 1 ? " would succeed" : " would all succeed");
        }
        else
        {
            summary.Append(callJsons.Count == 1 ? " would fail" : $" would fail at call {firstFailure}").Append(" (").Append(failureText).Append(')');
        }

        summary.Append(", using ").Append(McpTxFormat.Thousands(totalGas)).Append(" gas");
        if (firstFailure is null)
        {
            int described = 0;
            foreach ((Address nativeFrom, Address nativeTo, UInt256 amount) in allNative)
            {
                if (described++ == 2) break;
                summary.Append(", moving ").Append(Human(amount)).Append(" from ").Append(McpTxFormat.Short(nativeFrom)).Append(" to ").Append(Display(nativeTo));
            }

            described = 0;
            foreach (McpTokenMovement movement in allMovements)
            {
                if (described++ == 2) break;
                summary.Append(", ").Append(Display(movement.From)).Append(" sends ").Append(TokenText(movement, tokens)).Append(" to ").Append(Display(movement.To));
            }
        }

        summary.Append($"; simulated on top of block {McpTxFormat.Thousands(header.Number ?? 0)}. Nothing was broadcast.");
        result["summary"] = summary.ToString();
        result["notes"] = NotesJson(notes);
        return result;
    }

    private CallToolResult SimulateFailure(IResultWrapper result)
    {
        int code = result.ErrorCode;
        string message = result.Result.Error is { Length: > 0 } error ? McpToolExecutor.SanitizeMessage(error) : "The simulation failed.";
        if (code == ErrorCodes.ClientLimitExceededError)
        {
            return McpToolExecutor.Error(McpToolErrorCodes.ResourceExhausted, message);
        }

        if (message.StartsWith("No state available", StringComparison.Ordinal))
        {
            return McpToolExecutor.Error(McpToolErrorCodes.Unavailable, $"{message} The node has no state for that block; simulate on \"latest\".");
        }

        // Transaction-level rejections (nonce, intrinsic gas, funds, block gas) are caused by the request, so say so.
        return code is <= -38000 and > -39000
            ? McpToolExecutor.Error(McpToolErrorCodes.InvalidInput, message)
            : executor.Failure("simulate_transaction", result);
    }

    private bool TryParseCalls(string? to, string? data, string? value, string? gas, JsonElement[]? calls, Address? defaultSender,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out List<SimulateCallInput>? inputs, out string? error)
    {
        inputs = null;
        if (calls is not null)
        {
            if (to is not null || data is not null || value is not null || gas is not null)
            {
                error = "Give either a single call (to, data, value, gas) or 'calls', not both.";
                return false;
            }

            if (calls.Length is 0 or > MaxSimulateCalls)
            {
                error = $"'calls' must contain between 1 and {MaxSimulateCalls} entries.";
                return false;
            }

            List<SimulateCallInput> parsed = new(calls.Length);
            for (int i = 0; i < calls.Length; i++)
            {
                JsonElement call = calls[i];
                string name = $"calls[{i}]";
                if (call.ValueKind != JsonValueKind.Object)
                {
                    error = $"'{name}' must be an object {{to, data?, value?, gas?, from?}}.";
                    return false;
                }

                string? callTo = null, callData = null, callValue = null, callGas = null, callFrom = null;
                foreach (JsonProperty property in call.EnumerateObject())
                {
                    if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    {
                        error = $"'{name}.{property.Name}' must be a string.";
                        return false;
                    }

                    string? text = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                    switch (property.Name)
                    {
                        case "to": callTo = text; break;
                        case "data" or "input": callData = text; break;
                        case "value": callValue = text; break;
                        case "gas": callGas = text; break;
                        case "from": callFrom = text; break;
                        default:
                            error = $"'{name}' has an unknown property '{property.Name}'; allowed: to, data, value, gas, from.";
                            return false;
                    }
                }

                Address? sender = defaultSender;
                if (callFrom is not null && !McpToolInput.TryParseAddress(callFrom, $"{name}.from", out sender, out error))
                {
                    return false;
                }

                if (!TryParseCall(callTo, callData, callValue, callGas, sender, name + ".", out SimulateCallInput? input, out error))
                {
                    return false;
                }

                parsed.Add(input);
            }

            inputs = parsed;
            error = null;
            return true;
        }

        if (to is null)
        {
            error = "'to' is required (or give 'calls').";
            return false;
        }

        if (!TryParseCall(to, data, value, gas, defaultSender, string.Empty, out SimulateCallInput? single, out error))
        {
            return false;
        }

        inputs = [single];
        return true;
    }

    private bool TryParseCall(string? to, string? data, string? value, string? gas, Address? sender, string prefix,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SimulateCallInput? input, out string? error)
    {
        input = null;
        byte[]? callData = null;
        UInt256 callValue = UInt256.Zero;
        ulong gasLimit = 0;
        if (!McpToolInput.TryParseAddress(to, prefix + "to", out Address? target, out error)
            || (data is not null && !McpToolInput.TryParseData(data, prefix + "data", _maxCallDataSize, out callData, out error))
            || (value is not null && !McpToolInput.TryParseUInt256(value, prefix + "value", out callValue, out error))
            || (gas is not null && !McpToolInput.TryParseULong(gas, prefix + "gas", out gasLimit, out error)))
        {
            return false;
        }

        if (gas is not null && (gasLimit == 0 || gasLimit > _maxCallGas))
        {
            error = $"'{prefix}gas' must be between 1 and {_maxCallGas}.";
            return false;
        }

        input = new SimulateCallInput(sender, target, callData ?? [], callValue, gas is null ? null : gasLimit);
        return true;
    }

    private static bool TryParseOverrides(JsonElement? element, out Dictionary<Address, AccountOverride>? overrides, out string? error)
    {
        overrides = null;
        error = null;
        if (element is null || element.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return true;
        }

        if (element.Value.ValueKind != JsonValueKind.Object)
        {
            error = "'stateOverrides' must be an object mapping addresses to {balance?, nonce?, code?, stateDiff?}.";
            return false;
        }

        Dictionary<Address, AccountOverride> result = [];
        foreach (JsonProperty account in element.Value.EnumerateObject())
        {
            if (result.Count == MaxOverrideAccounts)
            {
                error = $"'stateOverrides' may override no more than {MaxOverrideAccounts} accounts.";
                return false;
            }

            string name = $"stateOverrides.{account.Name}";
            if (!McpToolInput.TryParseAddress(account.Name, "stateOverrides key", out Address? address, out error))
            {
                return false;
            }

            if (account.Value.ValueKind != JsonValueKind.Object)
            {
                error = $"'{name}' must be an object {{balance?, nonce?, code?, stateDiff?}}.";
                return false;
            }

            AccountOverride accountOverride = new();
            foreach (JsonProperty field in account.Value.EnumerateObject())
            {
                string fieldName = $"{name}.{field.Name}";
                switch (field.Name)
                {
                    case "balance":
                        if (!McpToolInput.TryParseUInt256(StringOf(field.Value), fieldName, out UInt256 balance, out error)) return false;
                        accountOverride.Balance = balance;
                        break;
                    case "nonce":
                        if (!McpToolInput.TryParseULong(StringOf(field.Value), fieldName, out ulong nonce, out error)) return false;
                        accountOverride.Nonce = nonce;
                        break;
                    case "code":
                        if (!McpToolInput.TryParseData(StringOf(field.Value), fieldName, MaxOverrideCodeSize, out byte[]? code, out error)) return false;
                        accountOverride.Code = code;
                        break;
                    case "stateDiff":
                        if (!TryParseStateDiff(field.Value, fieldName, out Dictionary<UInt256, Hash256>? diff, out error)) return false;
                        accountOverride.StateDiff = diff;
                        break;
                    default:
                        error = $"'{fieldName}' is not supported; only balance, nonce, code and stateDiff can be overridden.";
                        return false;
                }
            }

            if (!result.TryAdd(address, accountOverride))
            {
                error = $"'stateOverrides' lists {address} more than once.";
                return false;
            }
        }

        overrides = result.Count == 0 ? null : result;
        return true;
    }

    private static bool TryParseStateDiff(JsonElement element, string name, out Dictionary<UInt256, Hash256>? diff, out string? error)
    {
        diff = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            error = $"'{name}' must be an object mapping 32-byte slots to 32-byte values.";
            return false;
        }

        Dictionary<UInt256, Hash256> result = [];
        foreach (JsonProperty slot in element.EnumerateObject())
        {
            if (result.Count == MaxOverrideSlots)
            {
                error = $"'{name}' may set no more than {MaxOverrideSlots} slots.";
                return false;
            }

            if (!McpToolInput.TryParseUInt256(slot.Name, $"{name} key", out UInt256 key, out error)
                || !McpToolInput.TryParseUInt256(StringOf(slot.Value), $"{name}.{slot.Name}", out UInt256 word, out error))
            {
                return false;
            }

            result[key] = new Hash256(word.ToBigEndian());
        }

        diff = result;
        error = null;
        return true;
    }

    // ---- block_summary ----

    private CallToolResult SummarizeBlock(IEthRpcModule eth, BlockParameter blockParameter, CancellationToken token)
    {
        using ResultWrapper<BlockForRpc> blockResult = blockParameter.Type == BlockParameterType.BlockHash
            ? eth.eth_getBlockByHash(blockParameter.BlockHash!, true)
            : eth.eth_getBlockByNumber(blockParameter, true);
        if (blockResult.Result.ResultType != ResultType.Success)
        {
            return executor.Failure("block_summary", blockResult);
        }

        if (blockResult.Data is not { Number: { } number } block)
        {
            return McpEthHelpers.MissingBlock(eth, blockParameter, capabilities, "Block not found.");
        }

        List<string> notes = [];
        ulong timestamp = (ulong)block.Timestamp;
        long age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)Math.Min(timestamp, long.MaxValue);
        Address? feeRecipient = block.Miner ?? block.Author;

        JsonObject json = new()
        {
            ["number"] = number,
            ["hash"] = block.Hash?.ToString(),
            ["parentHash"] = block.ParentHash?.ToString(),
            ["timestamp"] = timestamp,
            ["timestampIso"] = McpTxFormat.Iso(timestamp),
            ["ageSeconds"] = Math.Max(0, age)
        };

        if (feeRecipient is not null)
        {
            JsonObject recipient = new() { ["address"] = McpEthHelpers.Checksum(feeRecipient) };
            if (Label(feeRecipient) is { } label) recipient["label"] = label;
            json["feeRecipient"] = recipient;
        }

        // Transactions: types, recipients and creations.
        object[] transactions = block.Transactions ?? [];
        SortedDictionary<string, int> types = new(StringComparer.Ordinal);
        Dictionary<AddressAsKey, int> recipients = [];
        int creations = 0;
        foreach (object item in transactions)
        {
            if (item is not TransactionForRpc tx)
            {
                continue;
            }

            string typeName = TypeName(tx.Type ?? TxType.Legacy);
            types[typeName] = types.TryGetValue(typeName, out int typeCount) ? typeCount + 1 : 1;
            if (tx is LegacyTransactionForRpc { To: { } to })
            {
                recipients[to] = recipients.TryGetValue(to, out int count) ? count + 1 : 1;
            }
            else
            {
                creations++;
            }
        }

        json["transactionCount"] = transactions.Length;
        JsonObject typesJson = [];
        foreach ((string typeName, int count) in types) typesJson[typeName] = count;
        json["transactionTypes"] = typesJson;
        json["contractCreations"] = creations;
        json["gasUsed"] = block.GasUsed;
        json["gasLimit"] = block.GasLimit;
        json["gasUsedPercent"] = McpTxFormat.Percent(block.GasUsed, block.GasLimit);

        IReleaseSpec spec = specProvider.GetSpec(number, timestamp);
        UInt256 baseFeesPaid = UInt256.Zero;
        if (block.BaseFeePerGas is { } baseFee)
        {
            baseFeesPaid = baseFee * (UInt256)block.GasUsed;
            json["baseFeePerGas"] = McpTxFormat.Hex(baseFee);
            json["baseFeeGwei"] = McpEthHelpers.Gwei(baseFee);
            JsonObject baseFees = Amount(baseFeesPaid);
            baseFees["destination"] = spec.FeeCollector is { } collector ? $"fee collector {McpEthHelpers.Checksum(collector)}" : "burnt";
            json["baseFees"] = baseFees;
        }

        if (block.BlobGasUsed is { } blobGasUsed)
        {
            json["blobs"] = new JsonObject
            {
                ["count"] = blobGasUsed / Eip4844Constants.GasPerBlob,
                ["blobGasUsed"] = blobGasUsed,
                ["excessBlobGas"] = block.ExcessBlobGas
            };
        }

        int withdrawalCount = 0;
        if (block.Withdrawals is { } withdrawals)
        {
            UInt256 totalGwei = UInt256.Zero;
            foreach (Withdrawal withdrawal in withdrawals)
            {
                totalGwei += withdrawal.AmountInGwei;
            }

            withdrawalCount = withdrawals.Length;
            json["withdrawals"] = Withdrawals(withdrawals.Length, totalGwei);
        }

        List<KeyValuePair<AddressAsKey, int>> top = [.. recipients];
        top.Sort(static (a, b) => b.Value.CompareTo(a.Value));
        JsonArray topRecipients = [];
        for (int i = 0; i < top.Count && i < BlockTopCount; i++)
        {
            JsonObject entry = new() { ["address"] = McpEthHelpers.Checksum(top[i].Key), ["transactions"] = top[i].Value };
            if (Label(top[i].Key) is { } label) entry["label"] = label;
            topRecipients.Add(entry);
        }

        json["topRecipients"] = topRecipients;

        JsonObject? receipts = null;
        if (transactions.Length > 0)
        {
            if (capabilities.CheckReceipts((long)number) is { } receiptsUnavailable)
            {
                notes.Add($"Receipts unavailable: {ErrorMessage(receiptsUnavailable)} Failed transactions, fees and token transfers are omitted.");
            }
            else
            {
                // By hash, so a non-canonical block passed by hash gets its own receipts, not the canonical ones at its height.
                receipts = ReceiptStats(eth, block.Hash is { } blockHash ? new BlockParameter(blockHash) : new BlockParameter(number), block.BaseFeePerGas, notes, token);
            }
        }

        json["receipts"] = receipts;

        StringBuilder summary = new();
        summary.Append("Block ").Append(McpTxFormat.Thousands(number)).Append(" (").Append(McpTxFormat.Iso(timestamp)).Append(") has ")
            .Append(McpTxFormat.Thousands((ulong)transactions.Length)).Append(transactions.Length == 1 ? " transaction" : " transactions")
            .Append(" using ").Append(McpTxFormat.Percent(block.GasUsed, block.GasLimit).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append("% of the gas limit");
        if (block.BaseFeePerGas is { } fee)
        {
            summary.Append(" at a base fee of ").Append(McpTxFormat.Human(McpEthHelpers.Gwei(fee))).Append(" gwei");
            summary.Append(spec.FeeCollector is null ? "; " : "; base fees collected: ").Append(Human(baseFeesPaid));
            if (spec.FeeCollector is null) summary.Append(" burnt");
        }

        if (receipts?["failed"] is JsonValue failedValue && failedValue.GetValue<int>() > 0)
        {
            summary.Append("; ").Append(failedValue.GetValue<int>()).Append(" failed");
        }

        if (withdrawalCount > 0)
        {
            summary.Append("; ").Append(withdrawalCount).Append(" withdrawals");
        }

        if (feeRecipient is not null)
        {
            summary.Append(". Fee recipient: ").Append(Display(feeRecipient));
        }

        summary.Append('.');
        json["summary"] = summary.ToString();
        json["notes"] = NotesJson(notes);
        return executor.Success(json);
    }

    private JsonObject Withdrawals(int count, UInt256 totalGwei)
    {
        JsonObject json = new() { ["count"] = count, ["totalGwei"] = totalGwei.ToString() };
        if (profile.IsGnosisFamily)
        {
            // Gnosis withdrawals are amounts of mGNO in gwei; the deposit contract's executeSystemWithdrawals credits
            // amount / 32 GNO (an ERC-20) to be claimed, rather than minting native xDAI.
            UInt256 gnoWei = totalGwei * 1_000_000_000UL / 32;
            json["total"] = new JsonObject { ["formatted"] = McpEthHelpers.FormatNative(gnoWei), ["symbol"] = "GNO" };
            json["note"] = "On Gnosis, withdrawals are system calls to the deposit contract that credit claimable GNO (amount in mGNO gwei / 32), not native xDAI.";
        }
        else
        {
            UInt256 wei = totalGwei * 1_000_000_000UL;
            json["total"] = Amount(wei);
        }

        return json;
    }

    private JsonObject? ReceiptStats(IEthRpcModule eth, BlockParameter block, UInt256? baseFee, List<string> notes, CancellationToken token)
    {
        using ResultWrapper<ReceiptForRpc[]?> result = eth.eth_getBlockReceipts(block);
        if (result.Result.ResultType != ResultType.Success || result.Data is not { } receipts)
        {
            notes.Add("The node returned no receipts for this block; failed transactions, fees and token transfers are omitted.");
            return null;
        }

        int failed = 0;
        UInt256 totalFees = UInt256.Zero;
        UInt256 blobFees = UInt256.Zero;
        ulong gasUsed = 0;
        Dictionary<AddressAsKey, int> tokenCounts = [];
        Dictionary<AddressAsKey, string> standards = [];
        int transfers = 0;
        List<McpTokenMovement> scratch = [];
        foreach (ReceiptForRpc receipt in receipts)
        {
            if (receipt.Status == 0) failed++;
            gasUsed += receipt.GasUsed;
            totalFees += (receipt.EffectiveGasPrice ?? UInt256.Zero) * (UInt256)receipt.GasUsed;
            if (receipt.BlobGasUsed is { } blobGas && receipt.BlobGasPrice is { } blobPrice)
            {
                blobFees += blobPrice * (UInt256)blobGas;
            }

            foreach (LogEntryForRpc log in receipt.Logs ?? [])
            {
                scratch.Clear();
                McpTxTokens.Extract(log.ToLogEntry(), scratch, profile.WrappedNativeToken);
                foreach (McpTokenMovement movement in scratch)
                {
                    transfers++;
                    tokenCounts[movement.Token] = tokenCounts.TryGetValue(movement.Token, out int count) ? count + 1 : 1;
                    standards.TryAdd(movement.Token, movement.Standard);
                }
            }
        }

        UInt256 baseFeesPaid = baseFee is { } fee ? fee * (UInt256)gasUsed : UInt256.Zero;
        JsonObject json = new()
        {
            ["failed"] = failed,
            ["totalFees"] = Amount(totalFees),
            ["priorityFees"] = Amount(totalFees > baseFeesPaid ? totalFees - baseFeesPaid : UInt256.Zero),
            ["tokenTransfers"] = transfers
        };

        if (!blobFees.IsZero) json["blobFees"] = Amount(blobFees);

        List<KeyValuePair<AddressAsKey, int>> top = [.. tokenCounts];
        top.Sort(static (a, b) => b.Value.CompareTo(a.Value));
        List<Address> topTokens = [];
        for (int i = 0; i < top.Count && i < BlockTopCount; i++) topTokens.Add(top[i].Key);
        (Dictionary<AddressAsKey, McpTokenInfo> infos, _) = McpTxTokens.LookUp(tokenMetadata, eth, topTokens, BlockTopCount, token);

        JsonArray topJson = [];
        foreach (Address tokenAddress in topTokens)
        {
            infos.TryGetValue(tokenAddress, out McpTokenInfo? info);
            JsonObject entry = new()
            {
                ["token"] = McpEthHelpers.Checksum(tokenAddress),
                ["standard"] = standards[tokenAddress],
                ["transfers"] = tokenCounts[tokenAddress]
            };
            if (info?.Symbol is not null) entry["symbol"] = info.Symbol;
            topJson.Add(entry);
        }

        json["topTokens"] = topJson;
        return json;
    }

    // ---- shared helpers ----

    private JsonObject Amount(in UInt256 wei) => new()
    {
        ["wei"] = McpTxFormat.Hex(wei),
        ["formatted"] = McpEthHelpers.FormatNative(wei),
        ["symbol"] = profile.NativeCurrencySymbol
    };

    private string Human(in UInt256 wei) => $"{McpTxFormat.Human(McpEthHelpers.FormatNative(wei))} {profile.NativeCurrencySymbol}";

    // Anyone can deploy a token called "USDC", so symbols read from other contracts are flagged in sentences.
    private bool IsWellKnownToken(Address token)
    {
        foreach (McpWellKnownContract known in profile.Tokens)
        {
            if (known.Address == token)
            {
                return true;
            }
        }

        return false;
    }

    // Revert strings are attacker-controlled text: quoted and cut short so a summary cannot be taken over by them.
    private static string SummaryRevert(McpDecodedRevert revert)
    {
        string message = revert.Message.Length > MaxSummaryRevertLength ? revert.Message[..MaxSummaryRevertLength] + "..." : revert.Message;
        return revert.Kind == "Error" ? $"\"{message}\"" : message;
    }

    private string? Label(Address address)
    {
        foreach (McpWellKnownContract contract in profile.WellKnownContracts)
        {
            if (contract.Address == address)
            {
                return contract.Name;
            }
        }

        return null;
    }

    private string Display(Address address) => Label(address) is { } label ? $"{label} ({McpTxFormat.Short(address)})" : McpTxFormat.Short(address);

    private static IEnumerable<Address> TokensOf(List<McpTokenMovement> movements)
    {
        foreach (McpTokenMovement movement in movements)
        {
            yield return movement.Token;
        }
    }

    private static string TypeName(TxType type) => type switch
    {
        TxType.Legacy => "legacy",
        TxType.AccessList => "access-list (EIP-2930)",
        TxType.EIP1559 => "EIP-1559",
        TxType.Blob => "blob (EIP-4844)",
        TxType.SetCode => "set-code (EIP-7702)",
        _ => $"type 0x{(int)type:x}"
    };

    private static string? StringOf(JsonElement element) => element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    private static string ErrorMessage(CallToolResult error) =>
        McpToolExecutor.ReadError(error) is { } e && e.TryGetProperty("message", out JsonElement m) ? m.GetString() ?? string.Empty : string.Empty;

    // Puts the fields an LLM reads first (identity, outcome and summary) at the top of the object.
    private static JsonObject Ordered(JsonObject json)
    {
        string[] first = ["hash", "status", "summary"];
        List<KeyValuePair<string, JsonNode?>> properties = [.. json];
        json.Clear();
        JsonObject ordered = [];
        foreach (string key in first)
        {
            foreach (KeyValuePair<string, JsonNode?> property in properties)
            {
                if (property.Key == key) ordered[key] = property.Value;
            }
        }

        foreach (KeyValuePair<string, JsonNode?> property in properties)
        {
            if (Array.IndexOf(first, property.Key) < 0) ordered[property.Key] = property.Value;
        }

        return ordered;
    }

    private static JsonArray NotesJson(List<string> notes)
    {
        JsonArray array = [];
        foreach (string note in notes) array.Add(note);
        return array;
    }

    private sealed class ExplainContext(Hash256 hash, LegacyTransactionForRpc tx)
    {
        public Hash256 Hash { get; } = hash;
        public LegacyTransactionForRpc Tx { get; } = tx;
        public JsonObject Json { get; } = new() { ["hash"] = hash.ToString() };
        public List<string> Notes { get; } = [];
        public int BlobCount { get; set; }

        public Stopwatch Clock { get; } = Stopwatch.StartNew();
    }

    private sealed record TraceFacts(List<McpValueTransfer> Transfers, int TransfersTotal, McpFailingFrame? FailingFrame, string? RootError);

    private sealed record SimulateCallInput(Address? From, Address To, byte[] Data, UInt256 Value, ulong? Gas);
}
