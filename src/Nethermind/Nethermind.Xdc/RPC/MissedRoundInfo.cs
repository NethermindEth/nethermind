// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Xdc;

public class MissedRoundInfo
{
    public ulong Round { get; set; }
    public Address? Miner { get; set; }
    public Hash256? CurrentBlockHash { get; set; }
    public UInt256? CurrentBlockNum { get; set; }
    public Hash256? ParentBlockHash { get; set; }
    public UInt256? ParentBlockNum { get; set; }
}
