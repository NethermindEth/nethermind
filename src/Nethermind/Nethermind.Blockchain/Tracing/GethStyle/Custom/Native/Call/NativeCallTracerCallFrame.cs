// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, Disposed, Alive) == Alive)
        {
            Logs?.Dispose();
            _calls?.DisposeRecursive();
        }
    }
}
