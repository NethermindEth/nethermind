// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Types;

using Round = ulong;

public class BlockInfo
{
    public BlockInfo(Hash256 hash256, ulong round, long number)
    {
        Hash256 = hash256;
        Round = round;
        Number = number;
    }

    [JsonPropertyName("hash")]
    public Hash256 Hash256 { get; set; }

    [JsonPropertyName("round")]
    public Round Round { get; set; }

    [JsonPropertyName("number")]
    public long Number { get; set; }
    public Rlp Hash() => Rlp.Encode(this);
}
