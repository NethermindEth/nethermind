// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp.RlpWriter;

// TODO: Naming could be better
public interface IRlpWriter
{
    public void Push(int value);
    public void Push(ulong value);
    public void Push(ReadOnlySpan<byte> value);
    public void Push(UInt256 value);
    public void Push(long value);
    public void Push(Address value);
    public void Push(Memory<byte>? value);
    public void Push(byte[] value);
    public void Push(Rlp value);
}
