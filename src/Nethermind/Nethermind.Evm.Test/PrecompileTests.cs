// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core;
using Nethermind.Evm.Precompiles;
using Nethermind.Serialization.Json;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public abstract class PrecompileTests<TPrecompile, TTests> : IPrecompileTests
    where TPrecompile : IPrecompile<TPrecompile>
    where TTests : PrecompileTests<TPrecompile, TTests>, IPrecompileTests
{
    public record TestCase(byte[] Input, byte[]? Expected, string Name, long? Gas, string? ExpectedError)
    {
        public TestCase(string input, string output, bool status) : this(
            Convert.FromHexString(input), Convert.FromHexString(output),
            Name: input, Gas: null, ExpectedError: status ? null : "<error>"
        )
        { }
    }
    private const string TestFilesDirectory = "PrecompileVectors";

    protected static readonly TPrecompile Instance = TPrecompile.Instance;
    protected static readonly IEqualityComparer<Result<byte[]>> ResultComparer = new ResultEqComparer();

    private static IEnumerable<TestCaseData> TestSource()
    {
        EthereumJsonSerializer serializer = new();
        foreach (string file in TTests.TestFiles())
        {
            string path = Path.Combine(TestFilesDirectory, file);
            string json = File.ReadAllText(path);
            foreach (TestCase test in serializer.Deserialize<TestCase[]>(json))
            {
                yield return new(test) { TestName = EnsureSafeName(test.Name) };
            }
        }
    }

    [TestCaseSource(nameof(TestSource))]
    public void TestVectors(TestCase testCase)
    {
        if (this is not TTests) throw new InvalidOperationException($"Misconfigured tests! Type {GetType()} must be {typeof(TTests)}");

        IPrecompile precompile = Instance;
        long gas = precompile.BaseGasCost(Prague.Instance) + precompile.DataGasCost(testCase.Input, Prague.Instance);
        Result<byte[]> result = precompile.Run(testCase.Input, Prague.Instance);
        (byte[]? output, bool success) = result;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(success, Is.EqualTo(testCase.ExpectedError is null));
            Assert.That(output, Is.EquivalentTo(testCase.Expected ?? []));

            if (testCase.Gas is not null)
            {
                Assert.That(gas, Is.EqualTo(testCase.Gas));
            }
        }
    }

    protected void RunTest(string input, string output, bool status)
    {
        byte[] inputData = Convert.FromHexString(input);
        (byte[] outputData, bool outcome) = Instance.Run(inputData, Prague.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome, Is.EqualTo(status));
            Assert.That(outputData, Is.EqualTo(Convert.FromHexString(output)));
        }
    }

    private static string EnsureSafeName(string name) =>
        name.Replace('(', '[')
            .Replace(')', ']')
            .Replace("!=", "_not_eq_")
            .Replace("=", "_eq_")
            .Replace(" ", string.Empty);

    private class ResultEqComparer : IEqualityComparer<Result<byte[]>>
    {
        public bool Equals(Result<byte[]> x, Result<byte[]> y)
        {
            if (x.ResultType != y.ResultType) return false;
            if (x.Data is null && y.Data is null) return true;
            if (x.Data is null || y.Data is null) return false;
            return x.Data.AsSpan().SequenceEqual(y.Data);
        }

        public int GetHashCode(Result<byte[]> obj)
        {
            HashCode hash = new();
            hash.Add(obj.ResultType);
            if (obj.Data is not null)
                hash.AddBytes(obj.Data);
            return hash.ToHashCode();
        }
    }
}
