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
    public void Setup_read_before_initialization_throws() => WithFreshSetupState(kzg =>
        Assert.That(() => ReadSetup(kzg), Throws.InstanceOf<TargetInvocationException>()
            .With.InnerException.Matches<Exception>(IsSetupUnavailable)));

    [Test]
    public void Failed_load_is_not_reported_as_an_invalid_proof() => WithFreshSetupState(kzg =>
    {
        // A missing file makes the loader throw ArgumentException, the type proof verification maps to false.
        Assert.That(() => StartLoad(kzg, "missing-kzg-trusted-setup.txt").GetAwaiter().GetResult(), Throws.ArgumentException);

        VerifyProof verify = kzg.GetMethod(nameof(KzgPolynomialCommitments.VerifyProof))!.CreateDelegate<VerifyProof>();

        Assert.That(() => verify(new byte[Ckzg.BytesPerCommitment], new byte[Ckzg.BytesPerFieldElement],
                new byte[Ckzg.BytesPerFieldElement], new byte[Ckzg.BytesPerProof]),
            Throws.Exception.Matches<Exception>(IsSetupUnavailable).With.InnerException.TypeOf<ArgumentException>());
    });

    /// <remarks>
    /// A fresh load context gives the static setup state its own copy, untouched by whatever earlier tests
    /// in this process initialized in the shared one. The native setup it loads is freed afterwards, since
    /// unloading the context does not release it.
    /// </remarks>
    private static void WithFreshSetupState(Action<Type> test)
    {
        AssemblyLoadContext context = new(nameof(KzgPolynomialCommitmentsTests), isCollectible: true);
        Type kzg = context.LoadFromAssemblyPath(typeof(KzgPolynomialCommitments).Assembly.Location)
            .GetType(typeof(KzgPolynomialCommitments).FullName!, throwOnError: true)!;
        try
        {
            test(kzg);
        }
        finally
        {
            FieldInfo setupField = kzg.GetField("_ckzgSetup", BindingFlags.NonPublic | BindingFlags.Static)!;
            nint setup = (nint)setupField.GetValue(null)!;
            if (setup != nint.Zero)
            {
                Ckzg.FreeTrustedSetup(setup);
                setupField.SetValue(null, nint.Zero);
            }

            context.Unload();
        }
    }

    // The isolated context has its own copy of the exception type, so it is matched by name.
    private static bool IsSetupUnavailable(Exception exception) =>
        exception.GetType().FullName == typeof(KzgSetupUnavailableException).FullName;

    private static Task StartLoad(Type kzg, string? setupFilePath) =>
        (Task)kzg.GetMethod(nameof(KzgPolynomialCommitments.InitializeAsync))!.Invoke(null, [default(ILogger), setupFilePath])!;

    private static nint ReadSetup(Type kzg) =>
        (nint)kzg.GetProperty(nameof(KzgPolynomialCommitments.CkzgSetup), BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
}
