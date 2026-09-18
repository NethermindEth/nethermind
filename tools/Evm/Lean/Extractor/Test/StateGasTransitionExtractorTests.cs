// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.Extractor.Test;

[TestFixture]
public class StateGasTransitionExtractorTests
{
    private const string SourceRelativePath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionKernel.cs";

    private static readonly string[] ExpectedRoots =
    [
        "AddStateGasRefundToReservoir",
        "DiscardStateGas",
        "Refund",
        "RefundStateGas",
        "RemoveStateGasRefundFromReservoir",
        "RepayStateGasSpill",
        "RestoreChildStateGas",
        "RestoreChildStateGasOnHalt",
        "RevertRefundToHalt",
    ];

    [Test]
    public void Extracts_every_transition_root_deterministically()
    {
        using Fixture fixture = new(ReadProductionSource());

        ExtractionResult first = StateGasChargeExtractor.ExtractTransitions(fixture.Root, fixture.Output);
        byte[] firstIr = File.ReadAllBytes(first.IrPath);
        byte[] firstManifest = File.ReadAllBytes(first.ManifestPath);
        byte[] firstLean = File.ReadAllBytes(first.LeanPath);
        ExtractionResult second = StateGasChargeExtractor.ExtractTransitions(fixture.Root, fixture.Output);
        string generatedLean = File.ReadAllText(first.LeanPath);

        using JsonDocument manifest = JsonDocument.Parse(firstManifest);
        JsonElement root = manifest.RootElement;
        string[] signatures = root.GetProperty("rootSignatures")
            .EnumerateArray()
            .Select(static signature => signature.GetString()!)
            .ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(second.IrPath), Is.EqualTo(firstIr));
            Assert.That(File.ReadAllBytes(second.ManifestPath), Is.EqualTo(firstManifest));
            Assert.That(File.ReadAllBytes(second.LeanPath), Is.EqualTo(firstLean));
            Assert.That(first.MethodCount, Is.EqualTo(12));
            Assert.That(signatures, Has.Length.EqualTo(ExpectedRoots.Length));
            Assert.That(
                root.GetProperty("leanArtifact").GetProperty("sha256").GetString(),
                Is.EqualTo(Convert.ToHexString(SHA256.HashData(firstLean)).ToLowerInvariant()));
        }

        foreach (string rootName in ExpectedRoots)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(signatures, Has.Some.Contains(rootName));
                Assert.That(generatedLean, Does.Contain($"def {LowerFirst(rootName)}\n"));
            }
        }

        int positivePart = generatedLean.IndexOf("private def positivePartNormalized", StringComparison.Ordinal);
        int getSpill = generatedLean.IndexOf("private def getUnrefundedStateGasSpillNormalized", StringComparison.Ordinal);
        int addRefund = generatedLean.IndexOf("private def addStateGasRefundToReservoirNormalized", StringComparison.Ordinal);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(positivePart, Is.GreaterThanOrEqualTo(0));
            Assert.That(getSpill, Is.GreaterThan(positivePart));
            Assert.That(addRefund, Is.GreaterThan(getSpill));
        }
    }

    [TestCase(
        "repayment <= 0",
        "repayment < 0",
        TestName = "Repayment_boundary_mutation_changes_Lean")]
    [TestCase(
        "unchecked(stateGasUsed - appliedRefund)",
        "unchecked(stateGasUsed + appliedRefund)",
        TestName = "Used_update_mutation_changes_Lean")]
    [TestCase(
        "unchecked(stateGasSpillRefunded + toGasLeft)",
        "unchecked(stateGasSpillRefunded - toGasLeft)",
        TestName = "Spill_refund_update_mutation_changes_Lean")]
    [TestCase(
        "unchecked(value + childValue)",
        "unchecked(value + unchecked(childValue + 1))",
        TestName = "Wrapped_UInt64_update_mutation_changes_Lean")]
    [TestCase(
        "stateReservoir <= 0 ? 0 : stateReservoir >= amount ? amount : stateReservoir",
        "stateReservoir < 0 ? 0 : stateReservoir >= amount ? amount : stateReservoir",
        TestName = "Reservoir_clamp_mutation_changes_Lean")]
    public void Semantic_mutations_change_generated_Lean(string original, string replacement)
    {
        string source = ReadProductionSource();
        Assert.That(source, Does.Contain(original));
        using Fixture baselineFixture = new(source);
        using Fixture mutatedFixture = new(source.Replace(original, replacement, StringComparison.Ordinal));

        ExtractionResult baseline = StateGasChargeExtractor.ExtractTransitions(
            baselineFixture.Root,
            baselineFixture.Output);
        ExtractionResult mutated = StateGasChargeExtractor.ExtractTransitions(
            mutatedFixture.Root,
            mutatedFixture.Output);

        Assert.That(
            SemanticBody(File.ReadAllText(mutated.LeanPath)),
            Is.Not.EqualTo(SemanticBody(File.ReadAllText(baseline.LeanPath))));
    }

    [Test]
    public void Rejects_checked_transition_arithmetic()
    {
        string source = ReadProductionSource().Replace(
            "unchecked(stateGasUsed - appliedRefund)",
            "checked(stateGasUsed - appliedRefund)",
            StringComparison.Ordinal);
        using Fixture fixture = new(source);

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractTransitions(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("Checked"));
    }

    [Test]
    public void Rejects_an_unlisted_transition_intrinsic()
    {
        string source = ReadProductionSource().Replace(
            "Math.Min(amount, refundableStateGas)",
            "Math.Max(amount, refundableStateGas)",
            StringComparison.Ordinal);
        using Fixture fixture = new(source);

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractTransitions(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("is not an allowed intrinsic"));
    }

    [Test]
    public void Rejects_result_fields_mapped_to_different_constructor_parameters()
    {
        string source = ReadProductionSource()
            .Replace(
                "public readonly long StateReservoir = stateReservoir;",
                "public readonly long StateReservoir = stateGasUsed;",
                StringComparison.Ordinal)
            .Replace(
                "public readonly long StateGasUsed = stateGasUsed;",
                "public readonly long StateGasUsed = stateReservoir;",
                StringComparison.Ordinal);
        using Fixture fixture = new(source);

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractTransitions(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("corresponding primary-constructor parameters"));
    }

    [Test]
    public void Rejects_transition_syntax_outside_the_subset()
    {
        string source = ReadProductionSource().Replace(
            "long repayment = Math.Min(stateReservoir, GetUnrefundedStateGasSpill(stateGasSpill, stateGasSpillRefunded));",
            "for (int i = 0; i < 1; i++) { value++; }\n        long repayment = Math.Min(stateReservoir, GetUnrefundedStateGasSpill(stateGasSpill, stateGasSpillRefunded));",
            StringComparison.Ordinal);
        using Fixture fixture = new(source);

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractTransitions(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("ForStatement"));
    }

    private static string ReadProductionSource()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, SourceRelativePath);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate the production transition kernel.", SourceRelativePath);
    }

    private static string SemanticBody(string generatedLean)
    {
        const string namespaceMarker = "namespace Eip803x.Generated.StateGasTransitionKernel";
        int start = generatedLean.IndexOf(namespaceMarker, StringComparison.Ordinal);
        return start < 0
            ? throw new AssertionException("Generated Lean namespace marker was not found.")
            : generatedLean[start..];
    }

    private static string LowerFirst(string value) => char.ToLowerInvariant(value[0]) + value[1..];

    private sealed class Fixture : IDisposable
    {
        public Fixture(string source)
        {
            Root = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "transition-extractor-fixtures",
                Guid.NewGuid().ToString("N"));
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
