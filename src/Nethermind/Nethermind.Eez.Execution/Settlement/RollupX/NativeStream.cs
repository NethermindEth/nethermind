// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

/// <summary>
/// A RollupX batch's blob stream: <c>00 | ChainOperation(rollup, operations) | message* | CloseBlobStream</c>,
/// then only zero bytes. Scalars are little-endian; <c>bytes</c> lengths are varints that may be padded. The
/// messages must form complete cross-chain transactions: calls and returns nest at most
/// <see cref="MaxCallDepth"/> deep, and a <c>Revert</c> closes the innermost <c>Snapshot</c> at its own depth.
/// </summary>
public sealed record NativeStream(ReadOnlyMemory<byte> Operations, StreamMessage[] Messages)
{
    public const byte ProtocolVersion = 0x00;
    public const int MaxCallDepth = 64;

    private const byte CloseBlobStream = 1;
    private const byte ChainOperation = 2;
    private const byte Initiate = 3;
    private const byte Call = 4;
    private const byte StaticCall = 5;
    private const byte ReturnSuccess = 6;
    private const byte ReturnFail = 7;
    private const byte Snapshot = 8;
    private const byte Revert = 9;
    private const byte Finish = 10;

    /// <exception cref="EezSettlementException">The stream is malformed, names another rollup or has non-zero padding.</exception>
    public static NativeStream Decode(ReadOnlyMemory<byte> stream, ulong rollupId)
    {
        DaReader reader = new(stream);
        if (stream.Length == 0 || reader.Byte("protocol version") != ProtocolVersion)
        {
            throw DaReader.Invalid("unsupported blob stream version");
        }

        reader.Expect(ChainOperation);
        ulong chainId = reader.UInt64("chain id");
        if (chainId != rollupId)
        {
            throw DaReader.Invalid($"the stream belongs to rollup {chainId}, not {rollupId}");
        }

        ReadOnlyMemory<byte> operations = reader.Bytes("operations", padded: true);
        Brackets brackets = new();
        List<StreamMessage> messages = [];
        while (true)
        {
            byte type = reader.Byte("message type");
            if (type == CloseBlobStream)
            {
                EnsureZeroPadding(reader.Take("padding", reader.Remaining).Span);
                brackets.EnsureClosed();
                return new NativeStream(operations, messages.ToArray());
            }

            StreamMessage message = DecodeMessage(type, ref reader);
            brackets.Apply(message);
            messages.Add(message);
        }
    }

    /// <summary>The unpadded stream for <paramref name="rollupId"/>; <paramref name="messages"/> must form complete transactions.</summary>
    /// <exception cref="EezSettlementException">The messages do not form complete cross-chain transactions.</exception>
    public static byte[] Encode(ulong rollupId, ReadOnlySpan<byte> operations, IReadOnlyList<StreamMessage> messages)
    {
        Brackets brackets = new();
        List<byte> output = [ProtocolVersion, ChainOperation];
        AddUInt64(output, rollupId);
        AddBytes(output, operations);
        foreach (StreamMessage message in messages)
        {
            brackets.Apply(message);
            EncodeMessage(output, message);
        }

        brackets.EnsureClosed();
        output.Add(CloseBlobStream);
        return output.ToArray();
    }

    private static StreamMessage DecodeMessage(byte type, ref DaReader reader) => type switch
    {
        Initiate => new StreamMessage.Initiate(reader.UInt64("chain id"), reader.Bytes("tx data", padded: true)),
        Call => new StreamMessage.Call(reader.UInt64("to chain"), ReadAddress(ref reader, "from"), ReadAddress(ref reader, "to"),
            new UInt256(reader.Take("value", 32).Span, isBigEndian: false), reader.UInt64("gas"), reader.Bytes("data", padded: true)),
        StaticCall => new StreamMessage.StaticCall(reader.UInt64("to chain"), ReadAddress(ref reader, "from"), ReadAddress(ref reader, "to"),
            reader.UInt64("gas"), reader.Bytes("data", padded: true)),
        ReturnSuccess => new StreamMessage.Return(true, reader.Bytes("return data", padded: true)),
        ReturnFail => new StreamMessage.Return(false, reader.Bytes("return data", padded: true)),
        Snapshot => new StreamMessage.Snapshot(),
        Revert => new StreamMessage.Revert(),
        Finish => new StreamMessage.Finish(),
        _ => throw DaReader.Invalid($"message type {type} is not allowed between the chain operation and the stream's end"),
    };

    private static Address ReadAddress(ref DaReader reader, string field) => new(reader.Take(field, Address.Size).ToArray());

    private static void EnsureZeroPadding(ReadOnlySpan<byte> padding)
    {
        int offset = padding.IndexOfAnyExcept((byte)0);
        if (offset >= 0)
        {
            throw DaReader.Invalid($"non-zero byte {offset} bytes after the stream's end");
        }
    }

    private static void EncodeMessage(List<byte> output, StreamMessage message)
    {
        Span<byte> value = stackalloc byte[32];
        switch (message)
        {
            case StreamMessage.Initiate initiate:
                output.Add(Initiate);
                AddUInt64(output, initiate.ChainId);
                AddBytes(output, initiate.TxData.Span);
                break;
            case StreamMessage.Call call:
                output.Add(Call);
                AddUInt64(output, call.ToChain);
                output.AddRange(call.From.Bytes);
                output.AddRange(call.To.Bytes);
                call.Value.ToLittleEndian(value);
                output.AddRange(value);
                AddUInt64(output, call.Gas);
                AddBytes(output, call.Data.Span);
                break;
            case StreamMessage.StaticCall call:
                output.Add(StaticCall);
                AddUInt64(output, call.ToChain);
                output.AddRange(call.From.Bytes);
                output.AddRange(call.To.Bytes);
                AddUInt64(output, call.Gas);
                AddBytes(output, call.Data.Span);
                break;
            case StreamMessage.Return result:
                output.Add(result.Success ? ReturnSuccess : ReturnFail);
                AddBytes(output, result.ReturnData.Span);
                break;
            case StreamMessage.Snapshot:
                output.Add(Snapshot);
                break;
            case StreamMessage.Revert:
                output.Add(Revert);
                break;
            case StreamMessage.Finish:
                output.Add(Finish);
                break;
        }
    }

    private static void AddUInt64(List<byte> output, ulong value)
    {
        for (int i = 0; i < sizeof(ulong); i++)
        {
            output.Add((byte)(value >> (8 * i)));
        }
    }

    private static void AddBytes(List<byte> output, ReadOnlySpan<byte> value)
    {
        uint remaining = (uint)value.Length;
        while (remaining >= 0x80)
        {
            output.Add((byte)(remaining | 0x80));
            remaining >>= 7;
        }

        output.Add((byte)remaining);
        output.AddRange(value);
    }

    /// <summary>The open transaction, calls and snapshots while messages are applied in order.</summary>
    private sealed class Brackets
    {
        private readonly Stack<ulong> _context = new();
        private readonly Stack<int> _snapshotDepths = new();

        public void Apply(StreamMessage message)
        {
            switch (message)
            {
                case StreamMessage.Initiate initiate:
                    Require(_context.Count == 0, "a transaction opens inside another");
                    Push(initiate.ChainId);
                    break;
                case StreamMessage.Call call:
                    RequireOpen();
                    Push(call.ToChain);
                    break;
                case StreamMessage.StaticCall call:
                    RequireOpen();
                    Push(call.ToChain);
                    break;
                case StreamMessage.Return:
                    Require(_context.Count >= 2, "a return has no open call");
                    Require(_snapshotDepths.Count == 0 || _context.Count - 1 >= _snapshotDepths.Peek(), "a return crosses an open snapshot");
                    _context.Pop();
                    break;
                case StreamMessage.Snapshot:
                    RequireOpen();
                    Require(_snapshotDepths.Count < MaxCallDepth, "snapshots nest too deep");
                    _snapshotDepths.Push(_context.Count);
                    break;
                case StreamMessage.Revert:
                    Require(_snapshotDepths.Count != 0 && _snapshotDepths.Peek() == _context.Count, "a revert has no snapshot at its depth");
                    _snapshotDepths.Pop();
                    break;
                case StreamMessage.Finish:
                    RequireOpen();
                    Require(_context.Count == 1 && _snapshotDepths.Count == 0, "a transaction finishes with an open call or snapshot");
                    _context.Pop();
                    break;
            }
        }

        public void EnsureClosed() => Require(_context.Count == 0 && _snapshotDepths.Count == 0, "the stream ends inside a transaction");

        private void RequireOpen() => Require(_context.Count != 0, "a message appears outside a transaction");

        private void Push(ulong chainId)
        {
            Require(_context.Count < MaxCallDepth, "calls nest too deep");
            _context.Push(chainId);
        }

        private static void Require(bool condition, string reason)
        {
            if (!condition)
            {
                throw DaReader.Invalid(reason);
            }
        }
    }
}
