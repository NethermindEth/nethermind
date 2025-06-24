// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Reflection;
using Autofac.Core.Activators.Reflection;

namespace Nethermind.Core.Container;

public class NethermindConstructorFinder: IConstructorFinder
{
    private NethermindConstructorFinder()
    {
    }

    public static NethermindConstructorFinder Instance { get; } = new();


    private readonly DefaultConstructorFinder _baseFinder = new DefaultConstructorFinder();

    public ConstructorInfo[] FindConstructors(Type targetType)
    {
        ConstructorInfo[] constructors = _baseFinder.FindConstructors(targetType);

        ConstructorInfo[] explicitlySelectedConstructors = constructors
            .Where(c => c.GetCustomAttribute<UseConstructorForDependencyInjectionAttribute>() is not null)
            .ToArray();

        return explicitlySelectedConstructors.Length > 0 ? explicitlySelectedConstructors : constructors;
    }
}
