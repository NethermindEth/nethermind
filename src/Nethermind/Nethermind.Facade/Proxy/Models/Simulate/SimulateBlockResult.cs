// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Facade.Eth;

namespace Nethermind.Facade.Proxy.Models.Simulate;

public class SimulateBlockResult<TTrace>(Block source, bool includeFullTransactionData, ISpecProvider specProvider)
    : BlockForRpc(source, includeFullTransactionData, specProvider)
{
    public IReadOnlyCollection<TTrace> Calls { get; set; } = [];
    public IReadOnlyCollection<TTrace> Traces => Calls;

    private bool ShouldSerializeCalls() => typeof(TTrace) == typeof(SimulateCallResult);
    private bool ShouldSerializeTraces() => !ShouldSerializeCalls();
}
