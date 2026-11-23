// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Crypto;
using Nethermind.Evm.GasPolicy;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Which precompile exceptions say that a native library cannot be used: the VM ends the process on those instead of
/// failing the call.
/// </summary>
public class NativeLoadFailureTests
{
    [TestCaseSource(nameof(Chains))]
    public void Finds_the_load_failure_at_any_depth(Exception? chain, Exception? expected) =>
        Assert.That(VirtualMachineStatics.FindNativeLoadFailure(chain), Is.SameAs(expected));

    private static IEnumerable<TestCaseData> Chains()
    {
        yield return new TestCaseData(null, null).SetArgDisplayNames("null");
        yield return new TestCaseData(new InvalidOperationException("malformed input"), null).SetArgDisplayNames("plain");
        yield return new TestCaseData(Wrap(Wrap(new InvalidOperationException("malformed input"))), null).SetArgDisplayNames("plain, three levels");

        foreach (Func<Exception> create in (Func<Exception>[])[
            () => new DllNotFoundException("missing library"),
            () => new BadImageFormatException("wrong format"),
            () => new EntryPointNotFoundException("missing export")])
        {
            Exception direct = create();
            Exception nested = create();
            string name = direct.GetType().Name;
            yield return new TestCaseData(direct, direct).SetArgDisplayNames(name, "direct");
            yield return new TestCaseData(Wrap(Wrap(nested)), nested).SetArgDisplayNames(name, "two levels down");
        }

        // From the outside in: a load failure caused by another is the one returned.
        Exception outer = new DllNotFoundException("missing library", new BadImageFormatException("wrong format"));
        yield return new TestCaseData(outer, outer).SetArgDisplayNames("outermost first");
    }

    [TestCaseSource(nameof(FatalChains))]
    public void Fatal_load_failure_keeps_the_old_rule_for_node_faults_only(Exception chain, Exception? expected) =>
        Assert.That(Classifier.GetFatal(chain), Is.SameAs(expected));

    /// <remarks>A node fault exits only on a missing library directly in it or one level down, as before the rule was
    /// shared; deeper, or another load failure, it propagates. Anything else follows the shared rule.</remarks>
    private static IEnumerable<TestCaseData> FatalChains()
    {
        DllNotFoundException missingLibrary = new("missing library");
        yield return new TestCaseData(new KzgSetupUnavailableException("KZG trusted setup failed to load", missingLibrary), missingLibrary)
            .SetArgDisplayNames("KZG setup, missing library one level down");
        yield return new TestCaseData(new KzgSetupUnavailableException("KZG trusted setup failed to load", Wrap(new DllNotFoundException("missing library"))), null)
            .SetArgDisplayNames("KZG setup, missing library two levels down");
        yield return new TestCaseData(new KzgSetupUnavailableException("KZG trusted setup failed to load", new BadImageFormatException("wrong format")), null)
            .SetArgDisplayNames("KZG setup, wrong format");
        yield return new TestCaseData(new OutOfMemoryException("out of memory", new BadImageFormatException("wrong format")), null)
            .SetArgDisplayNames("out of memory, wrong format one level down");
        yield return new TestCaseData(new BadImageFormatException("wrong format", new OutOfMemoryException()), null)
            .SetArgDisplayNames("wrong format, out of memory one level down");

        EntryPointNotFoundException missingExport = new("missing export");
        yield return new TestCaseData(Wrap(Wrap(missingExport)), missingExport).SetArgDisplayNames("not a node fault, missing export two levels down");
    }

    private static Exception Wrap(Exception inner) => new TypeInitializationException("Precompile", inner);

    /// <summary>Reaches the classifier the VM's precompile dispatch uses; never constructed.</summary>
    private sealed class Classifier() : VirtualMachine<EthereumGasPolicy>(null, null, null, null)
    {
        public static Exception? GetFatal(Exception exception) => GetFatalLoadFailure(exception);
    }
}
