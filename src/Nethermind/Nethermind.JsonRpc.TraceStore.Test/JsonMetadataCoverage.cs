// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core.Test;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Trace;
using NUnit.Framework;

/// <inheritdoc cref="JsonMetadataCoverageBase"/>
/// <remarks>Outside any namespace, so it covers every fixture in the assembly.</remarks>
[SetUpFixture]
internal class JsonMetadataCoverage() : JsonMetadataCoverageBase(TestOnly)
{
    /// <summary>Values the tests serialize to compare with the module's results; the node writes results through the response writer.</summary>
    private static readonly Dictionary<Type, string> TestOnly = new()
    {
        [typeof(ParityLikeTxTrace[])] = "test size reference",
        [typeof(ResultWrapper<ParityTxTraceFromReplay>)] = "test expected value",
        [typeof(ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>)] = "test expected value",
        [typeof(ResultWrapper<IEnumerable<ParityTxTraceFromStore>>)] = "test expected value",
    };
}
