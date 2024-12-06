// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Optimism.CL;

public class CLConfig : ICLConfig
{
    public bool Enabled { get; set; } = false;
    public string P2PHost { get; set; } = "127.0.0.1";
    public int P2PPort { get; set; } = 3030;
    public string? L1BeaconApiEndpoint { get; set; }
    public string? L1EthApiEndpoint { get; set; }
}
