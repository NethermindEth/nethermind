// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.ChainSpecStyle.Json;

/// <summary>The <c>config.discovery</c> section of a Besu genesis file.</summary>
public class GethGenesisDiscoveryJson
{
    public string[]? Bootnodes { get; set; }
}
