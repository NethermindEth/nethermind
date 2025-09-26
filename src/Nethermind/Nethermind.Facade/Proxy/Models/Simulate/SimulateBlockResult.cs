// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Facade.Eth;

namespace Nethermind.Facade.Proxy.Models.Simulate;

/// <summary>
/// Represents the successful result of a simulation for the JSON-RPC serialization.
/// </summary>
/// <remarks>
/// Serializes <see cref="Calls"/> when used in <c>eth_simulateV1</c>
/// or <see cref="Traces"/> when used in <c>debug_simulateV1</c>.
/// </remarks>
public class SimulateBlockResult<TTrace>(Block source, bool includeFullTransactionData, ISpecProvider specProvider)
    : BlockForRpc(source, includeFullTransactionData, specProvider)
{
    private static bool ShouldSerializeCalls => typeof(TTrace) == typeof(SimulateCallResult);
    private readonly IReadOnlyCollection<TTrace> _calls = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyCollection<TTrace>? Calls
    {
        get { return ShouldSerializeCalls ? _calls : null; }
        init { _calls = value ?? []; }
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyCollection<TTrace>? Traces => ShouldSerializeCalls ? null : _calls;
}
