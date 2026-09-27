// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Nodes;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.Mcp.Plugin.Tools.Abi;

namespace Nethermind.Mcp.Plugin.Tools;

/// <summary>A native value transfer found in a call trace.</summary>
/// <param name="Type">The frame type, such as <c>CALL</c> or <c>SELFDESTRUCT</c>.</param>
/// <param name="From">The sender.</param>
/// <param name="To">The recipient.</param>
/// <param name="Value">The amount in wei.</param>
/// <param name="Depth">The call depth; 1 is a call made directly by the transaction's target.</param>
internal readonly record struct McpValueTransfer(string Type, Address From, Address To, UInt256 Value, int Depth);

/// <summary>The call frame where a failed transaction's error originated.</summary>
internal sealed record McpFailingFrame(int Depth, string Type, Address? From, Address? To, string? Selector, string? Method, string? Error, McpDecodedRevert? Revert);

/// <summary>Runs the native <c>callTracer</c> over a mined transaction and reads its call tree.</summary>
/// <remarks>
/// The tracer is a non-streaming <c>debug_traceTransaction</c> call (a named tracer never streams), so the whole tree,
/// with each frame's full input and output, is materialised by the debug module before it returns; callers cut it only
/// afterwards. <c>onlyTopCall</c> is the tracer's only bound, so a transaction with very many frames costs node memory for
/// the duration of the call. The frames are pooled and released when the result wrapper
/// is disposed, so every reader here runs inside <see cref="Run{T}"/>, before that happens.
/// </remarks>
internal static class McpTxCallTree
{
    /// <summary>Output bytes kept per frame unless the caller asks for more.</summary>
    public const int DefaultOutputBytes = 256;

    private static readonly JsonElement OnlyTopCallConfig = JsonSerializer.SerializeToElement(new { onlyTopCall = true });

    /// <summary>Traces <paramref name="txHash"/> with the call tracer and passes the root frame to <paramref name="read"/>.</summary>
    /// <param name="debug">The rented debug module.</param>
    /// <param name="txHash">The mined transaction.</param>
    /// <param name="timeout">The tracer's own time limit.</param>
    /// <param name="read">Reads the tree before its pooled frames are released.</param>
    /// <param name="onlyTopCall">Whether the tracer records only the top-level frame (callTracer <c>onlyTopCall</c>), so nested frames are never built.</param>
    /// <returns>The value read, or the failed JSON-RPC wrapper; the root is <see langword="null"/> for a transaction with no frames.</returns>
    public static (T? Value, IResultWrapper? Failure) Run<T>(IDebugRpcModule debug, Hash256 txHash, TimeSpan timeout, Func<NativeCallTracerCallFrame?, T> read, bool onlyTopCall = false)
    {
        GethTraceOptions options = new()
        {
            Tracer = NativeCallTracer.CallTracer,
            StreamMode = false,
            Timeout = timeout,
            TracerConfig = onlyTopCall ? OnlyTopCallConfig : null
        };
        using ResultWrapper<GethLikeTxTrace> result = debug.debug_traceTransaction(txHash, options);
        if (result.Result.ResultType != ResultType.Success)
        {
            return (default, result);
        }

        return (read(result.Data?.CustomTracerResult?.Value as NativeCallTracerCallFrame), null);
    }

    /// <summary>Counts every frame of the tree rooted at <paramref name="root"/>.</summary>
    public static int CountFrames(NativeCallTracerCallFrame root)
    {
        int count = 0;
        Stack<NativeCallTracerCallFrame> pending = new();
        pending.Push(root);
        while (pending.TryPop(out NativeCallTracerCallFrame? frame))
        {
            count++;
            foreach (NativeCallTracerCallFrame child in frame.Calls.AsSpan())
            {
                pending.Push(child);
            }
        }

        return count;
    }

    /// <summary>Collects up to <paramref name="max"/> native value transfers made by nested frames that were not reverted.</summary>
    /// <returns>The total number of such transfers, which may exceed the number collected.</returns>
    public static int CollectValueTransfers(NativeCallTracerCallFrame root, List<McpValueTransfer> sink, int max)
    {
        int total = 0;
        Stack<(NativeCallTracerCallFrame Frame, int Depth)> pending = new();
        if (root.Error is null)
        {
            foreach (NativeCallTracerCallFrame child in root.Calls.AsSpan())
            {
                pending.Push((child, 1));
            }
        }

        while (pending.TryPop(out (NativeCallTracerCallFrame Frame, int Depth) item))
        {
            NativeCallTracerCallFrame frame = item.Frame;
            // A failed frame's value transfer and everything below it were rolled back.
            if (frame.Error is not null)
            {
                continue;
            }

            bool movesValue = frame.Type is Instruction.CALL or Instruction.CREATE or Instruction.CREATE2 or Instruction.SELFDESTRUCT;
            if (movesValue && frame.Value is { } value && !value.IsZero && frame.From is not null && frame.To is not null)
            {
                total++;
                if (sink.Count < max)
                {
                    sink.Add(new McpValueTransfer(frame.Type.ToString(), frame.From, frame.To, value, item.Depth));
                }
            }

            ReadOnlySpan<NativeCallTracerCallFrame> children = frame.Calls.AsSpan();
            for (int i = children.Length - 1; i >= 0; i--)
            {
                pending.Push((children[i], item.Depth + 1));
            }
        }

        return total;
    }

    /// <summary>
    /// Follows the failure from the root down to the deepest failed frame whose error propagated: at each level the last
    /// failed child is taken, since a later successful sibling means the parent handled that failure.
    /// </summary>
    public static McpFailingFrame? FindFailingFrame(NativeCallTracerCallFrame root)
    {
        if (root.Error is null)
        {
            return null;
        }

        NativeCallTracerCallFrame frame = root;
        int depth = 0;
        while (true)
        {
            ReadOnlySpan<NativeCallTracerCallFrame> children = frame.Calls.AsSpan();
            NativeCallTracerCallFrame? next = null;
            if (children.Length > 0 && children[^1].Error is not null)
            {
                next = children[^1];
            }

            // A revert that re-throws the child's data bubbles it up; anything else means the parent failed on its own.
            if (next is null || !BubblesUp(frame, next))
            {
                break;
            }

            frame = next;
            depth++;
        }

        ReadOnlySpan<byte> input = frame.Input is null ? default : frame.Input.AsSpan();
        bool isCreate = frame.Type is Instruction.CREATE or Instruction.CREATE2;
        string? selector = !isCreate && input.Length >= 4 ? McpTxFormat.HexPrefix(input, 4) : null;
        return new McpFailingFrame(
            depth,
            frame.Type.ToString(),
            frame.From,
            frame.To,
            selector,
            isCreate ? null : McpTxMethods.TryName(input),
            frame.Error,
            DecodeRevert(frame));
    }

    /// <summary>Decodes a failed frame's revert data, or returns <see langword="null"/> if it did not revert.</summary>
    public static McpDecodedRevert? DecodeRevert(NativeCallTracerCallFrame frame) =>
        frame.Error == EvmExceptionType.Revert.GetEvmExceptionDescription()
            ? McpKnownAbi.DecodeRevert(frame.Output is null ? default : frame.Output.AsSpan())
            : null;

    /// <summary>Converts a decoded revert into JSON: <c>{"kind", "message", "selector"?}</c>.</summary>
    public static JsonObject RevertJson(McpDecodedRevert revert)
    {
        JsonObject json = new() { ["kind"] = revert.Kind, ["message"] = revert.Message };
        if (revert.Selector is not null)
        {
            json["selector"] = revert.Selector;
        }

        return json;
    }

    private static bool BubblesUp(NativeCallTracerCallFrame parent, NativeCallTracerCallFrame child)
    {
        string revert = EvmExceptionType.Revert.GetEvmExceptionDescription()!;
        if (parent.Error != revert)
        {
            // The parent halted itself (out of gas, invalid opcode...), so its own frame is the origin.
            return false;
        }

        ReadOnlySpan<byte> parentOutput = parent.Output is null ? default : parent.Output.AsSpan();
        ReadOnlySpan<byte> childOutput = child.Output is null ? default : child.Output.AsSpan();
        // Contracts usually re-throw the callee's revert data verbatim; an empty parent revert after a failed child
        // (require(success)) also points at the child.
        return parentOutput.SequenceEqual(childOutput) || parentOutput.IsEmpty;
    }
}
