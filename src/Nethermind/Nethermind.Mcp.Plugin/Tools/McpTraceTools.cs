// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Mcp.Plugin.Tools.Abi;

namespace Nethermind.Mcp.Plugin.Tools;

/// <summary>The <c>trace_transaction</c> tool: a bounded call tree of a mined transaction.</summary>
/// <remarks>
/// Uses the debug module's native <c>callTracer</c> rather than the parity-style <c>trace_transaction</c>: it returns a
/// nested tree with revert reasons and errors per frame in one non-streaming call, and the debug module has a larger
/// instance pool than the trace module (two instances by default). Rental works whether or not <c>debug</c> is listed
/// in <c>JsonRpc.EnabledModules</c>, since that setting only gates the JSON-RPC endpoint.
/// </remarks>
/// <param name="executor">Runs tool bodies against rented modules under the MCP limits.</param>
/// <param name="config">The MCP limits; <see cref="IMcpConfig.MaxTraceCalls"/> caps the frames returned.</param>
/// <param name="profile">The chain profile, for the native currency symbol.</param>
/// <param name="capabilities">Reports whether the state needed to replay a block is still available.</param>
[McpServerToolType]
internal sealed class McpTraceTools(McpToolExecutor executor, IMcpConfig config, McpChainProfile profile, McpNodeCapabilities capabilities) : IMcpToolSet
{
    /// <summary>The deepest call level returned; deeper frames are counted in <c>omittedCalls</c> so the JSON stays within parser nesting limits.</summary>
    public const int MaxTreeDepth = 24;

    /// <summary>The maximum number of input bytes returned per frame when <c>includeInput</c> is set.</summary>
    public const int MaxInputBytes = 1024;

    /// <summary>The default number of frames returned, sized for LLM clients' tool-result limits.</summary>
    public const int DefaultMaxFrames = 300;

    private const string TraceOutputSchema = """
        {"type":"object","properties":{"result":{"type":"object","properties":{
          "transactionHash":{"type":"string"},
          "blockNumber":{"type":"integer"},
          "status":{"type":"string","enum":["success","failed"]},
          "gasUsed":{"type":"integer"},
          "totalFrames":{"type":"integer"},
          "returnedFrames":{"type":"integer"},
          "truncated":{"type":"boolean"},
          "maxDepth":{"type":"integer"},
          "maxFrames":{"type":"integer"},
          "note":{"type":"string"},
          "root":{"type":["object","null"],"properties":{
            "type":{"type":"string"},"from":{"type":"string"},"to":{"type":"string"},
            "value":{"type":"string"},"valueFormatted":{"type":"string"},
            "gas":{"type":"integer"},"gasUsed":{"type":"integer"},
            "selector":{"type":"string"},"method":{"type":"string"},"inputSize":{"type":"integer"},"input":{"type":"string"},
            "output":{"type":"string"},"outputSize":{"type":"integer"},
            "error":{"type":"string"},"revert":{"type":"object"},
            "omittedCalls":{"type":"integer"},"calls":{"type":"array","items":{"type":"object"}}},
            "required":["type","gas","gasUsed"]}},
          "required":["transactionHash","status","totalFrames","returnedFrames","truncated","root"]}},
        "required":["result"]}
        """;

    private readonly int _maxFrames = Math.Max(1, config.MaxTraceCalls);
    private readonly bool _tracingEnabled = config.EnableTracing;
    private readonly TimeSpan _timeout = TimeSpan.FromMilliseconds(Math.Max(1, config.ToolTimeout));
    private readonly int _maxResultSize = Math.Max(1, config.MaxResultSize);

    /// <inheritdoc/>
    public IEnumerable<McpServerTool> CreateServerTools() =>
        McpToolFactory.Create(this, _ => $" Limits on this node: at most {_maxFrames} frames (maxFrames) and {MaxTreeDepth} levels per trace.", _maxResultSize);

    /// <summary>Returns the bounded call tree of a mined transaction.</summary>
    [McpServerTool(Name = "trace_transaction", Title = "Trace transaction calls", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [McpToolOutputSchema(TraceOutputSchema)]
    [Description("Replays a mined transaction and returns its internal call tree (like geth's callTracer): every CALL, STATICCALL, " +
        "DELEGATECALL, CALLCODE, CREATE, CREATE2 and SELFDESTRUCT frame with from, to, value (wei hex plus valueFormatted in the native " +
        "currency: ETH, or xDAI on Gnosis), gas and gasUsed (integers), the 4-byte selector and well-known method name, truncated output, " +
        "the error and a decoded revert reason ({kind, message, selector}), and nested calls. Use it to see which contracts a transaction " +
        "touched and where it failed; for a one-call plain-English overview prefer explain_transaction. The tree is capped by frame count " +
        "and depth: result.truncated is true and frames list omittedCalls when parts were cut; totalFrames counts every frame. " +
        "By default at most 300 frames are returned so the result fits in an LLM context; pass maxFrames (up to this node's limit) for more, " +
        "or maxDepth to see the top levels only (maxDepth=0 traces just the top-level call, which is also much cheaper for the node). " +
        "The node builds the whole call tree in memory before it is cut, so very large transactions are expensive to trace. " +
        "Pending transactions cannot be traced. Replaying needs the state of the parent block, so old blocks on pruned nodes fail with unavailable.")]
    public Task<CallToolResult> TraceTransaction(
        [Description("32-byte transaction hash: 0x followed by 64 hex characters.")] string hash,
        [Description("Optional maximum call depth to expand (0 = only the top-level call), at most 24. Default 24.")] int? maxDepth = null,
        [Description("If true, include each frame's call data (truncated to 1024 bytes) and longer output. Default false: only the selector and sizes.")] bool includeInput = false,
        [Description("Optional maximum number of frames to return, from 1 up to this node's limit. Default 300.")] int? maxFrames = null,
        CancellationToken cancellationToken = default)
    {
        if (!McpToolInput.TryParseHash(hash, nameof(hash), out Hash256? txHash, out string? error))
        {
            return Task.FromResult(McpToolExecutor.Error(McpToolErrorCodes.InvalidInput, error));
        }

        if (maxDepth is < 0 or > MaxTreeDepth)
        {
            return Task.FromResult(McpToolExecutor.Error(McpToolErrorCodes.InvalidInput, $"'maxDepth' must be between 0 and {MaxTreeDepth}."));
        }

        if (maxFrames is < 1 || maxFrames > _maxFrames)
        {
            return Task.FromResult(McpToolExecutor.Error(McpToolErrorCodes.InvalidInput, $"'maxFrames' must be between 1 and {_maxFrames}."));
        }

        if (!_tracingEnabled)
        {
            return Task.FromResult(McpToolExecutor.Error(McpToolErrorCodes.Unavailable,
                "Transaction tracing is disabled on this node (Mcp.EnableTracing=false); use explain_transaction or get_transaction_receipt instead."));
        }

        int depthLimit = maxDepth ?? MaxTreeDepth;
        int frameLimit = maxFrames ?? Math.Min(DefaultMaxFrames, _maxFrames);
        return executor.ExecuteAsync<IDebugRpcModule>("trace_transaction", nameof(IDebugRpcModule.debug_traceTransaction), async (debug, token) =>
        {
            ulong blockNumber;
            using (ModuleLease<IEthRpcModule> eth = await executor.RentAsync<IEthRpcModule>(nameof(IEthRpcModule.eth_getTransactionByHash)))
            {
                if (eth.Module is null)
                {
                    return McpToolExecutor.Error(McpToolErrorCodes.Unavailable, "The eth JSON-RPC module is not available on this node.");
                }

                using ResultWrapper<TransactionForRpc?> tx = eth.Module.eth_getTransactionByHash(txHash);
                if (tx.Result.ResultType != ResultType.Success)
                {
                    return executor.Failure("trace_transaction", tx);
                }

                if (tx.Data is null)
                {
                    return McpToolExecutor.Error(McpToolErrorCodes.NotFound,
                        $"Transaction {txHash} is not known to this node. Check the hash and the network (chain_info).");
                }

                if (tx.Data.BlockNumber is not { } number)
                {
                    return McpToolExecutor.Error(McpToolErrorCodes.Unavailable,
                        "The transaction is still pending in the mempool and has not executed yet; use simulate_transaction to dry-run it, or retry once it is mined.");
                }

                blockNumber = number;
            }

            // Replaying a transaction starts from the parent block's state.
            if (blockNumber > 0 && capabilities.CheckState((long)blockNumber - 1) is { } stateUnavailable)
            {
                return stateUnavailable;
            }

            token.ThrowIfCancellationRequested();
            (JsonObject? payload, IResultWrapper? failure) = McpTxCallTree.Run(debug, txHash, _timeout,
                root => BuildTrace(txHash, blockNumber, root, depthLimit, frameLimit, includeInput), onlyTopCall: depthLimit == 0);
            if (failure is not null)
            {
                return executor.Failure("trace_transaction", failure);
            }

            return executor.Success(payload);
        }, cancellationToken);
    }

    private JsonObject BuildTrace(Hash256 txHash, ulong blockNumber, NativeCallTracerCallFrame? root, int depthLimit, int frameLimit, bool includeInput)
    {
        int emitted = 0;
        bool truncated = false;
        // Frames stop once their estimated size would take the result near MaxResultSize, instead of failing the whole trace.
        long budget = Math.Max(1, _maxResultSize / 4 * 3);
        int total = root is null ? 0 : McpTxCallTree.CountFrames(root);
        JsonNode? rootJson = root is null ? null : Frame(root, 0, depthLimit, frameLimit, includeInput, ref emitted, ref truncated, ref budget);

        JsonObject result = new()
        {
            ["transactionHash"] = txHash.ToString(),
            ["blockNumber"] = blockNumber,
            ["status"] = root?.Error is null ? "success" : "failed",
            ["gasUsed"] = root?.GasUsed ?? 0,
            ["totalFrames"] = total,
            ["returnedFrames"] = emitted,
            ["truncated"] = truncated,
            ["maxDepth"] = depthLimit,
            ["maxFrames"] = frameLimit,
            ["root"] = rootJson
        };

        if (depthLimit == 0)
        {
            result["note"] = "maxDepth=0 traced only the top-level call: nested frames were not recorded, so totalFrames counts only that call.";
        }

        return result;
    }

    private JsonObject Frame(NativeCallTracerCallFrame frame, int depth, int depthLimit, int frameLimit, bool includeInput, ref int emitted, ref bool truncated, ref long budget)
    {
        emitted++;
        budget -= EstimateFrameSize(frame, includeInput);
        JsonObject json = new() { ["type"] = frame.Type.ToString() };
        if (frame.From is not null) json["from"] = McpEthHelpers.Checksum(frame.From);
        if (frame.To is not null) json["to"] = McpEthHelpers.Checksum(frame.To);
        if (frame.Value is { IsZero: false } value)
        {
            json["value"] = McpTxFormat.Hex(value);
            json["valueFormatted"] = $"{McpEthHelpers.FormatNative(value)} {profile.NativeCurrencySymbol}";
        }

        json["gas"] = frame.Gas;
        json["gasUsed"] = frame.GasUsed;

        ReadOnlySpan<byte> input = frame.Input is null ? default : frame.Input.AsSpan();
        bool isCreate = frame.Type is Instruction.CREATE or Instruction.CREATE2;
        if (!isCreate && input.Length >= 4)
        {
            json["selector"] = McpTxFormat.HexPrefix(input, 4);
            if (McpTxMethods.TryName(input) is { } method) json["method"] = method;
        }

        json["inputSize"] = input.Length;
        if (includeInput && input.Length > 0)
        {
            json["input"] = McpTxFormat.HexPrefix(input, MaxInputBytes);
        }

        ReadOnlySpan<byte> output = frame.Output is null ? default : frame.Output.AsSpan();
        if (output.Length > 0 && !isCreate)
        {
            json["output"] = McpTxFormat.HexPrefix(output, includeInput ? MaxInputBytes : McpTxCallTree.DefaultOutputBytes);
            json["outputSize"] = output.Length;
        }

        if (frame.Error is not null)
        {
            json["error"] = frame.Error;
            if (McpTxCallTree.DecodeRevert(frame) is { } revert)
            {
                json["revert"] = McpTxCallTree.RevertJson(revert);
            }
            else if (frame.RevertReason is not null)
            {
                json["revert"] = McpTxCallTree.RevertJson(new McpDecodedRevert("Error", McpKnownAbi.SanitizeRevertText(frame.RevertReason), null));
            }
        }

        ReadOnlySpan<NativeCallTracerCallFrame> children = frame.Calls.AsSpan();
        if (children.Length > 0)
        {
            JsonArray calls = [];
            int omitted = 0;
            foreach (NativeCallTracerCallFrame child in children)
            {
                if (depth + 1 > depthLimit || emitted >= frameLimit || budget <= 0)
                {
                    omitted += McpTxCallTree.CountFrames(child);
                    truncated = true;
                    continue;
                }

                calls.Add(Frame(child, depth + 1, depthLimit, frameLimit, includeInput, ref emitted, ref truncated, ref budget));
            }

            if (calls.Count > 0) json["calls"] = calls;
            if (omitted > 0) json["omittedCalls"] = omitted;
        }

        return json;
    }

    // An upper estimate of a frame's JSON size: fixed fields plus the hex it carries.
    private static long EstimateFrameSize(NativeCallTracerCallFrame frame, bool includeInput)
    {
        const int fixedFields = 400;
        int inputLength = frame.Input?.Count ?? 0;
        int outputLength = frame.Output?.Count ?? 0;
        int outputBytes = Math.Min(outputLength, includeInput ? MaxInputBytes : McpTxCallTree.DefaultOutputBytes);
        int inputBytes = includeInput ? Math.Min(inputLength, MaxInputBytes) : 0;
        return fixedFields + 2L * (inputBytes + outputBytes);
    }
}
