// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.JsonRpc;

/// <summary>Lets batch items of some methods execute alongside their neighbours.</summary>
public interface IBatchConcurrencyStrategy
{
    /// <summary>The methods this strategy decides for.</summary>
    IReadOnlyCollection<string> Methods { get; }

    /// <summary>Whether <paramref name="request"/> may execute in a concurrent batch window of <paramref name="windowSize"/> items.</summary>
    /// <remarks>Runs on the batch loop before the item executes, so it must not throw.</remarks>
    bool CanExecuteConcurrently(JsonRpcRequest request, int windowSize);
}
