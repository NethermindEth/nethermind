// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using CkzgLib;
using Nethermind.Crypto;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Core.Test.Crypto;

public class KzgPolynomialCommitmentsTests
{
    private delegate bool VerifyProof(ReadOnlySpan<byte> commitment, ReadOnlySpan<byte> z, ReadOnlySpan<byte> y, ReadOnlySpan<byte> proof);

    [Test]
    public void Setup_waits_for_the_in_flight_load() => WithFreshSetupState(kzg =>
    {
        Task load = StartLoad(kzg, setupFilePath: null);
        nint setup = ReadSetup(kzg);

        Assert.That(setup, Is.Not.EqualTo(nint.Zero));
        Assert.That(load.IsCompletedSuccessfully, Is.True);
    });

    [Test]
    public void Failed_load_is_not_reported_as_an_invalid_proof() => WithFreshSetupState(kzg =>
    {
        // A missing file makes the loader throw ArgumentException, the type proof verification maps to false.
        Assert.That(() => StartLoad(kzg, "missing-kzg-trusted-setup.txt").GetAwaiter().GetResult(), Throws.ArgumentException);

        VerifyProof verify = kzg.GetMethod(nameof(KzgPolynomialCommitments.VerifyProof))!.CreateDelegate<VerifyProof>();

        Assert.That(() => verify(new byte[Ckzg.BytesPerCommitment], new byte[Ckzg.BytesPerFieldElement],
                new byte[Ckzg.BytesPerFieldElement], new byte[Ckzg.BytesPerProof]),
            Throws.InvalidOperationException.With.InnerException.TypeOf<ArgumentException>());
    });

    /// <remarks>
    /// A fresh load context gives the static setup state its own copy, untouched by whatever earlier tests
    /// in this process initialized in the shared one.
    /// </remarks>
    private static void WithFreshSetupState(Action<Type> test)
    {
        AssemblyLoadContext context = new(nameof(KzgPolynomialCommitmentsTests), isCollectible: true);
        try
        {
            test(context.LoadFromAssemblyPath(typeof(KzgPolynomialCommitments).Assembly.Location)
                .GetType(typeof(KzgPolynomialCommitments).FullName!, throwOnError: true)!);
        }
        finally
        {
            context.Unload();
        }
    }

    private static Task StartLoad(Type kzg, string? setupFilePath) =>
        (Task)kzg.GetMethod(nameof(KzgPolynomialCommitments.InitializeAsync))!.Invoke(null, [default(ILogger), setupFilePath])!;

    private static nint ReadSetup(Type kzg) =>
        (nint)kzg.GetProperty(nameof(KzgPolynomialCommitments.CkzgSetup), BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
}
