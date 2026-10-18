// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.FourByte;

// 4byteTracer searches for 4byte-identifiers, and collects them for post-processing.
// It collects the methods identifiers along with the size of the supplied data, so
// a reversed signature can be matched against the size of the data.
//
// Example:
//   > debug.traceTransaction( "0x214e597e35da083692f5386141e69f47e973b2c56e7a8073b1ea08fd7571e9de", {tracer: "4byteTracer"})
//   {
//     0x27dc297e-128: 1,
//     0x38cc4831-0: 2,
//     0x524f3889-96: 1,
//     0xadf59f99-288: 1,
//     0xc281d19e-0: 1
//   }
public sealed class Native4ByteTracer : GethLikeNativeTxTracer
{
    public const string FourByteTracer = "4byteTracer";

    private readonly Transaction _transaction;
    private readonly Dictionary<FourByteId, int> _4ByteIds = [];

    public Native4ByteTracer(Transaction transaction, GethTraceOptions options) : base(options)
    {
        _transaction = transaction;
        IsTracingActions = true;
        IsTracingStack = false;
        IsTracingOpLevelStorage = false;
        IsTracingReturnData = false;
    }

    public override bool IsTracingInstructions => false;

    protected override GethLikeTxTrace CreateTrace() => new();

    public override GethLikeTxTrace BuildResult()
    {
        GethLikeTxTrace result = base.BuildResult();

        result.TxHash = _transaction.Hash;
        result.CustomTracerResult = new GethLikeCustomTrace { Value = FormatIds() };

        return result;
    }

    public override void ReportAction(ulong gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
    {
        base.ReportAction(gas, value, from, to, input, callType, isPrecompileCall);

        if (Depth == 0)
        {
            CaptureStart(input);
        }
        else
        {
            CaptureEnter(callType, input, to, isPrecompileCall);
        }
    }

    private void CaptureStart(ReadOnlyMemory<byte> input)
    {
        if (input.Length >= 4)
        {
            Store4ByteIds(input, input.Length - 4);
        }
    }

    private void CaptureEnter(ExecutionType callType, ReadOnlyMemory<byte> input, Address? to, bool isPrecompileCall)
    {
        if (input.Length >= 4
            && callType.IsAnyCall()
            && to is not null
            && !isPrecompileCall)
        {
            Store4ByteIds(input, input.Length - 4);
        }
    }

    /*
     * Store4ByteIds counts the occurrences of 4byteId keys and stores them in a dictionary.
     * Each 4byteId key is composed of the function signature along with the size of the supplied call data.
     * The key is formatted only once per distinct id, when the result is built.
     *
     * 4byteId format: SELECTOR-CALLDATASIZE
     * example 4byteId: 0x27dc297e-128
     */
    private void Store4ByteIds(ReadOnlyMemory<byte> input, int size)
    {
        FourByteId id = new(BinaryPrimitives.ReadUInt32BigEndian(input.Span), size);
        CollectionsMarshal.GetValueRefOrAddDefault(_4ByteIds, id, out _) += 1;
    }

    private Dictionary<string, int> FormatIds()
    {
        Dictionary<string, int> ids = new(_4ByteIds.Count);
        foreach (KeyValuePair<FourByteId, int> entry in _4ByteIds)
        {
            ids.Add(entry.Key.ToString(), entry.Value);
        }

        return ids;
    }

    private readonly record struct FourByteId(uint Selector, int Size)
    {
        private const int SelectorChars = 2 + sizeof(uint) * 2;

        public override string ToString() =>
            string.Create(SelectorChars + 1 + GetDigitsBase10(Size), this, static (span, id) =>
            {
                Span<byte> selector = stackalloc byte[sizeof(uint)];
                BinaryPrimitives.WriteUInt32BigEndian(selector, id.Selector);
                Bytes.OutputBytesToCharHex(ref MemoryMarshal.GetReference(selector), sizeof(uint), ref MemoryMarshal.GetReference(span), true, 0);
                span[SelectorChars] = '-';
                id.Size.TryFormat(span[(SelectorChars + 1)..], out _);
            });

        private static int GetDigitsBase10(int n) => n == 0 ? 1 : (int)Math.Floor(Math.Log10(Math.Abs(n)) + 1);
    }
}
