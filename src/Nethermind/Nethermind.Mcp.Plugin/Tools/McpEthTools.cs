// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db.LogIndex;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;

namespace Nethermind.Mcp.Plugin.Tools;

/// <summary>Read-only MCP tools over the node's production <c>eth</c> JSON-RPC module.</summary>
/// <remarks>
/// Every tool validates its arguments, then rents the eth module from <see cref="IRpcModuleProvider"/> for a single
/// call and returns it afterwards. Results are serialized with the JSON-RPC serializer, so values use the same hex
/// conventions as the equivalent <c>eth_*</c> methods. Successful results are <c>{"result": ...}</c>; failures set
/// <see cref="CallToolResult.IsError"/> and carry <c>{"error": {"code", "message", "data"?}}</c> with a code from
/// <see cref="McpToolErrorCodes"/>.
/// </remarks>
/// <param name="executor">Runs tool bodies against rented modules under the MCP limits.</param>
/// <param name="blockFinder">Resolves <c>get_logs</c> block selectors to numbers before the range check.</param>
/// <param name="config">The MCP limits.</param>
/// <param name="rpcConfig">The JSON-RPC configuration; <see cref="IJsonRpcConfig.GasCap"/> also caps <c>call</c> gas.</param>
/// <param name="chainProfile">Network name, native currency and well-known contracts.</param>
/// <param name="logIndexStorage">The log index, whose covered range lets <c>get_logs</c> scan wider pages.</param>
/// <param name="capabilities">Reports the node's history range for not-found transaction lookups.</param>
[McpServerToolType]
internal sealed partial class McpEthTools(
    McpToolExecutor executor,
    IBlockFinder blockFinder,
    IMcpConfig config,
    IJsonRpcConfig rpcConfig,
    McpChainProfile chainProfile,
    ILogIndexStorage logIndexStorage,
    McpNodeCapabilities capabilities) : IMcpToolSet
{
    internal const string BlockSelectorDescription =
        "Block selector: \"latest\", \"earliest\", \"safe\", \"finalized\", a block number as a 0x-prefixed hex string (\"0x12a05f2\") " +
        "or decimal string (\"19553778\"), or a 32-byte block hash (0x followed by 64 hex characters). \"pending\" is not supported.";

    internal const string AddressDescription = "20-byte account address: 0x followed by 40 hex characters (any letter case).";
    private const string TransactionHashDescription = "32-byte transaction hash: 0x followed by 64 hex characters.";

    private readonly McpToolExecutor _executor = executor;
    private readonly ulong _maxCallGas = Math.Min((ulong)Math.Max(1, config.MaxCallGas), rpcConfig.GasCap.EffectiveGasCap());
    private readonly int _maxCallDataSize = Math.Max(0, config.MaxCallDataSize);
    private readonly ulong _maxLogBlockRange = (ulong)Math.Max(1, config.MaxLogBlockRange);
    private readonly ulong _maxIndexedLogBlockRange = (ulong)Math.Max(Math.Max(1, config.MaxLogBlockRange), config.MaxIndexedLogBlockRange);
    private readonly int _maxLogs = Math.Max(1, config.MaxLogs);
    private readonly int _maxResultSize = Math.Max(1, config.MaxResultSize);

    /// <inheritdoc/>
    public IEnumerable<McpServerTool> CreateServerTools() =>
        McpToolFactory.Create(this, DescribeLimits, _maxResultSize);

    // Descriptions are static attributes, so this node's actual limits are appended here.
    private string DescribeLimits(MethodInfo method) => method.Name switch
    {
        nameof(GetLogs) => $" Limits on this node: each page scans at most {_maxLogBlockRange} blocks ({_maxIndexedLogBlockRange} when the log index covers them) " +
            $"and returns at most {_maxLogs} logs.",
        nameof(Call) => $" Limits on this node: gas at most {_maxCallGas}, data at most {_maxCallDataSize} bytes.",
        _ => string.Empty
    };

    /// <summary>Returns the chain, network and head block, plus the native currency and well-known contracts.</summary>
    [McpServerTool(Name = "chain_info", Title = "Chain info", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Start here: identifies the network this node serves and its current head. Returns {chainId (hex), chainIdDecimal, networkName, " +
        "nativeCurrency (\"ETH\", or \"xDAI\" on Gnosis/Chiado), isGnosisFamily, headNumber (hex), headNumberDecimal, headHash, headTimestamp (hex Unix seconds), " +
        "headTimestampIso, wellKnownContracts: [{name, address (EIP-55), kind}]} where kind is system, token, ens, etc. " +
        "Use node_status instead for sync progress, peers and which historical data the node can serve. Takes no arguments.")]
    [McpToolOutputSchema("""
        {"type":"object","required":["result"],"properties":{"result":{"type":"object",
          "required":["chainId","chainIdDecimal","networkName","nativeCurrency","isGnosisFamily","headNumber","headNumberDecimal","headHash","headTimestamp","wellKnownContracts"],
          "properties":{
            "chainId":{"type":"string"},"chainIdDecimal":{"type":"integer"},"networkName":{"type":"string"},
            "nativeCurrency":{"type":"string"},"isGnosisFamily":{"type":"boolean"},
            "headNumber":{"type":"string"},"headNumberDecimal":{"type":"integer"},"headHash":{"type":"string"},
            "headTimestamp":{"type":"string"},"headTimestampIso":{"type":["string","null"]},
            "wellKnownContracts":{"type":"array","items":{"type":"object","required":["name","address","kind"],
              "properties":{"name":{"type":"string"},"address":{"type":"string"},"kind":{"type":"string"}}}}}}}}
        """)]
    public Task<CallToolResult> ChainInfo(CancellationToken cancellationToken) =>
        _executor.ExecuteAsync("chain_info", nameof(IEthRpcModule.eth_getHeaderByNumber), (eth, _) =>
        {
            // eth_chainId returns a cached, shared wrapper, so it must not be disposed.
            ResultWrapper<ulong> chainId = eth.eth_chainId();
            if (chainId.Result.ResultType != ResultType.Success)
            {
                return Task.FromResult(_executor.Failure("chain_info", chainId));
            }

            using ResultWrapper<BlockHeaderForRpc?> head = eth.eth_getHeaderByNumber(BlockParameter.Latest);
            if (head.Result.ResultType != ResultType.Success)
            {
                return Task.FromResult(_executor.Failure("chain_info", head));
            }

            if (head.Data is not { } header)
            {
                return Task.FromResult(McpToolExecutor.Error(McpToolErrorCodes.Unavailable, "The node has no head block yet; it is probably still starting."));
            }

            return Task.FromResult(_executor.Success((ChainId: chainId.Data, Header: header, Profile: chainProfile), static (writer, state) =>
            {
                writer.WriteStartObject();
                writer.WritePropertyName("chainId"u8);
                McpToolExecutor.WriteValue(writer, state.ChainId);
                writer.WriteNumber("chainIdDecimal"u8, state.ChainId);
                writer.WriteString("networkName"u8, state.Profile.NetworkName);
                writer.WriteString("nativeCurrency"u8, state.Profile.NativeCurrencySymbol);
                writer.WriteBoolean("isGnosisFamily"u8, state.Profile.IsGnosisFamily);
                writer.WritePropertyName("headNumber"u8);
                McpToolExecutor.WriteValue(writer, state.Header.Number);
                if (state.Header.Number is { } number)
                {
                    writer.WriteNumber("headNumberDecimal"u8, number);
                }

                writer.WritePropertyName("headHash"u8);
                McpToolExecutor.WriteValue(writer, state.Header.Hash);
                writer.WritePropertyName("headTimestamp"u8);
                McpToolExecutor.WriteValue(writer, state.Header.Timestamp);
                writer.WriteString("headTimestampIso"u8, state.Header.Timestamp > ulong.MaxValue ? null : McpEthHelpers.ToIso((ulong)state.Header.Timestamp));
                writer.WritePropertyName("wellKnownContracts"u8);
                writer.WriteStartArray();
                foreach (McpWellKnownContract contract in state.Profile.WellKnownContracts)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name"u8, contract.Name);
                    writer.WriteString("address"u8, McpEthHelpers.Checksum(contract.Address));
                    writer.WriteString("kind"u8, contract.Kind);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                return null;
            }));
        }, cancellationToken);

    /// <summary>Returns a block by number, tag or hash.</summary>
    [McpServerTool(Name = "get_block", Title = "Get block", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Returns a raw block exactly as eth_getBlockByNumber/eth_getBlockByHash: header fields (number, hash, parentHash, timestamp, gasUsed, gasLimit, " +
        "baseFeePerGas, miner...), uncles, withdrawals and either transaction hashes (default) or full transaction objects. Quantities are 0x-prefixed hex; gas in gas units, amounts in wei. " +
        "For a readable overview of a block (top transfers, gas, fees) prefer block_summary; for all receipts of a block use get_block_receipts. " +
        "Fails with not_found if the block is unknown; if the result is too large, retry with fullTransactions=false.")]
    [McpToolOutputSchema("""
        {"type":"object","required":["result"],"properties":{"result":{"type":"object",
          "required":["number","hash","parentHash","timestamp","gasUsed","gasLimit","transactions","stateRoot","receiptsRoot","transactionsRoot","logsBloom","miner"],
          "properties":{
            "number":{"type":["string","null"]},"hash":{"type":["string","null"]},"parentHash":{"type":"string"},"timestamp":{"type":"string"},
            "gasUsed":{"type":"string"},"gasLimit":{"type":"string"},"baseFeePerGas":{"type":"string"},"miner":{"type":["string","null"]},
            "stateRoot":{"type":"string"},"receiptsRoot":{"type":"string"},"transactionsRoot":{"type":"string"},"logsBloom":{"type":"string"},
            "extraData":{"type":"string"},"size":{"type":"string"},"difficulty":{"type":"string"},"totalDifficulty":{"type":"string"},
            "mixHash":{"type":"string"},"nonce":{"type":["string","null"]},"sha3Uncles":{"type":"string"},
            "transactions":{"type":"array","items":{"type":["string","object"]}},
            "uncles":{"type":"array","items":{"type":"string"}},
            "withdrawals":{"type":"array","items":{"type":"object"}},"withdrawalsRoot":{"type":"string"},
            "blobGasUsed":{"type":"string"},"excessBlobGas":{"type":"string"},"parentBeaconBlockRoot":{"type":"string"},"requestsHash":{"type":"string"}}}}}
        """)]
    public Task<CallToolResult> GetBlock(
        [Description(BlockSelectorDescription)] string block,
        [Description("If true, include full transaction objects instead of only their hashes. Default false. Full blocks on mainnet can be hundreds of KB.")] bool fullTransactions = false,
        CancellationToken cancellationToken = default)
    {
        if (!McpToolInput.TryParseBlock(block, nameof(block), out BlockParameter? blockParameter, out string? error))
        {
            return McpEthHelpers.InvalidInput(error);
        }

        bool byHash = blockParameter.Type == BlockParameterType.BlockHash;
        return _executor.ExecuteAsync(
            "get_block",
            byHash ? nameof(IEthRpcModule.eth_getBlockByHash) : nameof(IEthRpcModule.eth_getBlockByNumber),
            (eth, _) =>
            {
                using ResultWrapper<BlockForRpc> result = byHash
                    ? eth.eth_getBlockByHash(blockParameter.BlockHash!, fullTransactions)
                    : eth.eth_getBlockByNumber(blockParameter, fullTransactions);
                return Task.FromResult(result.Result.ResultType == ResultType.Success && result.Data is null
                    ? McpEthHelpers.MissingBlock(eth, blockParameter, capabilities, "Block not found; check the number is not above the head (see chain_info).")
                    : SuccessOrNotFound("get_block", result, string.Empty));
            },
            cancellationToken);
    }

    /// <summary>Returns a transaction by hash.</summary>
    [McpServerTool(Name = "get_transaction", Title = "Get transaction", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Returns a raw transaction by hash exactly as eth_getTransactionByHash (from, to, value, input, nonce, gas, fee fields, type, block position). " +
        "Transactions still in the node's pool have null blockHash/blockNumber. Quantities are 0x-prefixed hex; value and gas prices in wei. " +
        "To understand what a transaction did (transfers, decoded events, failure reason) prefer explain_transaction; for the outcome (status, gas used, logs) use get_transaction_receipt. " +
        "Fails with not_found if unknown.")]
    [McpToolOutputSchema("""
        {"type":"object","required":["result"],"properties":{"result":{"type":"object",
          "required":["hash","type","from","gas","input","nonce","value","blockHash","blockNumber","transactionIndex"],
          "properties":{
            "hash":{"type":["string","null"]},"type":{"type":["string","null"]},"from":{"type":"string"},"to":{"type":["string","null"]},
            "value":{"type":"string"},"input":{"type":"string"},"nonce":{"type":"string"},"gas":{"type":["string","null"]},
            "gasPrice":{"type":"string"},"maxFeePerGas":{"type":"string"},"maxPriorityFeePerGas":{"type":"string"},"chainId":{"type":"string"},
            "blockHash":{"type":["string","null"]},"blockNumber":{"type":["string","null"]},"transactionIndex":{"type":["string","null"]},
            "accessList":{"type":"array"},"v":{"type":"string"},"r":{"type":"string"},"s":{"type":"string"},"yParity":{"type":"string"}}}}}
        """)]
    public Task<CallToolResult> GetTransaction(
        [Description(TransactionHashDescription)] string hash,
        CancellationToken cancellationToken = default)
    {
        if (!McpToolInput.TryParseHash(hash, nameof(hash), out Hash256? txHash, out string? error))
        {
            return McpEthHelpers.InvalidInput(error);
        }

        return _executor.ExecuteAsync("get_transaction", nameof(IEthRpcModule.eth_getTransactionByHash), (eth, _) =>
        {
            using ResultWrapper<TransactionForRpc?> result = eth.eth_getTransactionByHash(txHash);
            return Task.FromResult(SuccessOrNotFound("get_transaction", result, "Transaction not found; it may never have been broadcast or been dropped from the pool.", historyHint: true));
        }, cancellationToken);
    }

    /// <summary>Returns a transaction receipt by transaction hash.</summary>
    [McpServerTool(Name = "get_transaction_receipt", Title = "Get transaction receipt", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Returns the raw receipt of a mined transaction exactly as eth_getTransactionReceipt: status (0x1 success, 0x0 failure), gasUsed, " +
        "effectiveGasPrice, contractAddress (for deployments) and raw logs. Quantities are 0x-prefixed hex. " +
        "For decoded events and a plain-English outcome prefer explain_transaction (or decode_logs for just the logs). " +
        "Fails with not_found if the transaction is unknown or not yet mined.")]
    [McpToolOutputSchema("""
        {"type":"object","required":["result"],"properties":{"result":{"type":"object",
          "required":["transactionHash","transactionIndex","blockNumber","cumulativeGasUsed","gasUsed","from","to","contractAddress","logs","type"],
          "properties":{
            "transactionHash":{"type":"string"},"transactionIndex":{"type":"string"},"blockHash":{"type":"string"},"blockNumber":{"type":"string"},
            "cumulativeGasUsed":{"type":"string"},"gasUsed":{"type":"string"},"effectiveGasPrice":{"type":"string"},
            "blobGasUsed":{"type":"string"},"blobGasPrice":{"type":"string"},
            "from":{"type":"string"},"to":{"type":["string","null"]},"contractAddress":{"type":["string","null"]},
            "logs":{"type":"array","items":{"type":"object","required":["address","topics","data"],
              "properties":{"address":{"type":"string"},"topics":{"type":"array","items":{"type":"string"}},"data":{"type":"string"},"logIndex":{"type":"string"}}}},
            "logsBloom":{"type":"string"},"root":{"type":"string"},"status":{"type":"string"},"type":{"type":"string"}}}}}
        """)]
    public Task<CallToolResult> GetTransactionReceipt(
        [Description(TransactionHashDescription)] string hash,
        CancellationToken cancellationToken = default)
    {
        if (!McpToolInput.TryParseHash(hash, nameof(hash), out Hash256? txHash, out string? error))
        {
            return McpEthHelpers.InvalidInput(error);
        }

        return _executor.ExecuteAsync("get_transaction_receipt", nameof(IEthRpcModule.eth_getTransactionReceipt), (eth, _) =>
        {
            using ResultWrapper<ReceiptForRpc?> result = eth.eth_getTransactionReceipt(txHash);
            return Task.FromResult(SuccessOrNotFound("get_transaction_receipt", result, "Receipt not found; the transaction is unknown or still pending.", historyHint: true));
        }, cancellationToken);
    }

    /// <summary>Returns an account's native balance.</summary>
    [McpServerTool(Name = "get_balance", Title = "Get balance", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Returns the native-currency balance of an account (ETH, or xDAI on Gnosis/Chiado): {balance (hex wei), balanceFormatted (decimal string in whole units, " +
        "for example \"1.5\"), symbol, decimals (18)}. For ERC-20 token balances use token_balances (several tokens) or call_function. " +
        "Fails with unavailable if the node has no state for the requested block (not synced or pruned).")]
    [McpToolOutputSchema("""
        {"type":"object","required":["result"],"properties":{"result":{"type":"object","required":["balance","balanceFormatted","symbol","decimals"],
          "properties":{"balance":{"type":"string"},"balanceFormatted":{"type":"string"},"symbol":{"type":"string"},"decimals":{"type":"integer"}}}}}
        """)]
    public Task<CallToolResult> GetBalance(
        [Description(AddressDescription)] string address,
        [Description(BlockSelectorDescription + " Default \"latest\".")] string block = "latest",
        CancellationToken cancellationToken = default)
    {
        if (!McpToolInput.TryParseAddress(address, nameof(address), out Address? account, out string? error)
            || !McpToolInput.TryParseBlock(block, nameof(block), out BlockParameter? blockParameter, out error))
        {
            return McpEthHelpers.InvalidInput(error);
        }

        return _executor.ExecuteAsync("get_balance", nameof(IEthRpcModule.eth_getBalance), async (eth, _) =>
        {
            using ResultWrapper<UInt256?> result = await eth.eth_getBalance(account, blockParameter);
            if (result.Result.ResultType != ResultType.Success)
            {
                return _executor.Failure("get_balance", result);
            }

            if (result.Data is not { } balance)
            {
                return McpToolExecutor.Error(McpToolErrorCodes.NotFound, "Balance not found.");
            }

            return _executor.Success((Balance: balance, Symbol: chainProfile.NativeCurrencySymbol), static (writer, state) =>
            {
                writer.WriteStartObject();
                writer.WritePropertyName("balance"u8);
                McpToolExecutor.WriteValue(writer, state.Balance);
                writer.WriteString("balanceFormatted"u8, McpEthHelpers.FormatNative(state.Balance));
                writer.WriteString("symbol"u8, state.Symbol);
                writer.WriteNumber("decimals"u8, McpEthHelpers.NativeDecimals);
                writer.WriteEndObject();
                return null;
            });
        }, cancellationToken);
    }

    /// <summary>Returns the bytecode deployed at an address.</summary>
    [McpServerTool(Name = "get_code", Title = "Get code", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Returns the EVM bytecode deployed at an address as 0x-prefixed hex; \"0x\" means the account has no code (an externally owned account or empty account). " +
        "Use it to check whether an address is a contract; for what the contract is (token metadata, well-known names) prefer lookup_address or token_info. " +
        "Fails with unavailable if the node has no state for the requested block.")]
    [McpToolOutputSchema("""{"type":"object","required":["result"],"properties":{"result":{"type":"string"}}}""")]
    public Task<CallToolResult> GetCode(
        [Description(AddressDescription)] string address,
        [Description(BlockSelectorDescription + " Default \"latest\".")] string block = "latest",
        CancellationToken cancellationToken = default)
    {
        if (!McpToolInput.TryParseAddress(address, nameof(address), out Address? account, out string? error)
            || !McpToolInput.TryParseBlock(block, nameof(block), out BlockParameter? blockParameter, out error))
        {
            return McpEthHelpers.InvalidInput(error);
        }

        return _executor.ExecuteAsync("get_code", nameof(IEthRpcModule.eth_getCode), (eth, _) =>
        {
            using ResultWrapper<byte[]> result = eth.eth_getCode(account, blockParameter);
            return Task.FromResult(SuccessOrNotFound("get_code", result, "Code not found."));
        }, cancellationToken);
    }

    /// <summary>Executes a read-only message call.</summary>
    [McpServerTool(Name = "call", Title = "Call contract", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Low-level eth_call: executes a message call with raw ABI-encoded calldata against the state of a block, without creating a transaction, " +
        "and returns the raw return data as 0x-prefixed hex. Nothing is signed, broadcast or persisted; gas price is zero. An explicit gas limit is required " +
        "and capped by the node configuration. If you know the function signature (for example \"balanceOf(address)\"), prefer call_function, which encodes " +
        "arguments and decodes results; to preview a whole transaction with state changes use simulate_transaction. " +
        "A revert fails with execution_reverted: the revert data is hex in error.data and the decoded reason is in error.reason.")]
    [McpToolOutputSchema("""{"type":"object","required":["result"],"properties":{"result":{"type":"string"}}}""")]
    public Task<CallToolResult> Call(
        [Description("Target contract address: 0x followed by 40 hex characters.")] string to,
        [Description("ABI-encoded call data (4-byte selector followed by arguments) as 0x-prefixed hex with an even number of digits; \"0x\" for none.")] string data,
        [Description("Gas limit in gas units, as a 0x-hex or decimal string; at least 1 and at most the node's configured cap.")] string gas,
        [Description("Optional sender address (0x followed by 40 hex characters); defaults to the zero address.")] string? from = null,
        [Description("Optional value to send in wei, as a 0x-hex or decimal string. Default 0.")] string? value = null,
        [Description(BlockSelectorDescription + " Default \"latest\".")] string block = "latest",
        CancellationToken cancellationToken = default)
    {
        Address? sender = null;
        UInt256 callValue = UInt256.Zero;
        if (!McpToolInput.TryParseAddress(to, nameof(to), out Address? target, out string? error)
            || !McpToolInput.TryParseData(data, nameof(data), _maxCallDataSize, out byte[]? input, out error)
            || !McpToolInput.TryParseULong(gas, nameof(gas), out ulong gasLimit, out error)
            || (from is not null && !McpToolInput.TryParseAddress(from, nameof(from), out sender, out error))
            || (value is not null && !McpToolInput.TryParseUInt256(value, nameof(value), out callValue, out error))
            || !McpToolInput.TryParseBlock(block, nameof(block), out BlockParameter? blockParameter, out error))
        {
            return McpEthHelpers.InvalidInput(error);
        }

        if (gasLimit == 0 || gasLimit > _maxCallGas)
        {
            return McpEthHelpers.InvalidInput($"'gas' must be between 1 and {_maxCallGas}.");
        }

        // A legacy call with no gas price is valid on every fork and executes with a zero base fee, like eth_call without fee fields.
        LegacyTransactionForRpc transaction = new()
        {
            To = target,
            From = sender,
            Value = callValue,
            Input = input,
            Gas = gasLimit
        };

        return _executor.ExecuteAsync("call", nameof(IEthRpcModule.eth_call), (eth, _) =>
        {
            using ResultWrapper<HexBytes> result = eth.eth_call(transaction, blockParameter);
            return Task.FromResult(result.Result.ResultType == ResultType.Success
                ? _executor.Success(result.Data)
                : McpEthHelpers.WithRevertReason(_executor.Failure("call", result), result));
        }, cancellationToken);
    }

    /// <summary>Maps a JSON-RPC result to a success, its mapped failure, or <c>not_found</c> when it holds no data.</summary>
    /// <param name="toolName">The MCP tool name, used in log messages.</param>
    /// <param name="result">The JSON-RPC result.</param>
    /// <param name="notFoundMessage">The message of the <c>not_found</c> error.</param>
    /// <param name="historyHint">Whether a not-found result should mention the node's history floor (transaction hash lookups).</param>
    private CallToolResult SuccessOrNotFound<T>(string toolName, ResultWrapper<T> result, string notFoundMessage, bool historyHint = false) =>
        result.Result.ResultType != ResultType.Success
            ? _executor.Failure(toolName, result)
            : result.Data is null
                ? McpToolExecutor.Error(McpToolErrorCodes.NotFound, historyHint ? notFoundMessage + capabilities.DescribeTransactionHistoryLimit() : notFoundMessage)
                : _executor.Success(result.Data);
}
