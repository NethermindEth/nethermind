// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
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
    [method: JsonConstructor]
    public record TestCase(ReadOnlyMemory<byte> Input, byte[]? Expected, string Name, ulong? Gas, string? ExpectedError)
    {
        public IReleaseSpec Spec { get; internal set; } = DefaultSpec;

        public TestCase(string input, string output, bool status, IReleaseSpec? spec = null) : this(
            Convert.FromHexString(input), Convert.FromHexString(output),
            Name: input, Gas: null, ExpectedError: status ? null : "<error>"
        ) => Spec = spec ?? DefaultSpec;

        public TestCase(byte[] Input, byte[]? Expected, bool status, IReleaseSpec? spec = null) : this(
            Input, Expected,
            Name: Convert.ToHexString(Input), Gas: null, ExpectedError: status ? null : "<error>"
        ) => Spec = spec ?? DefaultSpec;
    }
    private const string TestFilesDirectory = "PrecompileVectors";

    private static readonly IReleaseSpec DefaultSpec = Prague.Instance;
    protected static readonly TPrecompile Instance = TPrecompile.Instance;
    protected static readonly IEqualityComparer<Result<byte[]>> ResultComparer = new ResultEqComparer();

    private static IEnumerable<TestCaseData> TestSource()
    {
        EthereumJsonSerializer serializer = new();

        foreach ((string file, IReleaseSpec spec) in Enumerable.Union(
                     TTests.TestFiles().Select(static f => (f, (IReleaseSpec)null)),
                     TTests.TestFilesWithSpec()
                 ))
        {
            string path = Path.Combine(TestFilesDirectory, file);
            string json = File.ReadAllText(path);
            foreach (TestCase test in serializer.Deserialize<TestCase[]>(json))
            {
                test.Spec = spec ?? DefaultSpec;
                yield return new(test) { TestName = ComposeName(test.Name, spec) };
            }
        }
    }

    [TestCaseSource(nameof(TestSource))]
    public void TestVectors(TestCase testCase)
    {
        if (this is not TTests)
            throw new InvalidOperationException($"Misconfigured tests! Type {GetType()} must be {typeof(TTests)}");

        RunTest(testCase);
    }

    protected void RunTest(TestCase testCase)
    {
        RunTestCore(testCase);

        ReadOnlyMemory<byte> normalized = Instance.NormalizeInput(testCase.Input);
        if (!normalized.Span.SequenceEqual(testCase.Input.Span))
            RunTestCore(testCase with { Input = normalized }, "normalized input should produce same output");
    }

    protected void RunTest(string input, string output, bool status, IReleaseSpec? spec = null) =>
        RunTest(new TestCase(Convert.FromHexString(input), Convert.FromHexString(output), status, spec));

    private static void RunTestCore(TestCase testCase, string? reason = null)
    {
        ulong gas = Instance.BaseGasCost(testCase.Spec) + Instance.DataGasCost(testCase.Input, testCase.Spec);

        Result<byte[]> result = Instance.Run(testCase.Input, testCase.Spec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsSuccess, Is.EqualTo(testCase.ExpectedError is null), reason);
            Assert.That(result.Data, Is.EqualTo(testCase.Expected ?? []), reason);

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

    private static string ComposeName(string name, IReleaseSpec? spec)
    {
        name = name.Replace('(', '[')
            .Replace(')', ']')
            .Replace("!=", "_not_eq_")
            .Replace("=", "_eq_")
            .Replace(" ", string.Empty);

        return spec is null ? name : $"{name} @ {spec.Name}";
    }

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
