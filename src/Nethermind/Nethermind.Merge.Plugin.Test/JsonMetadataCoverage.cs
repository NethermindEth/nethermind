// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.JsonRpc.Test;
using Nethermind.Merge.Plugin.Data;
using NUnit.Framework;

/// <inheritdoc cref="JsonMetadataCoverageBase"/>
/// <remarks>Outside any namespace, so it covers every fixture in the assembly.</remarks>
[SetUpFixture]
internal class JsonMetadataCoverage() : JsonMetadataCoverageBase(JsonRpcTestJsonTypes.TestOnly, TestOnly)
{
    private static readonly Dictionary<Type, string> TestOnly = new()
    {
        [typeof(ExecutionPayloadBodyV1Result[])] = "test expected value",
        [typeof(ExecutionPayloadBodyV2Result[])] = "test expected value",
        [typeof(IEnumerable<Withdrawal>)] = "test request parameter",
    };
}
