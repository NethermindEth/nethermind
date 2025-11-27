// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Evm;

public interface IEvmMemory : IDisposable
{
    ulong Size { get; }
    bool TrySaveWord(in UInt256 location, Span<byte> word);
    bool TrySaveByte(in UInt256 location, byte value);
    bool TrySave(in UInt256 location, Span<byte> value);
    bool TrySave(in UInt256 location, byte[] value);
    bool TryLoadSpan(in UInt256 location, out Span<byte> data);
    bool TryLoadSpan(in UInt256 location, in UInt256 length, out Span<byte> data);
    bool TryLoad(in UInt256 location, in UInt256 length, out ReadOnlyMemory<byte> data);
    long CalculateMemoryCost(in UInt256 location, in UInt256 length, out bool outOfGas);
    TraceMemory GetTrace();
}
