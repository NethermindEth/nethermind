// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Reflection;
using Autofac.Core.Activators.Reflection;

namespace Nethermind.Core.Container;

public class NethermindConstructorFinder : IConstructorFinder
{
    private IConstructorFinder _defaultConstructorFinder = new DefaultConstructorFinder();

    public ConstructorInfo[] FindConstructors(Type targetType)
    {
        return _defaultConstructorFinder.FindConstructors(targetType)
            .Where((c) => c.GetCustomAttributes<SkipConstructorAttribute>() is null)
            .ToArray();
    }
}

[AttributeUsage(AttributeTargets.Constructor)]
public class SkipConstructorAttribute : Attribute { }
