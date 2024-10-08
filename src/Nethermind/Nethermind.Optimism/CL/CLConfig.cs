// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Optimism.CL;

public class CLConfig : ICLConfig
{
    public Address? BatcherInboxAddress { get; set; }
    public Address? BatcherAddress { get; set; }
    public string? L1BeaconApiEndpoint { get; set; }
    public string? L1EthApiEndpoint { get; set; }
}
