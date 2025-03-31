// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Facade.Proxy.Models.Simulate;

public class Log
{
    public Address Address { get; set; }
    public Hash256[] Topics { get; set; }
    public byte[] Data { get; set; }
    public ulong BlockNumber { get; set; }
    public Hash256 TransactionHash { get; set; }
    public ulong TransactionIndex { get; set; }
    public Hash256 BlockHash { get; set; }

    public ulong LogIndex { get; set; }
    public bool Removed { get; set; } = false;
}
