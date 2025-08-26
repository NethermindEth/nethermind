// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Consensus.Xdc;
internal class BlockInfo
{
    public BlockInfo(Hash256 hash, ulong parentHash, UInt256 number)
    {
        Number = number;
        Hash = hash;
        Number = number;
    }
    public Hash256 Hash { get; }
    public ulong Round { get; }
    public UInt256 Number { get; }

}
