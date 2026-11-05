// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.JsonRpc.Modules.Eth;

/// <summary>Calls that only read state and run the EVM execute alongside each other in a batch.</summary>
public sealed class EthCallBatchConcurrency : IBatchConcurrencyStrategy
{
    public IReadOnlyCollection<string> Methods { get; } = ["eth_call", "eth_estimateGas", "eth_createAccessList"];

    public bool CanExecuteConcurrently(JsonRpcRequest request, int windowSize) => true;
}
