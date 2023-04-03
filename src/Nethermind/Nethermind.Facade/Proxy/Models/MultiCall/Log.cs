// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Facade.Proxy.Models.MultiCall;

public class Log
{
    public ulong LogIndex { get; set; }
    public Keccak BlockHash { get; set; }
    public ulong BlockNumber { get; set; }
    public Address Address { get; set; }
    public byte[] Data { get; set; }
    public Keccak[] Topics { get; set; }
}
