// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

public class JsonMetadataCoverageBaseTests
{
    private static IEnumerable<TestCaseData> Types()
    {
        yield return new TestCaseData(typeof(List<Hidden>)).Returns(false).SetName("PublicListOfInternalType");
        yield return new TestCaseData(typeof(Dictionary<string, Hidden>)).Returns(false).SetName("PublicDictionaryOfInternalType");
        yield return new TestCaseData(typeof(Hidden)).Returns(false).SetName("InternalNethermindType");
        yield return new TestCaseData(typeof(Hidden[])).Returns(false).SetName("ArrayOfInternalNethermindType");
        yield return new TestCaseData(new Hidden[1].Select(static h => h).GetType()).Returns(true).SetName("FrameworkIterator");
        yield return new TestCaseData(Iterate().GetType()).Returns(true).SetName("CompilerGeneratedIterator");
    }

    [TestCaseSource(nameof(Types))]
    public bool IsUnnameable_ByTypeDefinition_ExemptsOnlyTypesNoCodeCanDeclare(Type type) =>
        JsonMetadataCoverageBase.IsUnnameable(type);

    private static IEnumerable<Hidden> Iterate()
    {
        yield return new Hidden();
    }

    private sealed class Hidden;
}
