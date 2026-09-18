// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.Extractor.Test;

[TestFixture]
public class BlockGasInclusionExtractorTests
{
    private const string KernelSourceRelativePath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/Eip8037BlockGasInclusionCheck.cs";
    private const string Eip7825SourceRelativePath = "src/Nethermind/Nethermind.Core/Eip7825Constants.cs";
    private const string UInt64ExtensionsSourceRelativePath =
        "src/Nethermind/Nethermind.Core/Extensions/UInt64Extensions.cs";

    [Test]
    public void Extracts_block_inclusion_and_execution_gas_deterministically()
    {
        using Fixture fixture = new(ReadProductionSource(KernelSourceRelativePath),
            ReadProductionSource(Eip7825SourceRelativePath),
            ReadProductionSource(UInt64ExtensionsSourceRelativePath));

        ExtractionResult first = StateGasChargeExtractor.ExtractBlockGasInclusion(fixture.Root, fixture.Output);
        byte[] firstIr = File.ReadAllBytes(first.IrPath);
        byte[] firstManifest = File.ReadAllBytes(first.ManifestPath);
        byte[] firstLean = File.ReadAllBytes(first.LeanPath);
        ExtractionResult second = StateGasChargeExtractor.ExtractBlockGasInclusion(fixture.Root, fixture.Output);
        string generatedLean = File.ReadAllText(first.LeanPath);

        using JsonDocument manifest = JsonDocument.Parse(firstManifest);
        JsonElement root = manifest.RootElement;
        string[] signatures = root.GetProperty("rootSignatures")
            .EnumerateArray()
            .Select(static signature => signature.GetString()!)
            .ToArray();
        JsonElement[] supportingSources = root.GetProperty("supportingSources").EnumerateArray().ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(second.IrPath), Is.EqualTo(firstIr));
            Assert.That(File.ReadAllBytes(second.ManifestPath), Is.EqualTo(firstManifest));
            Assert.That(File.ReadAllBytes(second.LeanPath), Is.EqualTo(firstLean));
            Assert.That(first.MethodCount, Is.EqualTo(2));
            Assert.That(signatures, Has.Length.EqualTo(2));
            Assert.That(signatures, Has.Some.Contains("Eip8037BlockGasInclusionCheck.Validate(ulong blockGasLimit"));
            Assert.That(signatures, Has.Some.Contains("CalculateBlockExecutionGas(ulong preRefundGas"));
            Assert.That(supportingSources.Select(static source => source.GetProperty("path").GetString()), Is.EquivalentTo(
                new[] { Eip7825SourceRelativePath.Replace('\\', '/'), UInt64ExtensionsSourceRelativePath.Replace('\\', '/') }));
            Assert.That(
                root.GetProperty("leanArtifact").GetProperty("sha256").GetString(),
                Is.EqualTo(Convert.ToHexString(SHA256.HashData(firstLean)).ToLowerInvariant()));
            Assert.That(generatedLean, Does.Contain("def validateNormalized"));
            Assert.That(generatedLean, Does.Contain("def calculateBlockExecutionGasNormalized"));
            Assert.That(generatedLean, Does.Contain("def saturatingSubUInt64"));
            Assert.That(generatedLean, Does.Contain("def txMaxGasLimit : Nat := 16777216"));
        }
    }

    [TestCase(
        "cumulativeBlockExecution > blockGasLimit",
        "cumulativeBlockExecution >= blockGasLimit",
        TestName = "Cumulative_execution_boundary_mutation_changes_Lean")]
    [TestCase(
        "Math.Min(Eip7825Constants.DefaultTxGasLimitCap, txGas)",
        "Math.Max(Eip7825Constants.DefaultTxGasLimitCap, txGas)",
        TestName = "Execution_cap_minimum_mutation_changes_Lean")]
    [TestCase(
        "if (txGas > stateAvailable)",
        "if (txGas >= stateAvailable)",
        TestName = "State_dimension_boundary_mutation_changes_Lean")]
    [TestCase(
        "Math.Max(preRefundGas.SaturatingSub(blockStateGas), calldataFloor)",
        "Math.Min(preRefundGas.SaturatingSub(blockStateGas), calldataFloor)",
        TestName = "Calldata_floor_maximum_mutation_changes_Lean")]
    public void Kernel_semantic_mutations_change_generated_Lean(string original, string replacement)
    {
        string kernelSource = ReadProductionSource(KernelSourceRelativePath);
        Assert.That(kernelSource, Does.Contain(original));
        using Fixture baseline = new(kernelSource,
            ReadProductionSource(Eip7825SourceRelativePath),
            ReadProductionSource(UInt64ExtensionsSourceRelativePath));
        using Fixture mutated = new(kernelSource.Replace(original, replacement, StringComparison.Ordinal),
            ReadProductionSource(Eip7825SourceRelativePath),
            ReadProductionSource(UInt64ExtensionsSourceRelativePath));

        ExtractionResult baselineResult = StateGasChargeExtractor.ExtractBlockGasInclusion(baseline.Root, baseline.Output);
        ExtractionResult mutatedResult = StateGasChargeExtractor.ExtractBlockGasInclusion(mutated.Root, mutated.Output);

        Assert.That(
            SemanticBody(File.ReadAllText(mutatedResult.LeanPath)),
            Is.Not.EqualTo(SemanticBody(File.ReadAllText(baselineResult.LeanPath))));
    }

    [Test]
    public void Eip7825_cap_mutation_changes_generated_Lean()
    {
        string eip7825Source = ReadProductionSource(Eip7825SourceRelativePath);
        using Fixture baseline = new(ReadProductionSource(KernelSourceRelativePath),
            eip7825Source,
            ReadProductionSource(UInt64ExtensionsSourceRelativePath));
        using Fixture mutated = new(ReadProductionSource(KernelSourceRelativePath),
            eip7825Source.Replace("16_777_216", "16_777_217", StringComparison.Ordinal),
            ReadProductionSource(UInt64ExtensionsSourceRelativePath));

        ExtractionResult baselineResult = StateGasChargeExtractor.ExtractBlockGasInclusion(baseline.Root, baseline.Output);
        ExtractionResult mutatedResult = StateGasChargeExtractor.ExtractBlockGasInclusion(mutated.Root, mutated.Output);

        Assert.That(
            SemanticBody(File.ReadAllText(mutatedResult.LeanPath)),
            Is.Not.EqualTo(SemanticBody(File.ReadAllText(baselineResult.LeanPath))));
    }

    [Test]
    public void Rejects_unlisted_block_gas_intrinsic()
    {
        string source = ReadProductionSource(KernelSourceRelativePath).Replace(
            "Math.Max(preRefundGas.SaturatingSub(blockStateGas), calldataFloor)",
            "Math.Clamp(preRefundGas.SaturatingSub(blockStateGas), 0UL, calldataFloor)",
            StringComparison.Ordinal);
        using Fixture fixture = new(source,
            ReadProductionSource(Eip7825SourceRelativePath),
            ReadProductionSource(UInt64ExtensionsSourceRelativePath));

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractBlockGasInclusion(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("is not an allowed intrinsic"));
    }

    [Test]
    public void Rejects_an_unpinned_root_method()
    {
        string source = ReadProductionSource(KernelSourceRelativePath).Replace(
            "public static class Eip8037BlockGasInclusionCheck\n{",
            "public static class Eip8037BlockGasInclusionCheck\n{\n    public static ulong Extra(ulong value) => value;",
            StringComparison.Ordinal);
        using Fixture fixture = new(source,
            ReadProductionSource(Eip7825SourceRelativePath),
            ReadProductionSource(UInt64ExtensionsSourceRelativePath));

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractBlockGasInclusion(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("must contain exactly the pinned inclusion methods"));
    }

    [Test]
    public void Rejects_reordered_outcome_values()
    {
        string source = ReadProductionSource(KernelSourceRelativePath).Replace(
            "Outcome { Ok, ExecutionDimensionExceeded, StateDimensionExceeded }",
            "Outcome { Ok, StateDimensionExceeded, ExecutionDimensionExceeded }",
            StringComparison.Ordinal);
        using Fixture fixture = new(source,
            ReadProductionSource(Eip7825SourceRelativePath),
            ReadProductionSource(UInt64ExtensionsSourceRelativePath));

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractBlockGasInclusion(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("pinned Outcome and method shape"));
    }

    [Test]
    public void Rejects_checked_unsigned_subtraction()
    {
        string source = ReadProductionSource(KernelSourceRelativePath).Replace(
            "blockGasLimit - cumulativeBlockExecution",
            "checked(blockGasLimit - cumulativeBlockExecution)",
            StringComparison.Ordinal);
        using Fixture fixture = new(source,
            ReadProductionSource(Eip7825SourceRelativePath),
            ReadProductionSource(UInt64ExtensionsSourceRelativePath));

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractBlockGasInclusion(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("Roslyn did not bind"));
    }

    [Test]
    public void Rejects_syntax_outside_the_restricted_subset()
    {
        string source = ReadProductionSource(KernelSourceRelativePath).Replace(
            "ulong executionAvailable = blockGasLimit - cumulativeBlockExecution;",
            "for (int i = 0; i < 1; i++) { txGas++; }\n        ulong executionAvailable = blockGasLimit - cumulativeBlockExecution;",
            StringComparison.Ordinal);
        using Fixture fixture = new(source,
            ReadProductionSource(Eip7825SourceRelativePath),
            ReadProductionSource(UInt64ExtensionsSourceRelativePath));

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractBlockGasInclusion(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("ForStatement"));
    }

    [Test]
    public void Rejects_changed_saturating_subtract_definition()
    {
        string extensionsSource = ReadProductionSource(UInt64ExtensionsSourceRelativePath).Replace(
            "a > b ? a - b : 0UL",
            "a >= b ? a - b : 0UL",
            StringComparison.Ordinal);
        using Fixture fixture = new(ReadProductionSource(KernelSourceRelativePath),
            ReadProductionSource(Eip7825SourceRelativePath),
            extensionsSource);

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractBlockGasInclusion(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("SaturatingSub must retain"));
    }

    private static string ReadProductionSource(string relativePath)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate the production source.", relativePath);
    }

    private static string SemanticBody(string generatedLean)
    {
        const string NamespaceMarker = "namespace Eip803x.Generated.Eip8037BlockGasInclusionCheck";
        int start = generatedLean.IndexOf(NamespaceMarker, StringComparison.Ordinal);
        return start < 0
            ? throw new AssertionException("Generated Lean namespace marker was not found.")
            : generatedLean[start..];
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(string kernelSource, string eip7825Source, string uint64ExtensionsSource)
        {
            Root = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "block-gas-inclusion-extractor-fixtures",
                Guid.NewGuid().ToString("N"));
            Output = Path.Combine(Root, "generated");
            Write(KernelSourceRelativePath, kernelSource);
            Write(Eip7825SourceRelativePath, eip7825Source);
            Write(UInt64ExtensionsSourceRelativePath, uint64ExtensionsSource);
        }

        public string Root { get; }

        public string Output { get; }

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private void Write(string relativePath, string source)
        {
            string path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }
}
