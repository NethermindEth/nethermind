// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Facade.Proxy.Models;

public class AccountOverride
{
    public UInt256? Nonce { get; set; }
    public UInt256? Balance { get; set; }
    public byte[]? Code { get; set; }
    public Address? MovePrecompileToAddress { get; set; }

    /// <summary>
    /// Storage for AccountOverrideState
    /// </summary>
    public Dictionary<UInt256, ValueKeccak>? State { get; set; }

    /// <summary>
    /// Storage difference for AccountOverrideStateDiff
    /// </summary>
    public Dictionary<UInt256, ValueKeccak>? StateDiff { get; set; }
}
