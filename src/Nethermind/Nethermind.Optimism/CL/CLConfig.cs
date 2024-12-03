// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Optimism.CL;

public class CLConfig : ICLConfig
{
    public string? L1BeaconApiEndpoint { get; set; }
    public string? L1EthApiEndpoint { get; set; }
}
