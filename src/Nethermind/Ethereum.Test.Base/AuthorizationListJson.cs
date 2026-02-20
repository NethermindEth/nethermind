// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Ethereum.Test.Base;

public class AuthorizationListJson
{
    public UInt256 ChainId { get; set; }
    public Address Address { get; set; }
    public UInt256 Nonce { get; set; }
    public ulong V { get; set; }
    public string R { get; set; }
    public string S { get; set; }
    public Address Signer { get; set; }
}
