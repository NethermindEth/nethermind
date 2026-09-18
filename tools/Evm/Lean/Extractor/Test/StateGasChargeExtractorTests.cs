// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.Extractor.Test;

[TestFixture]
public class StateGasChargeExtractorTests
{
    private const string SourceRelativePath = "src/Nethermind/Nethermind.Evm/GasPolicy/StateGasChargeKernel.cs";

    [Test]
    public void Extracts_bound_root_deterministically()
    {
        using Fixture fixture = new(ValidSource);

        ExtractionResult first = StateGasChargeExtractor.Extract(fixture.Root, fixture.Output);
        byte[] firstIr = File.ReadAllBytes(first.IrPath);
        byte[] firstManifest = File.ReadAllBytes(first.ManifestPath);
        byte[] firstLean = File.ReadAllBytes(first.LeanPath);
        ExtractionResult second = StateGasChargeExtractor.Extract(fixture.Root, fixture.Output);
        string generatedLean = File.ReadAllText(first.LeanPath);

        using JsonDocument manifest = JsonDocument.Parse(firstManifest);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(second.IrPath), Is.EqualTo(firstIr));
            Assert.That(File.ReadAllBytes(second.ManifestPath), Is.EqualTo(firstManifest));
            Assert.That(File.ReadAllBytes(second.LeanPath), Is.EqualTo(firstLean));
            Assert.That(first.MethodCount, Is.EqualTo(2));
            Assert.That(
                manifest.RootElement.GetProperty("root").GetString(),
                Is.EqualTo("Nethermind.Evm.GasPolicy.StateGasChargeKernel.TryCharge"));
            Assert.That(
                manifest.RootElement.GetProperty("methodSignature").GetString(),
                Does.Contain("TryCharge(ulong value, long stateReservoir"));
            Assert.That(
                manifest.RootElement.GetProperty("leanArtifact").GetProperty("sha256").GetString(),
                Is.EqualTo(Convert.ToHexString(SHA256.HashData(firstLean)).ToLowerInvariant()));
            Assert.That(generatedLean, Does.Contain("def wrapInt64"));
            Assert.That(generatedLean, Does.Contain("def subUInt64"));
            Assert.That(generatedLean, Does.Contain("private def tryChargeNormalized"));
        }
    }

    [Test]
    public void Rejects_a_different_type_with_similar_source_text()
    {
        using Fixture fixture = new(ValidSource.Replace("StateGasChargeKernel", "SimilarStateGasChargeKernel", StringComparison.Ordinal));

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.Extract(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("Required root type"));
    }

    [Test]
    public void Rejects_syntax_outside_the_subset()
    {
        using Fixture fixture = new(ValidSource.Replace(
            "if (stateReservoir >= stateGasCost)",
            "for (int i = 0; i < 1; i++) { value++; }\n        if (stateReservoir >= stateGasCost)",
            StringComparison.Ordinal));

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.Extract(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("ForStatement"));
    }

    [Test]
    public void Rejects_an_unlisted_external_call()
    {
        using Fixture fixture = new(ValidSource.Replace(
            "Math.Min(0, stateReservoir)",
            "Math.Max(0, stateReservoir)",
            StringComparison.Ordinal));

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.Extract(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("is not an allowed intrinsic"));
    }

    [Test]
    public void Rejects_a_mutable_field_read()
    {
        using Fixture fixture = new(ValidSource
            .Replace(
                "internal static class StateGasChargeKernel\n{",
                "internal static class StateGasChargeKernel\n{\n    private static ulong ExternalState = 1;",
                StringComparison.Ordinal)
            .Replace(
                "if (stateReservoir >= stateGasCost)",
                "if (ExternalState >= (ulong)stateGasCost)",
                StringComparison.Ordinal));

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.Extract(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("must contain only the two pinned method declarations"));
    }

    [TestCase(
        "stateReservoir >= stateGasCost",
        "stateReservoir > stateGasCost",
        TestName = "Reservoir_first_branch_mutation_changes_Lean")]
    [TestCase(
        "value < spillAmount",
        "value <= spillAmount",
        TestName = "Affordability_mutation_changes_Lean")]
    [TestCase(
        "unchecked(stateGasUsed + stateGasCost)",
        "unchecked(stateGasUsed - stateGasCost)",
        TestName = "Used_update_mutation_changes_Lean")]
    [TestCase(
        "unchecked(stateGasSpill + (long)spillAmount)",
        "unchecked(stateGasSpill - (long)spillAmount)",
        TestName = "Spill_update_mutation_changes_Lean")]
    public void Semantic_mutations_change_generated_Lean(string original, string replacement)
    {
        using Fixture baselineFixture = new(ValidSource);
        using Fixture mutatedFixture = new(ValidSource.Replace(original, replacement, StringComparison.Ordinal));

        ExtractionResult baseline = StateGasChargeExtractor.Extract(baselineFixture.Root, baselineFixture.Output);
        ExtractionResult mutated = StateGasChargeExtractor.Extract(mutatedFixture.Root, mutatedFixture.Output);

        Assert.That(
            SemanticBody(File.ReadAllText(mutated.LeanPath)),
            Is.Not.EqualTo(SemanticBody(File.ReadAllText(baseline.LeanPath))));
    }

    [Test]
    public void Rejects_checked_machine_arithmetic()
    {
        using Fixture fixture = new(ValidSource.Replace(
            "unchecked(stateGasUsed + stateGasCost)",
            "checked(stateGasUsed + stateGasCost)",
            StringComparison.Ordinal));

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.Extract(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("Checked"));
    }

    private static string SemanticBody(string generatedLean)
    {
        const string namespaceMarker = "namespace Eip803x.Generated.StateGasChargeKernel";
        int start = generatedLean.IndexOf(namespaceMarker, StringComparison.Ordinal);
        return start < 0
            ? throw new AssertionException("Generated Lean namespace marker was not found.")
            : generatedLean[start..];
    }

    private const string ValidSource =
        """
        using System;
        using System.Runtime.CompilerServices;

        namespace Nethermind.Evm.GasPolicy;

        internal enum StateGasChargeOutcome : byte
        {
            Success,
            OutOfGas,
        }

        internal readonly struct StateGasChargeResult(
            StateGasChargeOutcome outcome,
            ulong value,
            long stateReservoir,
            long stateGasUsed,
            long stateGasSpill,
            long stateGasSpillRefunded)
        {
            public readonly StateGasChargeOutcome Outcome = outcome;
            public readonly ulong Value = value;
            public readonly long StateReservoir = stateReservoir;
            public readonly long StateGasUsed = stateGasUsed;
            public readonly long StateGasSpill = stateGasSpill;
            public readonly long StateGasSpillRefunded = stateGasSpillRefunded;
        }

        internal static class StateGasChargeKernel
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static StateGasChargeResult TryCharge(
                ulong value,
                long stateReservoir,
                long stateGasUsed,
                long stateGasSpill,
                long stateGasSpillRefunded,
                long stateGasCost)
            {
                if (stateReservoir >= stateGasCost)
                {
                    return new StateGasChargeResult(
                        StateGasChargeOutcome.Success,
                        value,
                        unchecked(stateReservoir - stateGasCost),
                        unchecked(stateGasUsed + stateGasCost),
                        stateGasSpill,
                        stateGasSpillRefunded);
                }

                ulong spillAmount = CalculateSpill(stateReservoir, stateGasCost);
                if (value < spillAmount)
                {
                    return new StateGasChargeResult(
                        StateGasChargeOutcome.OutOfGas,
                        value,
                        stateReservoir,
                        stateGasUsed,
                        stateGasSpill,
                        stateGasSpillRefunded);
                }

                return new StateGasChargeResult(
                    StateGasChargeOutcome.Success,
                    value - spillAmount,
                    Math.Min(0, stateReservoir),
                    unchecked(stateGasUsed + stateGasCost),
                    unchecked(stateGasSpill + (long)spillAmount),
                    stateGasSpillRefunded);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static ulong CalculateSpill(long stateReservoir, long stateGasCost)
            {
                if (stateGasCost <= 0)
                {
                    return 0;
                }

                if (stateReservoir <= 0)
                {
                    return (ulong)stateGasCost;
                }

                return stateGasCost > stateReservoir ? (ulong)(stateGasCost - stateReservoir) : 0;
            }
        }
        """;

    private sealed class Fixture : IDisposable
    {
        public Fixture(string source)
        {
            Root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "extractor-fixtures", Guid.NewGuid().ToString("N"));
            string sourcePath = Path.Combine(Root, SourceRelativePath);
            Output = Path.Combine(Root, "generated");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllText(sourcePath, source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        public string Root { get; }

        public string Output { get; }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
