// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core;

namespace Nethermind.Optimism.CL;

public interface ICLConfig : IConfig
{
    string? L1BeaconApiEndpoint { get; set; }
    string? L1EthApiEndpoint { get; set; }
}
