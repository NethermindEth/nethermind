// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp.RlpWriter;

// TODO: Naming could be better
public interface IRlpWriter
{
    public void Write(int value);
    public void Write(ulong value);
    public void Write(ReadOnlySpan<byte> value);
    public void Write(UInt256 value);
    public void Write(long value);
    public void Write(Address value);
    public void Write(Memory<byte>? value);
    public void Write(byte[] value);
    public void Write(Rlp value);
}
