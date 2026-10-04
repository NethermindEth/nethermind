// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Test;
using Nethermind.JsonRpc.Test;
using NUnit.Framework;

/// <inheritdoc cref="JsonMetadataCoverageBase"/>
/// <remarks>Outside any namespace, so it covers every fixture in the assembly.</remarks>
[SetUpFixture]
internal class JsonMetadataCoverage() : JsonMetadataCoverageBase(JsonRpcTestJsonTypes.TestOnly, TestOnly)
{
    private static readonly Dictionary<Type, string> TestOnly = new()
    {
        [typeof(string[])] = "test local data file",
    };
}
