// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.Extractor.Test;

[TestFixture]
public class TransactionGasInitializationExtractorTests
{
    private const string SourceRelativePath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/TransactionGasInitializationKernel.cs";

    [Test]
    public void Extracts_transaction_initialization_and_block_combine_deterministically()
    {
        using Fixture fixture = new(ReadProductionSource());

        ExtractionResult first = StateGasChargeExtractor.ExtractTransactionGasInitialization(fixture.Root, fixture.Output);
        byte[] firstIr = File.ReadAllBytes(first.IrPath);
        byte[] firstManifest = File.ReadAllBytes(first.ManifestPath);
        byte[] firstLean = File.ReadAllBytes(first.LeanPath);
        ExtractionResult second = StateGasChargeExtractor.ExtractTransactionGasInitialization(fixture.Root, fixture.Output);
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
            Assert.That(first.MethodCount, Is.EqualTo(2));
            Assert.That(signatures, Has.Length.EqualTo(2));
            Assert.That(
                signatures,
                Has.Some.Contains("TransactionGasInitializationKernel.TryCreate(ulong gasLimit"));
            Assert.That(signatures, Has.Some.Contains("BlockGasAccountingKernel.Combine(ulong blockExecutionGas"));
            Assert.That(
                root.GetProperty("leanArtifact").GetProperty("sha256").GetString(),
                Is.EqualTo(Convert.ToHexString(SHA256.HashData(firstLean)).ToLowerInvariant()));
            Assert.That(generatedLean, Does.Contain("def tryCreateNormalized"));
            Assert.That(generatedLean, Does.Contain("def combineNormalized"));
            Assert.That(generatedLean, Does.Contain("def uint64ToInt64"));
            Assert.That(generatedLean, Does.Contain("inductive TransactionGasInitializationOutcome"));
        }
    }

    [TestCase(
        "gasLimit < intrinsicTotal",
        "gasLimit <= intrinsicTotal",
        TestName = "Intrinsic_limit_boundary_mutation_changes_Lean")]
    [TestCase(
        "Math.Min(availableGas, executionGasAfterIntrinsicCap)",
        "Math.Max(availableGas, executionGasAfterIntrinsicCap)",
        TestName = "Execution_cap_minimum_mutation_changes_Lean")]
    [TestCase(
        "unchecked((long)stateReservoir)",
        "unchecked((long)availableGas)",
        TestName = "Reservoir_cast_source_mutation_changes_Lean")]
    [TestCase(
        "unchecked(intrinsicExecutionGas + unchecked((ulong)intrinsicStateGas))",
        "unchecked(intrinsicExecutionGas + 0UL)",
        TestName = "Intrinsic_state_total_mutation_changes_Lean")]
    [TestCase(
        "executionGasLimitCap - intrinsicExecutionGas",
        "executionGasLimitCap - intrinsicTotal",
        TestName = "Execution_cap_state_baseline_mutation_changes_Lean")]
    [TestCase(
        "            intrinsicStateGas,",
        "            0,",
        TestName = "Intrinsic_state_used_mutation_changes_Lean")]
    [TestCase(
        "availableGas - gasLeft",
        "availableGas - availableGas",
        TestName = "Reservoir_value_mutation_changes_Lean")]
    [TestCase(
        "Math.Max(blockExecutionGas, blockStateGas)",
        "Math.Min(blockExecutionGas, blockStateGas)",
        TestName = "Block_header_maximum_mutation_changes_Lean")]
    public void Semantic_mutations_change_generated_Lean(string original, string replacement)
    {
        string source = ReadProductionSource();
        Assert.That(source, Does.Contain(original));
        using Fixture baselineFixture = new(source);
        using Fixture mutatedFixture = new(source.Replace(original, replacement, StringComparison.Ordinal));

        ExtractionResult baseline = StateGasChargeExtractor.ExtractTransactionGasInitialization(
            baselineFixture.Root,
            baselineFixture.Output);
        ExtractionResult mutated = StateGasChargeExtractor.ExtractTransactionGasInitialization(
            mutatedFixture.Root,
            mutatedFixture.Output);

        Assert.That(
            SemanticBody(File.ReadAllText(mutated.LeanPath)),
            Is.Not.EqualTo(SemanticBody(File.ReadAllText(baseline.LeanPath))));
    }

    [Test]
    public void Rejects_an_unlisted_transaction_intrinsic()
    {
        string source = ReadProductionSource().Replace(
            "Math.Max(blockExecutionGas, blockStateGas)",
            "Math.Clamp(blockExecutionGas, 0UL, blockStateGas)",
            StringComparison.Ordinal);
        using Fixture fixture = new(source);

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractTransactionGasInitialization(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("is not an allowed intrinsic"));
    }

    [Test]
    public void Rejects_checked_transaction_arithmetic()
    {
        string source = ReadProductionSource().Replace(
            "unchecked(intrinsicExecutionGas + unchecked((ulong)intrinsicStateGas))",
            "checked(intrinsicExecutionGas + unchecked((ulong)intrinsicStateGas))",
            StringComparison.Ordinal);
        using Fixture fixture = new(source);

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractTransactionGasInitialization(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("Checked"));
    }

    [Test]
    public void Rejects_an_unpinned_root_container_method()
    {
        string source = ReadProductionSource().Replace(
            "internal static class BlockGasAccountingKernel\n{",
            "internal static class BlockGasAccountingKernel\n{\n    public static ulong Extra(ulong value) => value;",
            StringComparison.Ordinal);
        using Fixture fixture = new(source);

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractTransactionGasInitialization(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("must contain exactly 'Combine'"));
    }

    [Test]
    public void Rejects_result_fields_mapped_to_different_constructor_parameters()
    {
        string source = ReadProductionSource().Replace(
            "public readonly long StateReservoir = stateReservoir;",
            "public readonly long StateReservoir = stateGasUsed;",
            StringComparison.Ordinal)
            .Replace(
                "public readonly long StateGasUsed = stateGasUsed;",
                "public readonly long StateGasUsed = stateReservoir;",
                StringComparison.Ordinal);
        using Fixture fixture = new(source);

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractTransactionGasInitialization(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("corresponding primary-constructor parameters"));
    }

    [Test]
    public void Rejects_syntax_outside_the_restricted_subset()
    {
        string source = ReadProductionSource().Replace(
            "ulong availableGas = gasLimit - intrinsicTotal;",
            "for (int i = 0; i < 1; i++) { gasLimit++; }\n        ulong availableGas = gasLimit - intrinsicTotal;",
            StringComparison.Ordinal);
        using Fixture fixture = new(source);

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractTransactionGasInitialization(fixture.Root, fixture.Output))!;

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

        throw new FileNotFoundException("Could not locate the production transaction gas kernel.", SourceRelativePath);
    }

    private static string SemanticBody(string generatedLean)
    {
        const string NamespaceMarker = "namespace Eip803x.Generated.TransactionGasInitializationKernel";
        int start = generatedLean.IndexOf(NamespaceMarker, StringComparison.Ordinal);
        return start < 0
            ? throw new AssertionException("Generated Lean namespace marker was not found.")
            : generatedLean[start..];
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(string source)
        {
            Root = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "transaction-gas-extractor-fixtures",
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
