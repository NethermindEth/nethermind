// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Reflection;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;

namespace Nethermind.Specs.Forks;

public static class Fork
{
    private static IReleaseSpec? _latest;

    public static IReleaseSpec GetLatest() => _latest ??= GetLatestCore();

    private static IReleaseSpec GetLatestCore()
    {
        Type releaseSpec = typeof(INamedReleaseSpec);
        Type type = releaseSpec.Assembly.GetTypes()
            .Where(t => releaseSpec.IsAssignableFrom(t))
            .Where(t => !t.Name.EndsWith("Gnosis", StringComparison.InvariantCultureIgnoreCase))
            .OrderByDescending(GetInheritanceDepth)
            .First(t => GetInstance(t).Released);

        return GetInstance(type);

        int GetInheritanceDepth(Type t)
        {
            int depth = 0;
            Type current = t;
            while (current.BaseType != null)
            {
                depth++;
                current = current.BaseType;
            }
            return depth;
        }

        INamedReleaseSpec GetInstance(Type t)
        {
            PropertyInfo instanceProp = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!;
            var instance = (INamedReleaseSpec)instanceProp.GetValue<IReleaseSpec>();
            return instance;
        }
    }
}
