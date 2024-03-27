// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Facade.Proxy.Models.Simulate;

namespace Nethermind.Facade.Simulate;

public class SimulateOutput
{
    public string? Error { get; set; }

    public IReadOnlyList<SimulateBlockResult> Items { get; set; }
}
