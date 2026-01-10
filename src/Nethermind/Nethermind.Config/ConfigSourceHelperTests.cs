// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using NUnit.Framework;

namespace Nethermind.Config.Test;

public class ConfigSourceHelperTests
{
    [Test]
    public void GetDefault_returns_null_for_ValueTuple_types()
    {
        // Test various ValueTuple types
        Assert.That(ConfigSourceHelper.GetDefault(typeof((int, string))), Is.Null, "ValueTuple<int, string> should return null");
        Assert.That(ConfigSourceHelper.GetDefault(typeof((int, int, int))), Is.Null, "ValueTuple<int, int, int> should return null");
        Assert.That(ConfigSourceHelper.GetDefault(typeof((string, bool, int, long))), Is.Null, "ValueTuple<string, bool, int, long> should return null");
    }

    [Test]
    public void GetDefault_returns_null_for_reference_types()
    {
        Assert.That(ConfigSourceHelper.GetDefault(typeof(string)), Is.Null, "string should return null");
        Assert.That(ConfigSourceHelper.GetDefault(typeof(object)), Is.Null, "object should return null");
    }

    [Test]
    public void GetDefault_returns_default_instance_for_value_types()
    {
        Assert.That(ConfigSourceHelper.GetDefault(typeof(int)), Is.EqualTo(0), "int should return 0");
        Assert.That(ConfigSourceHelper.GetDefault(typeof(bool)), Is.EqualTo(false), "bool should return false");
        Assert.That(ConfigSourceHelper.GetDefault(typeof(byte)), Is.EqualTo((byte)0), "byte should return 0");
    }

    [Test]
    public void GetDefault_returns_null_for_nullable_value_types()
    {
        Assert.That(ConfigSourceHelper.GetDefault(typeof(int?)), Is.Null, "int? should return null");
        Assert.That(ConfigSourceHelper.GetDefault(typeof(bool?)), Is.Null, "bool? should return null");
    }
}

