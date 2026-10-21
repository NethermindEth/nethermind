// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nethermind.State.Flat.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Persistence;

public class PrefetchingReaderContractTests
{
    // Open generic readers are listed closed over the readers the persistences build them with.
    private static IEnumerable<Type> Implementers() =>
        typeof(IPersistence).Assembly.GetTypes()
            .Where(static type => !type.IsInterface && !type.ContainsGenericParameters && typeof(IPersistence.IPrefetchingReader).IsAssignableFrom(type))
            .Append(typeof(BasePersistence.Reader<BasePersistence.ToHashedFlatReader<BaseFlatPersistence.Reader>, BaseTriePersistence.Reader>));

    /// <summary>
    /// A prefetching reader that leaves the plan or the support flag to the interface default answers for itself
    /// instead of the reader it wraps, which silently drops the prefetch decision of every block.
    /// </summary>
    [TestCaseSource(nameof(Implementers))]
    public void Implements_the_plan_and_the_support_flag_itself(Type type)
    {
        InterfaceMapping map = type.GetInterfaceMap(typeof(IPersistence.IPrefetchingReader));
        string[] defaulted = map.TargetMethods
            .Where(static method => method.DeclaringType == typeof(IPersistence.IPrefetchingReader))
            .Select(static method => method.Name)
            .ToArray();

        Assert.That(defaulted, Is.Empty);
    }

    [Test]
    public void Every_production_reader_is_checked() =>
        Assert.That(Implementers().Select(static type => type.Name), Is.SupersetOf(new[] { "CachingReader", "RefCountingPersistenceReader", "Reader`2" }));
}
