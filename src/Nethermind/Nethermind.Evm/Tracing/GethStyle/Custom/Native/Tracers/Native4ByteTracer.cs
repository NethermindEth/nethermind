// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
namespace Nethermind.Evm.Tracing.GethStyle.Custom.Native.Tracers;

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

    private readonly Dictionary<string, int> _4ByteIds = new();
    private Instruction _op;

    public Native4ByteTracer(
        GethTraceOptions options) : base(options)
    {
        IsTracingActions = true;
    }

    protected override GethLikeTxTrace CreateTrace() => new();

    public override GethLikeTxTrace BuildResult()
    {
        GethLikeTxTrace result = base.BuildResult();

        result.CustomTracerResult = new GethLikeCustomTrace() { Value = _4ByteIds };
        return result;
    }

    public override void ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
    {
        base.ReportAction(gas, value, from, to, input, callType, isPrecompileCall);

        if (Depth == 0)
        {
            CaptureStart(input);
        }
        else
        {
            CaptureEnter(_op, input, to, isPrecompileCall);
        }
    }

    public override void StartOperation(int depth, long gas, Instruction opcode, int pc, bool isPostMerge = false)
    {
        _op = opcode;
    }

    private void CaptureStart(ReadOnlyMemory<byte> input)
    {
        if (input.Length >= 4)
        {
            Store4ByteIds(input, input.Length - 4);
        }
    }

    private void CaptureEnter(Instruction op, ReadOnlyMemory<byte> input, Address? to, bool isPrecompileCall)
    {
        if (input.Length >= 4
            && op is Instruction.DELEGATECALL or Instruction.STATICCALL or Instruction.CALL or Instruction.CALLCODE
            && to is not null
            && !isPrecompileCall)
        {
            Store4ByteIds(input, input.Length - 4);
        }
    }

    /*
     * Store4ByteIds stores the first four bytes of the input data along with the size of the data as keys in a dictionary and counts their occurrences.
     * To optimize performance, string.Create is used to build the 4byteId key instead of using string concatenation.
     *
     * example 4byteId: 0x27dc297e-128
     */
    private void Store4ByteIds(ReadOnlyMemory<byte> input, int size)
    {
        const int length = 4;
        string _4byteId = string.Create(length * 2 + 1 + GetDigitsBase10(size), (input, size), (span, state) =>
        {
            ref char charsRef = ref MemoryMarshal.GetReference(span);
            ReadOnlySpan<byte> bytes = state.input.Span[..length];
            Bytes.OutputBytesToCharHex(ref MemoryMarshal.GetReference(bytes), length, ref charsRef, false, 0);
            span[length * 2] = '-';
            size.TryFormat(span[(length * 2 + 1)..], out _);
        });

        CollectionsMarshal.GetValueRefOrAddDefault(_4ByteIds, _4byteId, out _) += 1;
    }

    private static int GetDigitsBase10(int n) => n == 0 ? 1 : (int)Math.Floor(Math.Log10(Math.Abs(n)) + 1);
}
