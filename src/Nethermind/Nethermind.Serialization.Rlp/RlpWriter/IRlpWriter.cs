// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp.RlpWriter;

public interface IRlpWriter
{
    public void WriteByte(byte value);
    public void Write(byte value);
    public void Write(int value);
    public void Write(ulong value);
    public void Write(ReadOnlySpan<byte> value);
    public void Write(UInt256 value);
    public void Write(long value);
    public void Write(Address value);
    public void Write(Memory<byte>? value);
    public void Write(byte[] value);
    public void Write(Rlp value);
    public void Write(byte[]?[] value);
    public void Write(Hash256? value);
    public void Write(bool value);
    public void Write<T>(IRlpStreamDecoder<T> decoder, T value, RlpBehaviors rlpBehaviors = RlpBehaviors.None);
    public void WriteSequence(Action<IRlpWriter> action);
    public void Wrap(bool when, int bytes, Action<IRlpWriter> action);
}
