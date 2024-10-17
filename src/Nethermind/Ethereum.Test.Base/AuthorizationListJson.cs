// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Ethereum.Test.Base;
public class AuthorizationListJson
{
    public ulong ChainId { get; set; }
    public Address Address { get; set; }
    public ulong Nonce { get; set; }
    public ulong V { get; set; }
    public UInt256 R { get; set; }
    public UInt256 S { get; set; }
    public Address Signer { get; set; }
}
