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

public class BlockInfo(Hash256 hash256, ulong round, long number)
{
    public Hash256 Hash { get; set; } = hash256;
    public Round Round { get; set; } = round;
    public long Number { get; set; } = number;
    public Hash256 SigHash() => Keccak.Compute(Rlp.Encode(this).Bytes);
}
