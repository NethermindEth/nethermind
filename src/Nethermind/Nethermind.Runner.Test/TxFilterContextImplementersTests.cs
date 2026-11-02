// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Merge.AuRa;
using Nethermind.Xdc;
using NUnit.Framework;

namespace Nethermind.Runner.Test;

/// <summary>
/// A class that implements a <see cref="TxFilterContext"/> member of <see cref="ITxFilter"/>,
/// <see cref="IMinGasPriceTxFilter"/> or <see cref="ITxFilterPipeline"/> itself must be sealed.
/// </summary>
/// <remarks>
/// A subclass that re-declares the interface and hides the parent-and-spec overload with <c>new</c> would get the
/// interface's parent-and-spec member mapped to its own method, while the context member still maps to the base class.
/// Block building calls the context member, so the subclass's policy would be skipped there. Sealing makes such a
/// subclass a compile error; a custom policy implements the interface itself or wraps the filter instead.
/// </remarks>
[Parallelizable(ParallelScope.All)]
public class TxFilterContextImplementersTests
{
    private static readonly Type[] Interfaces = [typeof(ITxFilter), typeof(IMinGasPriceTxFilter), typeof(ITxFilterPipeline)];

    // Implementers in every assembly that holds one, so a pass means the scan saw them.
    private static readonly string[] KnownImplementers =
    [
        typeof(BaseFeeTxFilter).FullName!,
        typeof(MinGasPriceTxFilter).FullName!,
        typeof(CompositeTxFilter).FullName!,
        typeof(TxFilterPipeline).FullName!,
        typeof(MinGasPriceContractTxFilter).FullName!,
        typeof(TxCertifierFilter).FullName!,
        typeof(AuRaMergeTxFilter).FullName!,
        $"{typeof(XdcPlugin).Namespace}.TxPool.XdcTxFilterPipeline",
    ];

    [Test]
    public void Classes_that_implement_a_context_member_themselves_are_sealed()
    {
        Dictionary<Type, List<string>> implementers = FindContextImplementers();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(implementers.Keys.Select(static t => t.FullName), Is.SupersetOf(KnownImplementers));
            foreach ((Type type, List<string> members) in implementers)
            {
                Assert.That(type.IsSealed, Is.True, $"{type.FullName} implements {string.Join(", ", members)} and is not sealed");
            }
        }
    }

    private static Dictionary<Type, List<string>> FindContextImplementers()
    {
        TypeDiscovery.Initialize();
        Dictionary<Type, List<string>> implementers = [];
        IEnumerable<Assembly> assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(static a => !a.IsDynamic && a.GetName().Name is { } name && name.StartsWith("Nethermind") && !name.Contains("Test") && !name.Contains("Benchmark"));

        foreach (Type type in assemblies.SelectMany(LoadableTypes))
        {
            if (!type.IsClass || type.ContainsGenericParameters) continue;

            foreach (Type @interface in Interfaces.Where(i => i.IsAssignableFrom(type)))
            {
                InterfaceMapping map = type.GetInterfaceMap(@interface);
                for (int i = 0; i < map.InterfaceMethods.Length; i++)
                {
                    MethodInfo target = map.TargetMethods[i];
                    if (TakesContext(map.InterfaceMethods[i]) && target.DeclaringType is { IsInterface: false } declaring)
                    {
                        if (!implementers.TryGetValue(declaring, out List<string> members)) implementers[declaring] = members = [];
                        members.Add($"{@interface.Name}.{map.InterfaceMethods[i].Name}");
                    }
                }
            }
        }

        return implementers;
    }

    private static bool TakesContext(MethodInfo method) =>
        method.GetParameters().Any(static p => (p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType) == typeof(TxFilterContext));

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.OfType<Type>();
        }
    }
}
