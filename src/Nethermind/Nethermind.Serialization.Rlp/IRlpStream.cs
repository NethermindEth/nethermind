// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp;

public interface IRlpStream
{
    int Position { get; set; }
    bool IsNextItemNull();
    byte ReadByte();
    int ReadSequenceLength();
    Address? DecodeAddress();
    UInt256 DecodeUInt256(int length = -1);
    void Check(int nextCheck);
}
