// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Specs;
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

    private static readonly IReleaseSpec DefaultSpec = Prague.Instance;
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

        byte[] input = testCase.Input;
        RunTest(input, testCase);

        ReadOnlyMemory<byte> normalized = Instance.NormalizeInput(input);
        if (!normalized.Span.SequenceEqual(input))
            RunTest(normalized, testCase, "normalized input should produce same output");
    }

    protected void RunTest(string input, string output, bool status)
    {
        byte[] inputData = Convert.FromHexString(input);
        byte[] outputData = Convert.FromHexString(output);
        RunTest(inputData, outputData, status);

        ReadOnlyMemory<byte> normalized = Instance.NormalizeInput(inputData);
        if (!normalized.Span.SequenceEqual(inputData))
            RunTest(normalized, outputData, status, "normalized input should produce same output");
    }

    private static void RunTest(ReadOnlyMemory<byte> input, byte[] output, bool status, string? reason = null)
    {
        Result<byte[]> result = Instance.Run(input, DefaultSpec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsSuccess, Is.EqualTo(status), reason);
            Assert.That(result.Data, Is.EqualTo(output), reason);
        }
    }

    private static void RunTest(ReadOnlyMemory<byte> input, TestCase testCase, string? reason = null)
    {
        long gas = Instance.BaseGasCost(DefaultSpec) + Instance.DataGasCost(input, DefaultSpec);

        Result<byte[]> result = Instance.Run(input, DefaultSpec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsSuccess, Is.EqualTo(testCase.ExpectedError is null), reason);
            Assert.That(result.Data, Is.EquivalentTo(testCase.Expected ?? []), reason);

            if (testCase.Gas is not null)
            {
                Assert.That(gas, Is.EqualTo(testCase.Gas), reason);
            }
        }

    }

    protected static void RunEffectiveInputTest(string input, string? trailing = null, IReleaseSpec? spec = null) =>
        RunEffectiveInputTest(Instance, input, trailing, spec);

    protected static void RunEffectiveInputTest(IPrecompile precompile, string input, string? trailing = null, IReleaseSpec? spec = null)
    {
        spec ??= DefaultSpec;
        ReadOnlyMemory<byte> fullInput = Convert.FromHexString(input + trailing);
        ReadOnlyMemory<byte> effInput = precompile.NormalizeInput(fullInput);

        Assert.That(effInput.Span.SequenceEqual(fullInput.Span), Is.False);
        Assert.That(
            precompile.Run(effInput, spec),
            Is.EqualTo(precompile.Run(fullInput, spec)).Using(ResultComparer)
        );
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
            if (x.Error != y.Error) return false;
            if (x.Data is null && y.Data is null) return true;
            if (x.Data is null || y.Data is null) return false;
            return x.Data.AsSpan().SequenceEqual(y.Data);
        }

        public int GetHashCode(Result<byte[]> obj)
        {
            HashCode hash = new();
            hash.Add(obj.ResultType);
            hash.Add(obj.Error);
            if (obj.Data is not null)
                hash.AddBytes(obj.Data);
            return hash.ToHashCode();
        }
    }
}
