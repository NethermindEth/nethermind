// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Nethermind.Config.Test;

public class ConfigSourceHelperTests
{
    public static IEnumerable<TestCaseData> GetDefaultTestCases()
    {
        // ValueTuple types should return default instances (not null, as ValueTuple is a value type)
        yield return new TestCaseData(typeof((int, string)), (0, (string?)null)).SetName("GetDefault_returns_default_for_ValueTuple_2_elements");
        yield return new TestCaseData(typeof((int, int, int)), (0, 0, 0)).SetName("GetDefault_returns_default_for_ValueTuple_3_elements");
        yield return new TestCaseData(typeof((string, bool, int, long)), ((string?)null, false, 0, 0L)).SetName("GetDefault_returns_default_for_ValueTuple_4_elements");

        // Reference types should return null
        yield return new TestCaseData(typeof(string), null).SetName("GetDefault_returns_null_for_string");
        yield return new TestCaseData(typeof(object), null).SetName("GetDefault_returns_null_for_object");

        // Value types should return default instances
        yield return new TestCaseData(typeof(int), 0).SetName("GetDefault_returns_0_for_int");
        yield return new TestCaseData(typeof(bool), false).SetName("GetDefault_returns_false_for_bool");
        yield return new TestCaseData(typeof(byte), (byte)0).SetName("GetDefault_returns_0_for_byte");

        // Nullable value types should return null
        yield return new TestCaseData(typeof(int?), null).SetName("GetDefault_returns_null_for_int_nullable");
        yield return new TestCaseData(typeof(bool?), null).SetName("GetDefault_returns_null_for_bool_nullable");
    }

    [TestCaseSource(nameof(GetDefaultTestCases))]
    public void GetDefault_returns_expected_value(Type type, object? expected)
    {
        object? result = ConfigSourceHelper.GetDefault(type);
        Assert.That(result, Is.EqualTo(expected));
    }
}

