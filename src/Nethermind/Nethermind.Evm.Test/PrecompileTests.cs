// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Evm.Precompiles;
using Nethermind.Serialization.Json;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public abstract class PrecompileTests<T> where T : PrecompileTests<T>, IPrecompileTests
{
    public record TestCase(byte[] Input, byte[]? Expected, string Name, long? Gas, string? ExpectedError);
    private const string TestFilesDirectory = "PrecompileVectors";

    private static IEnumerable<TestCaseData> TestSource()
    {
        EthereumJsonSerializer serializer = new EthereumJsonSerializer();
        foreach (string file in T.TestFiles())
        {
            string path = Path.Combine(TestFilesDirectory, file);
            string json = File.ReadAllText(path);
            foreach (TestCase test in serializer.Deserialize<TestCase[]>(json))
            {
                yield return new TestCaseData(test) { TestName = EnsureSafeName(test.Name) };
            }
        }
    }

    [TestCaseSource(nameof(TestSource))]
    public void TestVectors(TestCase testCase)
    {
        if (this is not T) throw new InvalidOperationException($"Misconfigured tests! Type {GetType()} must be {typeof(T)}");

        IPrecompile precompile = T.Precompile();
        long gas = precompile.BaseGasCost(Prague.Instance) + precompile.DataGasCost(testCase.Input, Prague.Instance);
        (byte[] output, bool success) = precompile.Run(testCase.Input, Prague.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(success, Is.EqualTo(testCase.ExpectedError is null));

            if (testCase.Expected is not null)
            {
                Assert.That(output, Is.EquivalentTo(testCase.Expected));
            }

            if (testCase.Gas is not null)
            {
                Assert.That(gas, Is.EqualTo(testCase.Gas));
            }
        }
    }

    private static string EnsureSafeName(string name) =>
        name.Replace('(', '[')
            .Replace(')', ']')
            .Replace("!=", "_not_eq_")
            .Replace("=", "_eq_")
            .Replace(" ", string.Empty);
}
