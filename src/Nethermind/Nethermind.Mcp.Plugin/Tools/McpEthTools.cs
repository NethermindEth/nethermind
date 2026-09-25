// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Filters;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;

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
[McpServerToolType]
internal sealed class McpEthTools(McpToolExecutor executor, IBlockFinder blockFinder, IMcpConfig config, IJsonRpcConfig rpcConfig) : IMcpToolSet
{
    private const string BlockSelectorDescription =
        "Block selector: \"latest\", \"earliest\", \"safe\", \"finalized\", a block number as a 0x-prefixed hex string (\"0x12a05f2\") " +
        "or decimal string (\"19553778\"), or a 32-byte block hash (0x followed by 64 hex characters). \"pending\" is not supported.";

    private const string AddressDescription = "20-byte account address: 0x followed by 40 hex characters.";
    private const string TransactionHashDescription = "32-byte transaction hash: 0x followed by 64 hex characters.";
    private const int LogCancellationCheckInterval = 64;

    private readonly McpToolExecutor _executor = executor;
    private readonly ulong _maxCallGas = Math.Min((ulong)Math.Max(1, config.MaxCallGas), rpcConfig.GasCap.EffectiveGasCap());
    private readonly int _maxCallDataSize = Math.Max(0, config.MaxCallDataSize);
    private readonly ulong _maxLogBlockRange = (ulong)Math.Max(1, config.MaxLogBlockRange);
    private readonly int _maxLogs = Math.Max(1, config.MaxLogs);
    private readonly int _maxResultSize = Math.Max(1, config.MaxResultSize);

    /// <inheritdoc/>
    public IEnumerable<McpServerTool> CreateServerTools() => McpToolFactory.Create(this, DescribeLimits, _maxResultSize);

    // Descriptions are static attributes, so this node's actual limits are appended here.
    private string DescribeLimits(MethodInfo method) => method.Name switch
    {
        nameof(GetLogs) => $" Limits on this node: at most {_maxLogBlockRange} blocks per range and {_maxLogs} logs per result.",
        nameof(Call) => $" Limits on this node: gas at most {_maxCallGas}, data at most {_maxCallDataSize} bytes.",
        _ => string.Empty
    };

    /// <summary>Returns the chain ID and the current head block number and hash.</summary>
    [McpServerTool(Name = "chain_info", Title = "Chain info", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Returns the chain ID and the node's current head block: {\"chainId\": hex, \"headNumber\": hex, \"headHash\": 32-byte hash}. " +
        "Numbers are 0x-prefixed hex quantities. Takes no arguments.")]
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
                return Task.FromResult(McpToolExecutor.Error(McpToolErrorCodes.Unavailable, "The node has no head block yet."));
            }

            return Task.FromResult(_executor.Success((ChainId: chainId.Data, Header: header), static (writer, state) =>
            {
                writer.WriteStartObject();
                writer.WritePropertyName("chainId"u8);
                McpToolExecutor.WriteValue(writer, state.ChainId);
                writer.WritePropertyName("headNumber"u8);
                McpToolExecutor.WriteValue(writer, state.Header.Number);
                writer.WritePropertyName("headHash"u8);
                McpToolExecutor.WriteValue(writer, state.Header.Hash);
                writer.WriteEndObject();
                return null;
            }));
        }, cancellationToken);

    /// <summary>Returns a block by number, tag or hash.</summary>
    [McpServerTool(Name = "get_block", Title = "Get block", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Returns a block in the same format as eth_getBlockByNumber/eth_getBlockByHash: header fields, uncles, withdrawals and " +
        "either transaction hashes (default) or full transaction objects. Quantities are 0x-prefixed hex; gas and value amounts are in gas units and wei. " +
        "Fails with not_found if the block is unknown; if the result is too large, retry with fullTransactions=false.")]
    public Task<CallToolResult> GetBlock(
        [Description(BlockSelectorDescription)] string block,
        [Description("If true, include full transaction objects instead of only their hashes. Default false.")] bool fullTransactions = false,
        CancellationToken cancellationToken = default)
    {
        if (!McpToolInput.TryParseBlock(block, nameof(block), out BlockParameter? blockParameter, out string? error))
        {
            return InvalidInput(error);
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
                return Task.FromResult(SuccessOrNotFound("get_block", result, "Block not found."));
            },
            cancellationToken);
    }

    /// <summary>Returns a transaction by hash.</summary>
    [McpServerTool(Name = "get_transaction", Title = "Get transaction", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Returns a transaction by hash in the same format as eth_getTransactionByHash. Transactions still in the node's pool are " +
        "returned with null blockHash/blockNumber. Quantities are 0x-prefixed hex; value and gas prices are in wei. Fails with not_found if unknown.")]
    public Task<CallToolResult> GetTransaction(
        [Description(TransactionHashDescription)] string hash,
        CancellationToken cancellationToken = default)
    {
        if (!McpToolInput.TryParseHash(hash, nameof(hash), out Hash256? txHash, out string? error))
        {
            return InvalidInput(error);
        }

        return _executor.ExecuteAsync("get_transaction", nameof(IEthRpcModule.eth_getTransactionByHash), (eth, _) =>
        {
            using ResultWrapper<TransactionForRpc?> result = eth.eth_getTransactionByHash(txHash);
            return Task.FromResult(SuccessOrNotFound("get_transaction", result, "Transaction not found."));
        }, cancellationToken);
    }

    /// <summary>Returns a transaction receipt by transaction hash.</summary>
    [McpServerTool(Name = "get_transaction_receipt", Title = "Get transaction receipt", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Returns the receipt of a mined transaction in the same format as eth_getTransactionReceipt: status, gasUsed, " +
        "effectiveGasPrice, contractAddress and logs. Quantities are 0x-prefixed hex. Fails with not_found if the transaction is unknown or not yet mined.")]
    public Task<CallToolResult> GetTransactionReceipt(
        [Description(TransactionHashDescription)] string hash,
        CancellationToken cancellationToken = default)
    {
        if (!McpToolInput.TryParseHash(hash, nameof(hash), out Hash256? txHash, out string? error))
        {
            return InvalidInput(error);
        }

        return _executor.ExecuteAsync("get_transaction_receipt", nameof(IEthRpcModule.eth_getTransactionReceipt), (eth, _) =>
        {
            using ResultWrapper<ReceiptForRpc?> result = eth.eth_getTransactionReceipt(txHash);
            return Task.FromResult(SuccessOrNotFound("get_transaction_receipt", result, "Receipt not found."));
        }, cancellationToken);
    }

    /// <summary>Returns an account balance in wei.</summary>
    [McpServerTool(Name = "get_balance", Title = "Get balance", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Returns the balance of an account in wei as a 0x-prefixed hex quantity (1 ether = 10^18 wei). " +
        "Fails with unavailable if the node has no state for the requested block (not synced or pruned).")]
    public Task<CallToolResult> GetBalance(
        [Description(AddressDescription)] string address,
        [Description(BlockSelectorDescription + " Default \"latest\".")] string block = "latest",
        CancellationToken cancellationToken = default)
    {
        if (!McpToolInput.TryParseAddress(address, nameof(address), out Address? account, out string? error)
            || !McpToolInput.TryParseBlock(block, nameof(block), out BlockParameter? blockParameter, out error))
        {
            return InvalidInput(error);
        }

        return _executor.ExecuteAsync("get_balance", nameof(IEthRpcModule.eth_getBalance), async (eth, _) =>
        {
            using ResultWrapper<UInt256?> result = await eth.eth_getBalance(account, blockParameter);
            return SuccessOrNotFound("get_balance", result, "Balance not found.");
        }, cancellationToken);
    }

    /// <summary>Returns the bytecode deployed at an address.</summary>
    [McpServerTool(Name = "get_code", Title = "Get code", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Returns the EVM bytecode deployed at an address as 0x-prefixed hex; \"0x\" means the account has no code (an EOA or empty account). " +
        "Fails with unavailable if the node has no state for the requested block.")]
    public Task<CallToolResult> GetCode(
        [Description(AddressDescription)] string address,
        [Description(BlockSelectorDescription + " Default \"latest\".")] string block = "latest",
        CancellationToken cancellationToken = default)
    {
        if (!McpToolInput.TryParseAddress(address, nameof(address), out Address? account, out string? error)
            || !McpToolInput.TryParseBlock(block, nameof(block), out BlockParameter? blockParameter, out error))
        {
            return InvalidInput(error);
        }

        return _executor.ExecuteAsync("get_code", nameof(IEthRpcModule.eth_getCode), (eth, _) =>
        {
            using ResultWrapper<byte[]> result = eth.eth_getCode(account, blockParameter);
            return Task.FromResult(SuccessOrNotFound("get_code", result, "Code not found."));
        }, cancellationToken);
    }

    /// <summary>Returns logs matching a bounded filter.</summary>
    [McpServerTool(Name = "get_logs", Title = "Get logs", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Returns event logs in the inclusive block range [fromBlock, toBlock], in the same format as eth_getLogs. " +
        "The range may span at most the node's configured maximum number of blocks and the query may match at most " +
        "the configured maximum number of logs; otherwise it fails with resource_exhausted and should be narrowed. " +
        "Up to 32 addresses and 4 topic positions are accepted; each topic position is null (any), a 32-byte hash, or an array of up to 32 hashes (any of them).")]
    public Task<CallToolResult> GetLogs(
        [Description("First block of the range, inclusive. " + BlockSelectorDescription)] string fromBlock,
        [Description("Last block of the range, inclusive. " + BlockSelectorDescription)] string toBlock,
        [Description("Optional contract addresses (0x followed by 40 hex characters each) that emitted the logs; empty or omitted matches any address. At most 32.")] string[]? address = null,
        [Description("Optional topic filter by position (topic0 is usually the event signature hash). Each position is null (any topic), " +
            "a 32-byte hash (0x followed by 64 hex characters), or an array of up to 32 such hashes (any of them). At most 4 positions.")] JsonElement[]? topics = null,
        CancellationToken cancellationToken = default)
    {
        if (!McpToolInput.TryParseBlock(fromBlock, nameof(fromBlock), out BlockParameter? from, out string? error)
            || !McpToolInput.TryParseBlock(toBlock, nameof(toBlock), out BlockParameter? to, out error)
            || !McpToolInput.TryParseAddresses(address, nameof(address), out HashSet<AddressAsKey>? addresses, out error)
            || !McpToolInput.TryParseTopics(topics, nameof(topics), out Hash256[]?[]? topicFilter, out error))
        {
            return InvalidInput(error);
        }

        return _executor.ExecuteAsync("get_logs", nameof(IEthRpcModule.eth_getLogs), (eth, token) =>
        {
            // Resolve tags once so the range check and the query see the same blocks even while the head moves.
            if (!TryResolveBlockNumber(from, nameof(fromBlock), out ulong fromNumber, out CallToolResult? failure)
                || !TryResolveBlockNumber(to, nameof(toBlock), out ulong toNumber, out failure))
            {
                return Task.FromResult(failure);
            }

            if (fromNumber > toNumber)
            {
                return Task.FromResult(McpToolExecutor.Error(McpToolErrorCodes.InvalidInput, $"fromBlock ({fromNumber}) is greater than toBlock ({toNumber})."));
            }

            if (toNumber - fromNumber >= _maxLogBlockRange)
            {
                return Task.FromResult(McpToolExecutor.Error(McpToolErrorCodes.ResourceExhausted,
                    $"The range spans {toNumber - fromNumber + 1} blocks; the maximum is {_maxLogBlockRange}. Narrow fromBlock/toBlock."));
            }

            Filter filter = new()
            {
                FromBlock = new BlockParameter(fromNumber),
                ToBlock = new BlockParameter(toNumber),
                Address = addresses,
                Topics = topicFilter
            };

            using ResultWrapper<IEnumerable<FilterLog>> result = eth.eth_getLogs(filter);
            if (result.Result.ResultType != ResultType.Success)
            {
                return Task.FromResult(_executor.Failure("get_logs", result));
            }

            // The logs may be produced lazily, so they are enumerated here, while the module is still rented.
            return Task.FromResult(_executor.Success((Logs: result.Data, Tools: this, Token: token), static (writer, state) =>
                state.Tools.WriteLogs(writer, state.Logs, state.Token)));
        }, cancellationToken);
    }

    /// <summary>Executes a read-only message call.</summary>
    [McpServerTool(Name = "call", Title = "Call contract", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Executes a message call against the state of a block without creating a transaction (like eth_call) and returns the " +
        "return data as 0x-prefixed hex. Nothing is signed, broadcast or persisted; gas price is zero. An explicit gas limit is required " +
        "and capped by the node configuration. A revert fails with execution_reverted, with the revert data as hex in error.data.")]
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
            return InvalidInput(error);
        }

        if (gasLimit == 0 || gasLimit > _maxCallGas)
        {
            return InvalidInput($"'gas' must be between 1 and {_maxCallGas}.");
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
                : _executor.Failure("call", result));
        }, cancellationToken);
    }

    private CallToolResult? WriteLogs(Utf8JsonWriter writer, IEnumerable<FilterLog> logs, CancellationToken cancellationToken)
    {
        int count = 0;
        writer.WriteStartArray();
        foreach (FilterLog log in logs)
        {
            if (++count > _maxLogs)
            {
                return McpToolExecutor.Error(McpToolErrorCodes.ResourceExhausted,
                    $"The query matches more than {_maxLogs} logs; narrow the block range or add address/topic filters.");
            }

            if (count % LogCancellationCheckInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            McpToolExecutor.WriteValue(writer, log);
        }

        // In stream mode the eth module silently stops at JsonRpc.MaxLogsPerResponse instead of failing.
        if (rpcConfig.EnableLogsStreamMode && rpcConfig.MaxLogsPerResponse > 0 && count >= rpcConfig.MaxLogsPerResponse)
        {
            return McpToolExecutor.Error(McpToolErrorCodes.ResourceExhausted,
                $"The query reached the node's limit of {rpcConfig.MaxLogsPerResponse} logs per response; narrow the block range or add address/topic filters.");
        }

        writer.WriteEndArray();
        return null;
    }

    private bool TryResolveBlockNumber(BlockParameter block, string parameter, out ulong number, [NotNullWhen(false)] out CallToolResult? failure)
    {
        failure = null;
        if (block.BlockNumber is { } explicitNumber)
        {
            number = explicitNumber;
            return true;
        }

        number = 0;
        if (blockFinder.Head is null)
        {
            failure = McpToolExecutor.Error(McpToolErrorCodes.Unavailable, "The node has no head block yet.");
            return false;
        }

        BlockParameter lookup = block.BlockHash is { } hash ? new BlockParameter(hash, requireCanonical: true) : block;
        SearchResult<BlockHeader> header = blockFinder.SearchForHeader(lookup);
        if (!header.IsError)
        {
            number = header.Object!.Number;
            return true;
        }

        failure = block.Type is BlockParameterType.Safe or BlockParameterType.Finalized
            ? McpToolExecutor.Error(McpToolErrorCodes.Unavailable, $"'{parameter}': the {block} block is not known to the node yet.")
            : header.ErrorCode == ErrorCodes.InvalidInput && header.Error != BlockFinderExtensions.HeaderNotFound
                ? McpToolExecutor.Error(McpToolErrorCodes.InvalidInput, $"'{parameter}': {header.Error}")
                : McpToolExecutor.Error(McpToolErrorCodes.NotFound, $"'{parameter}': block not found.");
        return false;
    }

    private CallToolResult SuccessOrNotFound<T>(string toolName, ResultWrapper<T> result, string notFoundMessage) =>
        result.Result.ResultType != ResultType.Success
            ? _executor.Failure(toolName, result)
            : result.Data is null
                ? McpToolExecutor.Error(McpToolErrorCodes.NotFound, notFoundMessage)
                : _executor.Success(result.Data);

    private static Task<CallToolResult> InvalidInput(string message) =>
        Task.FromResult(McpToolExecutor.Error(McpToolErrorCodes.InvalidInput, message));
}
