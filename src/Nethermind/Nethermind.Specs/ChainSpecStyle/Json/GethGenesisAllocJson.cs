// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Specs.ChainSpecStyle.Json;

/// <summary>
/// Represents an account allocation in a Geth-style genesis.json file as defined in EIP-7949.
/// </summary>
public class GethGenesisAllocJson
{
    public string Balance { get; set; }

    public string Nonce { get; set; }

    public string Code { get; set; }

    public Dictionary<string, string> Storage { get; set; }
}
