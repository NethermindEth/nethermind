// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Test;

/// <summary>Types the engine API tests serialize, for every assembly that runs them.</summary>
public static class EngineTestJsonTypes
{
    /// <summary>Uncovered types only the tests serialize, with the reason no production path needs them.</summary>
    public static readonly IReadOnlyDictionary<Type, string> TestOnly = new Dictionary<Type, string>
    {
        [typeof(ExecutionPayloadBodyV1Result[])] = "test expected value",
        [typeof(ExecutionPayloadBodyV2Result[])] = "test expected value",
        [typeof(IEnumerable<Withdrawal>)] = "test request parameter",
    };
}
