// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using Nethermind.Crypto;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Core.Test.Crypto;

public class KzgPolynomialCommitmentsTests
{
    [Test]
    public void Setup_waits_for_the_in_flight_load()
    {
        // A fresh load context gives the static setup state its own copy, so the load is still in flight
        // however many earlier tests in this process initialized the shared one.
        AssemblyLoadContext context = new(nameof(Setup_waits_for_the_in_flight_load), isCollectible: true);
        try
        {
            Type kzg = context.LoadFromAssemblyPath(typeof(KzgPolynomialCommitments).Assembly.Location)
                .GetType(typeof(KzgPolynomialCommitments).FullName!, throwOnError: true)!;

            Task load = (Task)kzg.GetMethod(nameof(KzgPolynomialCommitments.InitializeAsync))!
                .Invoke(null, [default(ILogger), null])!;
            nint setup = (nint)kzg.GetProperty(nameof(KzgPolynomialCommitments.CkzgSetup), BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null)!;

            Assert.That(setup, Is.Not.EqualTo(nint.Zero));
            Assert.That(load.IsCompletedSuccessfully, Is.True);
        }
        finally
        {
            context.Unload();
        }
    }
}
