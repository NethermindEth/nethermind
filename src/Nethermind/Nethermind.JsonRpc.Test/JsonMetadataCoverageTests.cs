// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization.Metadata;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test;

public class JsonMetadataCoverageTests
{
    private static IEnumerable<TestCaseData> UnnameableCollections()
    {
        yield return new TestCaseData(new ParityLikeTxTrace[1].Select(static t => new ParityTxTraceFromReplay(t)).GetType())
            .SetName("FrameworkIterator");
        yield return new TestCaseData(typeof(ParityTxTraceFromReplay).Assembly.GetType("<>z__ReadOnlyArray`1")!.MakeGenericType(typeof(ParityTxTraceFromReplay)))
            .SetName("CompilerGeneratedCollection");
    }

    // Asked for outside any dispatch frame, as when the JIT has inlined those frames into the caller.
    [TestCaseSource(nameof(UnnameableCollections))]
    public void Coverage_UnnameableCollectionWithCoveredInterfaceAndNoDispatchFrame_IsNotReported(Type type)
    {
        Assert.That(type.IsVisible, Is.False, "precondition: only a non-public collection may fall back to its interface");

        Assert.That(() => EthereumJsonSerializer.JsonOptions.TryGetTypeInfo(type, out JsonTypeInfo? _), Throws.Nothing,
            "the serializer writes such a value through its covered interface, so the coverage check must not report it");
    }
}
