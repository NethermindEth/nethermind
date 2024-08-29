// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp.RlpWriter;

public interface IRlpWriter
{
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
    public void Write(AccessList? value, RlpBehaviors rlpBehaviors = RlpBehaviors.None);
}
