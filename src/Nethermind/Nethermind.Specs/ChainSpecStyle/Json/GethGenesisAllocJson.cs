// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;
using System.Collections.Generic;

namespace Nethermind.Specs.ChainSpecStyle.Json;

/// <summary>
/// Represents an account allocation in a Geth-style genesis.json file as defined in EIP-7949.
/// </summary>
public class GethGenesisAllocJson
{
    public UInt256? Balance { get; set; }

    public ulong? Nonce { get; set; }

    public byte[]? Code { get; set; }

    public Dictionary<UInt256, byte[]>? Storage { get; set; }
}
