// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Xdc;

public class V2BlockInfo
{
    public Hash256? Hash { get; set; }
    public UInt256? Round { get; set; }
    public UInt256? Number { get; set; }
    public Hash256? ParentHash { get; set; }
    public bool Committed { get; set; }
    public Address? Miner { get; set; }
    public ulong Timestamp { get; set; }
    public string? EncodedRLP { get; set; }
    public string? Error { get; set; }
}
