// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Facade.Eth;
using Nethermind.Int256;

namespace Nethermind.Facade.Proxy.Models.Simulate;

public class SimulateBlockResult<TTrace>(Block source, bool includeFullTransactionData, ISpecProvider specProvider)
    : BlockForRpc(source, includeFullTransactionData, specProvider)
{
    [JsonIgnore]
    public new UInt256 TotalDifficulty { get => base.TotalDifficulty; set => base.TotalDifficulty = value; }
    public string? Error { get; set; }
    [JsonIgnore]
    public bool Success { get; set; } = true;
    public IReadOnlyCollection<TTrace> Calls { get; set; } = [];
}
