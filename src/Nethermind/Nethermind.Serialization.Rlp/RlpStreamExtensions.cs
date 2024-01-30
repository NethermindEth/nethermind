// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

public static class RlpStreamExtensions
{
    public static Hash256 ComputeNextItemHash(this RlpStream rlpStream)
    {
        return Keccak.Compute(rlpStream.PeekNextItem());
    }
}
