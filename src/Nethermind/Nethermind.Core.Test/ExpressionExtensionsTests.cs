// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq.Expressions;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class ExpressionExtensionsTests
{
    private class Dummy
    {
        public int ValueProperty { get; set; }
        public int ValueField = 0;
    }

    [Test]
    public void GetSetter_should_set_property_value()
    {
        Expression<Func<Dummy, int>> expr = x => x.ValueProperty;
        Action<Dummy, int> setter = expr.GetSetter();
        Dummy d = new Dummy();

        setter(d, 42);

        Assert.That(d.ValueProperty, Is.EqualTo(42));
    }

    [Test]
    public void GetSetter_should_set_field_value()
    {
        Expression<Func<Dummy, int>> expr = x => x.ValueField;
        Action<Dummy, int> setter = expr.GetSetter();
        Dummy d = new Dummy();

        setter(d, 7);

        Assert.That(d.ValueField, Is.EqualTo(7));
    }
}
