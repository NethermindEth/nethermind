// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp;

public interface IRlpStream
{
    int Position { get; set; }
    void Check(int nextCheck);
    bool IsNextItemNull();
    int ReadSequenceLength();
    int PeekNumberOfItemsRemaining(int? beforePosition = null, int maxSearch = int.MaxValue);
    byte ReadByte();


    byte[] DecodeByteArray();
    ReadOnlySpan<byte> DecodeByteArraySpan();

    byte DecodeByte();
    int DecodeInt();
    ulong DecodeULong();
    UInt256 DecodeUInt256(int length = -1);
    BigInteger DecodeUBigInt();

    Hash256? DecodeZeroPrefixKeccak();

    Address? DecodeAddress();
}
