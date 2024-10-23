// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Facade.Eth;

namespace Nethermind.Facade.Proxy.Models.Simulate;

public class SimulateBlockResult(Block source, bool includeFullTransactionData, ISpecProvider specProvider)
    : BlockForRpc(source, includeFullTransactionData, specProvider)
{
    public List<SimulateCallResult> Calls { get; set; } = new();
}
