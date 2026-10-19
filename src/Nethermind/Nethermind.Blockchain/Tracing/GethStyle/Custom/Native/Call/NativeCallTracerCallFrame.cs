// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Text.Json.Serialization;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Evm;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;

[JsonConverter(typeof(NativeCallTracerCallFrameConverter))]
public class NativeCallTracerCallFrame : IDisposable
{
    private const int Alive = 0;
    private const int Disposed = 1;
    private int _disposed = Alive;
    private byte[]? _rentedInput;
    private byte[]? _rentedOutput;

    public Instruction Type { get; set; }

    public Address? From { get; set; }

    public ulong Gas { get; set; }

    public ulong GasUsed { get; set; }

    // EIP-8037 two-dimensional gas; set on the top frame for Amsterdam+, null (omitted) otherwise.
    public TwoDimensionalGas? Eip8037Gas { get; set; }

    public Address? To { get; set; }

    public ReadOnlyMemory<byte> Input { get; set; }

    public ReadOnlyMemory<byte> Output { get; set; }

    public string? Error { get; set; }

    public string? RevertReason { get; set; }

    private ArrayPoolList<NativeCallTracerCallFrame>? _calls;

    public ArrayPoolList<NativeCallTracerCallFrame> Calls => _calls ??= new(8);

    internal ReadOnlySpan<NativeCallTracerCallFrame> CallsSpan => _calls is null ? [] : _calls.AsSpan();

    internal bool HasCalls => _calls is not null;

    public ArrayPoolList<NativeCallTracerLogEntry>? Logs { get; set; }

    public UInt256? Value { get; set; }

    /// <summary>Sets <see cref="Input"/> to a pooled copy of <paramref name="input"/>, returned on <see cref="Dispose"/>.</summary>
    internal void CopyInput(ReadOnlySpan<byte> input) => Input = CopyToRented(input, ref _rentedInput);

    /// <summary>Sets <see cref="Output"/> to a pooled copy of <paramref name="output"/>, returned on <see cref="Dispose"/>.</summary>
    internal void CopyOutput(ReadOnlySpan<byte> output) => Output = CopyToRented(output, ref _rentedOutput);

    private static ReadOnlyMemory<byte> CopyToRented(ReadOnlySpan<byte> data, ref byte[]? rented)
    {
        Return(ref rented);
        if (data.IsEmpty) return default;

        rented = ArrayPool<byte>.Shared.Rent(data.Length);
        data.CopyTo(rented);
        return rented.AsMemory(0, data.Length);
    }

    private static void Return(ref byte[]? rented)
    {
        if (rented is null) return;

        ArrayPool<byte>.Shared.Return(rented);
        rented = null;
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, Disposed, Alive) == Alive)
        {
            Return(ref _rentedInput);
            Return(ref _rentedOutput);
            Logs?.Dispose();
            _calls?.DisposeRecursive();
        }
    }
}
