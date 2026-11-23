// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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

    private static Exception Wrap(Exception inner) => new TypeInitializationException("Precompile", inner);
}
