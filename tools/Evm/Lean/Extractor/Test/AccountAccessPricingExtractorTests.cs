// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.Extractor.Test;

[TestFixture]
public class AccountAccessPricingExtractorTests
{
    private const string SourceRelativePath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/AccountAccessPricingKernel.cs";
    private const string KindSourceRelativePath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/AccountAccessKind.cs";
    private const string AdapterSourceRelativePath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs";
    private const string CallSourceRelativePath =
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.cs";
    private const string SpecSourceRelativePath =
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Spec.cs";

    [Test]
    public void Extracts_account_access_pricing_deterministically_with_adapter_manifest()
    {
        using Fixture fixture = new(ReadProductionFile(SourceRelativePath));

        ExtractionResult first = StateGasChargeExtractor.ExtractAccountAccessPricing(fixture.Root, fixture.Output);
        byte[] firstIr = File.ReadAllBytes(first.IrPath);
        byte[] firstManifest = File.ReadAllBytes(first.ManifestPath);
        byte[] firstLean = File.ReadAllBytes(first.LeanPath);
        ExtractionResult second = StateGasChargeExtractor.ExtractAccountAccessPricing(fixture.Root, fixture.Output);
        using JsonDocument manifest = JsonDocument.Parse(firstManifest);
        using JsonDocument ir = JsonDocument.Parse(firstIr);
        JsonElement root = manifest.RootElement;
        JsonElement adapter = ir.RootElement.GetProperty("adapter");
        string[] signatures = root.GetProperty("rootSignatures")
            .EnumerateArray()
            .Select(static signature => signature.GetString()!)
            .ToArray();
        string[] supportingPaths = root.GetProperty("supportingSources")
            .EnumerateArray()
            .Select(static source => source.GetProperty("path").GetString()!)
            .ToArray();
        string generatedLean = File.ReadAllText(first.LeanPath);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(second.IrPath), Is.EqualTo(firstIr));
            Assert.That(File.ReadAllBytes(second.ManifestPath), Is.EqualTo(firstManifest));
            Assert.That(File.ReadAllBytes(second.LeanPath), Is.EqualTo(firstLean));
            Assert.That(first.MethodCount, Is.EqualTo(1));
            Assert.That(signatures, Has.Length.EqualTo(1));
            Assert.That(signatures[0], Does.Contain("AccountAccessPricingKernel.Price("));
            Assert.That(root.GetProperty("supportingSources").GetArrayLength(), Is.EqualTo(4));
            Assert.That(supportingPaths, Is.EquivalentTo(new[]
                { KindSourceRelativePath, AdapterSourceRelativePath, CallSourceRelativePath, SpecSourceRelativePath }));
            Assert.That(
                root.GetProperty("leanArtifact").GetProperty("sha256").GetString(),
                Is.EqualTo(Convert.ToHexString(SHA256.HashData(firstLean)).ToLowerInvariant()));
            Assert.That(adapter.GetProperty("delegationMethod").GetString(),
                Is.EqualTo("TryConsumeDelegatedAccountAccessGas"));
            Assert.That(adapter.GetProperty("coreMethod").GetString(),
                Is.EqualTo("TryConsumeAccountAccessGasCore"));
            Assert.That(adapter.GetProperty("callSiteMethod").GetString(), Is.EqualTo("InstructionCall"));
            Assert.That(adapter.GetProperty("callSpecMethod").GetString(),
                Is.EqualTo("CallSpec.TryConsumeDelegatedAccountAccessGas"));
            Assert.That(adapter.GetProperty("tracingWarmUpPrecedesFacts").GetBoolean(), Is.True);
            Assert.That(adapter.GetProperty("precompileCheckOnlyAfterColdFact").GetBoolean(), Is.True);
            Assert.That(adapter.GetProperty("gasUpdateFollowsPricing").GetBoolean(), Is.True);
            Assert.That(adapter.GetProperty("delegatedAccessIsSecondAndConditional").GetBoolean(), Is.True);
            Assert.That(adapter.GetProperty("delegatedAccessPathIsReachable").GetBoolean(), Is.True);
            Assert.That(generatedLean, Does.Contain("def priceNormalized"));
            Assert.That(generatedLean, Does.Contain("inductive Decision"));
            Assert.That(generatedLean, Does.Not.Contain("theorem"));
        }
    }

    [TestCase(
        "if (isCold && !isPrecompile)",
        "if (isCold || !isPrecompile)",
        TestName = "Rejects_cold_precompile_branch_mutation")]
    [TestCase(
        "kind == AccountAccessKind.SelfDestructBeneficiary && !eip8038Enabled",
        "kind == AccountAccessKind.SelfDestructBeneficiary || !eip8038Enabled",
        TestName = "Rejects_legacy_selfdestruct_branch_mutation")]
    [TestCase(
        "AccountAccessPricingDecision.Charge, warmAccessGas",
        "AccountAccessPricingDecision.Charge, coldAccountAccessGas",
        TestName = "Rejects_warm_schedule_mutation")]
    public void Kernel_semantic_mutations_are_rejected(string original, string replacement)
    {
        string source = ReadProductionFile(SourceRelativePath);
        Assert.That(source, Does.Contain(original));
        using Fixture fixture = new(source.Replace(original, replacement, StringComparison.Ordinal));

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractAccountAccessPricing(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("Account-access pricing"));
    }

    [TestCase(
        "kind == AccountAccessKind.SelfDestructBeneficiary &&\n            (!isCold || isPrecompile) &&\n            TMode.IsEip8038Enabled(spec)",
        "kind == AccountAccessKind.SelfDestructBeneficiary &&\n            (!isCold || isPrecompile) ||\n            TMode.IsEip8038Enabled(spec)",
        TestName = "Rejects_adapter_eip8038_fact_mutation")]
    [TestCase(
        "TryConsumeAccountAccessGas<Eip2929, Eip8038>(\n            ref gas, spec, in accessTracker, isTracingAccess, delegated)",
        "TryConsumeAccountAccessGas<Eip2929, Eip8038>(\n            ref gas, spec, in accessTracker, isTracingAccess, address)",
        TestName = "Rejects_adapter_delegated_step_mutation")]
    [TestCase(
        "TMode.WarmAccountAccessGas(spec)",
        "GasCostOf.WarmStateRead",
        TestName = "Rejects_adapter_warm_schedule_mutation")]
    [TestCase(
        "Eip8038.IsActive ? Eip8038Constants.WarmAccess : GasCostOf.WarmStateRead",
        "Eip8038.IsActive ? GasCostOf.WarmStateRead : GasCostOf.WarmStateRead",
        TestName = "Rejects_specialized_warm_schedule_fork_mutation")]
    [TestCase(
        "spec.IsEip8038Enabled ? Eip8038Constants.WarmAccess : GasCostOf.WarmStateRead",
        "spec.IsEip8038Enabled ? GasCostOf.WarmStateRead : GasCostOf.WarmStateRead",
        TestName = "Rejects_dynamic_warm_schedule_fork_mutation")]
    public void Adapter_shape_mutations_are_rejected(string original, string replacement)
    {
        string source = ReadProductionFile(SourceRelativePath);
        string adapter = ReadProductionFile(AdapterSourceRelativePath);
        Assert.That(adapter, Does.Contain(original));
        using Fixture fixture = new(source, adapter.Replace(original, replacement, StringComparison.Ordinal));

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractAccountAccessPricing(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("Account-access"));
    }

    [TestCase(
        "TSpec.TryConsumeDelegatedAccountAccessGas<TGasPolicy>",
        "TSpec.TryConsumeAccountAccessGas<TGasPolicy>",
        TestName = "Rejects_call_site_delegation_bypass")]
    [TestCase(
        "TGasPolicy.TryConsumeDelegatedAccountAccessGas<Eip2929, Eip8038>",
        "TGasPolicy.TryConsumeAccountAccessGas<Eip2929, Eip8038>",
        TestName = "Rejects_call_spec_delegation_bypass")]
    public void Reachable_delegation_path_mutations_are_rejected(string original, string replacement)
    {
        string source = ReadProductionFile(SourceRelativePath);
        string adapter = ReadProductionFile(AdapterSourceRelativePath);
        string call = ReadProductionFile(CallSourceRelativePath);
        string spec = ReadProductionFile(SpecSourceRelativePath);
        using Fixture fixture = new(
            source,
            adapter,
            call.Replace(original, replacement, StringComparison.Ordinal),
            spec.Replace(original, replacement, StringComparison.Ordinal));

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractAccountAccessPricing(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("helper"));
    }

    [Test]
    public void Rejects_kernel_syntax_outside_restricted_subset()
    {
        string source = ReadProductionFile(SourceRelativePath).Replace(
            "if (!hotAndColdEnabled)",
            "for (int i = 0; i < 1; i++) { }\n        if (!hotAndColdEnabled)",
            StringComparison.Ordinal);
        using Fixture fixture = new(source);

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractAccountAccessPricing(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("ForStatement"));
    }

    private static string ReadProductionFile(string relativePath)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate the production account-access source.", relativePath);
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(string source, string? adapter = null, string? call = null, string? spec = null)
        {
            Root = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "account-access-pricing-extractor-fixtures",
                Guid.NewGuid().ToString("N"));
            string sourcePath = Path.Combine(Root, SourceRelativePath);
            string kindPath = Path.Combine(Root, KindSourceRelativePath);
            string adapterPath = Path.Combine(Root, AdapterSourceRelativePath);
            string callPath = Path.Combine(Root, CallSourceRelativePath);
            string specPath = Path.Combine(Root, SpecSourceRelativePath);
            Output = Path.Combine(Root, "generated");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(adapterPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(callPath)!);
            File.WriteAllText(sourcePath, source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.WriteAllText(kindPath, ReadProductionFile(KindSourceRelativePath), new UTF8Encoding(false));
            File.WriteAllText(adapterPath, adapter ?? ReadProductionFile(AdapterSourceRelativePath), new UTF8Encoding(false));
            File.WriteAllText(callPath, call ?? ReadProductionFile(CallSourceRelativePath), new UTF8Encoding(false));
            File.WriteAllText(specPath, spec ?? ReadProductionFile(SpecSourceRelativePath), new UTF8Encoding(false));
        }

        public string Root { get; }

        public string Output { get; }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
