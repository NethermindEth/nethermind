// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.Extractor.Test;

[TestFixture]
public class SStorePricingExtractorTests
{
    private const string SourceRelativePath = "src/Nethermind/Nethermind.Evm/GasPolicy/SStorePricingKernel.cs";
    private const string AdapterSourceRelativePath = "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Storage.cs";

    [Test]
    public void Extracts_sstore_pricing_deterministically_with_the_post_access_boundary()
    {
        using Fixture fixture = new(ReadProductionSource());

        ExtractionResult first = StateGasChargeExtractor.ExtractSStorePricing(fixture.Root, fixture.Output);
        byte[] firstIr = File.ReadAllBytes(first.IrPath);
        byte[] firstManifest = File.ReadAllBytes(first.ManifestPath);
        byte[] firstLean = File.ReadAllBytes(first.LeanPath);
        ExtractionResult second = StateGasChargeExtractor.ExtractSStorePricing(fixture.Root, fixture.Output);
        using JsonDocument manifest = JsonDocument.Parse(firstManifest);
        using JsonDocument ir = JsonDocument.Parse(firstIr);
        JsonElement root = manifest.RootElement;
        JsonElement adapterSource = root.GetProperty("supportingSources")[0];
        JsonElement adapter = ir.RootElement.GetProperty("adapter");
        string[] signatures = root.GetProperty("rootSignatures")
            .EnumerateArray()
            .Select(static signature => signature.GetString()!)
            .ToArray();
        string generatedLean = File.ReadAllText(first.LeanPath);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(second.IrPath), Is.EqualTo(firstIr));
            Assert.That(File.ReadAllBytes(second.ManifestPath), Is.EqualTo(firstManifest));
            Assert.That(File.ReadAllBytes(second.LeanPath), Is.EqualTo(firstLean));
            Assert.That(first.MethodCount, Is.EqualTo(2));
            Assert.That(signatures, Has.Length.EqualTo(1));
            Assert.That(signatures[0], Does.Contain("SStorePricingKernel.Price("));
            Assert.That(
                adapterSource.GetProperty("path").GetString(),
                Is.EqualTo(AdapterSourceRelativePath));
            Assert.That(
                adapterSource.GetProperty("sha256").GetString(),
                Is.EqualTo(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture.AdapterSourcePath))).ToLowerInvariant()));
            Assert.That(
                root.GetProperty("leanArtifact").GetProperty("sha256").GetString(),
                Is.EqualTo(Convert.ToHexString(SHA256.HashData(firstLean)).ToLowerInvariant()));
            Assert.That(adapter.GetProperty("activeGate").GetString(),
                Is.EqualTo("Eip8038.IsActive&&TEip8037.IsActive"));
            Assert.That(adapter.GetProperty("pricingDelegation").GetString(), Is.EqualTo("SStorePricingKernel.PriceAfterAccess(input,schedule)"));
            Assert.That(adapter.GetProperty("accessPrecedesSlotRead").GetBoolean(), Is.True);
            Assert.That(adapter.GetProperty("changedBranchSuppliesNoOpFact").GetBoolean(), Is.True);
            Assert.That(adapter.GetProperty("refundHelperReportsEachNonzeroComponent").GetBoolean(), Is.True);
            Assert.That(generatedLean, Does.Contain("def priceAfterAccess"));
            Assert.That(generatedLean, Does.Contain("def price\n"));
            Assert.That(generatedLean, Does.Contain("structure PostAccessResult"));
            Assert.That(generatedLean, Does.Not.Contain("theorem"));
        }
    }

    [TestCase(
        "!input.NewSameAsCurrent && input.CurrentSameAsOriginal",
        "input.CurrentSameAsOriginal",
        TestName = "Rejects_first_change_predicate_mutation")]
    [TestCase(
        "unchecked(-schedule.StorageClearRefund)",
        "schedule.StorageClearRefund",
        TestName = "Rejects_clear_reversal_sign_mutation")]
    [TestCase(
        "PriceAfterAccess(input, postAccessSchedule)",
        "PriceAfterAccess(input, default)",
        TestName = "Rejects_post_access_composition_mutation")]
    public void Semantic_mutations_are_rejected(string original, string replacement)
    {
        string source = ReadProductionSource();
        Assert.That(source, Does.Contain(original));
        using Fixture fixture = new(source.Replace(original, replacement, StringComparison.Ordinal));

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractSStorePricing(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("SSTORE"));
    }

    [Test]
    public void Rejects_an_unpinned_pricing_method()
    {
        string source = ReadProductionSource().Replace(
            "internal static class SStorePricingKernel\n{",
            "internal static class SStorePricingKernel\n{\n    public static ulong Extra(ulong value) => value;",
            StringComparison.Ordinal);
        using Fixture fixture = new(source);

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractSStorePricing(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("pinned pricing methods"));
    }

    [Test]
    public void Rejects_non_projected_post_access_result_field()
    {
        string source = ReadProductionSource().Replace(
            "public readonly long StateGasRefund = stateGasRefund;",
            "public readonly long StateGasRefund = stateGasRefund + 0;",
            StringComparison.Ordinal);
        using Fixture fixture = new(source);

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractSStorePricing(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("primary-constructor parameter"));
    }

    [Test]
    public void Rejects_syntax_outside_the_restricted_subset()
    {
        string source = ReadProductionSource().Replace(
            "bool writesFirstValue = !input.NewSameAsCurrent && input.CurrentSameAsOriginal;",
            "for (int i = 0; i < 1; i++) { }\n        bool writesFirstValue = !input.NewSameAsCurrent && input.CurrentSameAsOriginal;",
            StringComparison.Ordinal);
        using Fixture fixture = new(source);

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractSStorePricing(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("ForStatement"));
    }

    [TestCase(
        "if (Eip8038.IsActive && TEip8037.IsActive)",
        "if (Eip8038.IsActive)",
        TestName = "Rejects_missing_active_eip8037_gate")]
    [TestCase(
        "else if (newSameAsCurrent)",
        "else if (Eip8038.IsActive && TEip8037.IsActive)",
        TestName = "Rejects_duplicate_active_eip8038_eip8037_gate")]
    [TestCase(
        "TGasPolicy.TryConsumeStateAndExecutionGas(ref gas, pricing.StateGasCharge, pricing.ExecutionWriteGas)",
        "TGasPolicy.TryConsumeStateAndExecutionGas(ref gas, pricing.ExecutionWriteGas, pricing.StateGasCharge)",
        TestName = "Rejects_reordered_execution_and_state_charge")]
    [TestCase(
        "ApplySStoreRefund(vm, vmState, pricing.StorageClearRefundReversal);",
        "ApplySStoreRefund(vm, vmState, pricing.RestoreOriginalRefund);",
        TestName = "Rejects_collapsed_or_reordered_refund_component")]
    [TestCase(
        "Bytes.AreEqual(originalValue, currentValue),\n                    false,",
        "Bytes.AreEqual(originalValue, currentValue),\n                    newSameAsCurrent,",
        TestName = "Rejects_changed_branch_without_derived_noop_fact")]
    public void Adapter_semantic_mutations_are_rejected(string original, string replacement)
    {
        string adapter = ReadProductionAdapter();
        Assert.That(adapter, Does.Contain(original));
        using Fixture fixture = new(ReadProductionSource(), adapter.Replace(original, replacement, StringComparison.Ordinal));

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractSStorePricing(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("SSTORE adapter"));
    }

    private static string ReadProductionSource() => ReadProductionFile(SourceRelativePath);

    private static string ReadProductionAdapter() => ReadProductionFile(AdapterSourceRelativePath);

    private static string ReadProductionFile(string relativePath)
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

        throw new FileNotFoundException("Could not locate the production SSTORE pricing source.", relativePath);
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(string source, string? adapter = null)
        {
            Root = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "sstore-pricing-extractor-fixtures",
                Guid.NewGuid().ToString("N"));
            string sourcePath = Path.Combine(Root, SourceRelativePath);
            AdapterSourcePath = Path.Combine(Root, AdapterSourceRelativePath);
            Output = Path.Combine(Root, "generated");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(AdapterSourcePath)!);
            File.WriteAllText(sourcePath, source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.WriteAllText(AdapterSourcePath, adapter ?? ReadProductionAdapter(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        public string Root { get; }

        public string Output { get; }

        public string AdapterSourcePath { get; }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
