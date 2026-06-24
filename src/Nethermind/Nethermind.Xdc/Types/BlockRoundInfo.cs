// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;

namespace Nethermind.Xdc.Types;

public class BlockRoundInfo(Hash256 hash256, ulong round, long number)
{
    private static readonly XdcBlockInfoDecoder _decoder = new();

    public Hash256 Hash { get; set; } = hash256;
    public ulong Round { get; set; } = round;
    public long BlockNumber { get; set; } = number;

    public Hash256 SigHash()
    {
        KeccakRlpWriter writer = new();
        _decoder.Encode(ref writer, this);
        return writer.GetHash();
    }
}
