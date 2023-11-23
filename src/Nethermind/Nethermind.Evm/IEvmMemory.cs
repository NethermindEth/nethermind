// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Int256;

namespace Nethermind.Evm;

public interface IEvmMemory : IDisposable
{
    ulong Size { get; }
    void SaveWord(in UInt256 location, Span<byte> word);
    void SaveByte(in UInt256 location, byte value);
    void Save(in UInt256 location, Span<byte> value);
    void Save(in UInt256 location, byte[] value);
    Span<byte> LoadSpan(in UInt256 location);
    Span<byte> LoadSpan(in UInt256 location, in UInt256 length);
    ReadOnlyMemory<byte> Load(in UInt256 location, in UInt256 length);
    long CalculateMemoryCost(in UInt256 location, in UInt256 length);
    IEnumerable<string> GetTrace();
}
