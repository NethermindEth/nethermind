// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.Extractor.Test;

[TestFixture]
public class PrecompileGasPricingExtractorTests
{
    private const string KernelSourceRelativePath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/PrecompileGasPricingKernel.cs";
    private const string AdapterSourceRelativePath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs";
    private const string ContractSourceRelativePath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs";
    private const string FullFrameSourceRelativePath =
        "src/Nethermind/Nethermind.Evm/VirtualMachine.cs";
    private const string InlineSourceRelativePath =
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.std.cs";
    private const string MainnetDiSourceRelativePath =
        "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs";
    private const string MainnetDiExtensionSourceRelativePath =
        "src/Nethermind/Nethermind.Core/ContainerBuilderExtensions.cs";

    [Test]
    public void Extracts_precompile_pricing_deterministically_with_adapter_and_call_shapes()
    {
        using Fixture fixture = new();

        ExtractionResult first = StateGasChargeExtractor.ExtractPrecompileGasPricing(fixture.Root, fixture.Output);
        byte[] firstIr = File.ReadAllBytes(first.IrPath);
        byte[] firstManifest = File.ReadAllBytes(first.ManifestPath);
        byte[] firstLean = File.ReadAllBytes(first.LeanPath);
        ExtractionResult second = StateGasChargeExtractor.ExtractPrecompileGasPricing(fixture.Root, fixture.Output);
        using JsonDocument manifest = JsonDocument.Parse(firstManifest);
        using JsonDocument ir = JsonDocument.Parse(firstIr);
        JsonElement root = manifest.RootElement;
        JsonElement adapter = ir.RootElement.GetProperty("adapter");
        string generatedLean = File.ReadAllText(first.LeanPath);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(second.IrPath), Is.EqualTo(firstIr));
            Assert.That(File.ReadAllBytes(second.ManifestPath), Is.EqualTo(firstManifest));
            Assert.That(File.ReadAllBytes(second.LeanPath), Is.EqualTo(firstLean));
            Assert.That(first.MethodCount, Is.EqualTo(1));
            Assert.That(root.GetProperty("rootSignatures").GetArrayLength(), Is.EqualTo(1));
            Assert.That(root.GetProperty("supportingSources").GetArrayLength(), Is.EqualTo(6));
            Assert.That(
                root.GetProperty("leanArtifact").GetProperty("sha256").GetString(),
                Is.EqualTo(Convert.ToHexString(SHA256.HashData(firstLean)).ToLowerInvariant()));
            Assert.That(adapter.GetProperty("baseThenData").GetBoolean(), Is.True);
            Assert.That(adapter.GetProperty("onlyExecutionGasIsAssigned").GetBoolean(), Is.True);
            Assert.That(adapter.GetProperty("fullFrameUsesLocalCopy").GetBoolean(), Is.True);
            Assert.That(adapter.GetProperty("inlineUsesByRefChild").GetBoolean(), Is.True);
            Assert.That(adapter.GetProperty("ethereumVirtualMachineUsesStandardGasPolicy").GetBoolean(), Is.True);
            Assert.That(adapter.GetProperty("mainnetDiRegistersEthereumVirtualMachine").GetBoolean(), Is.True);
            Assert.That(
                adapter.GetProperty("concreteVirtualMachine").GetString(),
                Is.EqualTo("EthereumVirtualMachine : VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>"));
            Assert.That(
                adapter.GetProperty("mainnetDiRegistration").GetString(),
                Is.EqualTo(".AddScoped<IVirtualMachine, EthereumVirtualMachine>()"));
            Assert.That(
                adapter.GetProperty("mainnetDiRegistrationExtension").GetString(),
                Is.EqualTo(
                    "Nethermind.Core.ContainerBuilderExtensions.AddScoped<Nethermind.Evm.IVirtualMachine, Nethermind.Evm.EthereumVirtualMachine>()"));
            Assert.That(adapter.GetProperty("mainnetDiRegistrationAssembly").GetString(), Is.EqualTo("Nethermind.Core"));
            Assert.That(adapter.GetProperty("mainnetDiBindsProductionScopedExtension").GetBoolean(), Is.True);
            Assert.That(generatedLean, Does.Contain("def tryConsumeNormalized"));
            Assert.That(generatedLean, Does.Not.Contain("theorem"));
        }
    }

    [TestCase(
        KernelSourceRelativePath,
        "baseGasCost > ulong.MaxValue - dataGasCost",
        "baseGasCost >= ulong.MaxValue - dataGasCost",
        TestName = "Rejects_overflow_guard_mutation")]
    [TestCase(
        KernelSourceRelativePath,
        "PrecompileGasPricingOutcome.OutOfGas, 0, 0",
        "PrecompileGasPricingOutcome.OutOfGas, gas, 0",
        TestName = "Rejects_out_of_gas_clear_mutation")]
    [TestCase(
        AdapterSourceRelativePath,
        "PrecompileGasPricingKernel.TryConsume(",
        "UnpinnedPricingKernel.TryConsume(",
        TestName = "Rejects_specialized_adapter_dispatch_mutation")]
    [TestCase(
        AdapterSourceRelativePath,
        "gas.Value = result.RemainingGas;",
        "gas.StateGasUsed = 0;",
        TestName = "Rejects_state_field_assignment_mutation")]
    [TestCase(
        ContractSourceRelativePath,
        "baseGasCost <= ulong.MaxValue - dataGasCost",
        "baseGasCost < ulong.MaxValue - dataGasCost",
        TestName = "Rejects_default_policy_overflow_guard_mutation")]
    [TestCase(
        FullFrameSourceRelativePath,
        "TGasPolicy.TryConsumePrecompileGas(ref gas, precompile, callData, spec)",
        "TGasPolicy.UpdateGas(ref gas, 0)",
        TestName = "Rejects_full_frame_generic_dispatch_mutation")]
    [TestCase(
        FullFrameSourceRelativePath,
        "if (!TGasPolicy.TryConsumePrecompileGas(ref gas, precompile, callData, spec))",
        "if (TGasPolicy.TryConsumePrecompileGas(ref gas, precompile, callData, spec))",
        TestName = "Rejects_full_frame_success_condition_mutation")]
    [TestCase(
        FullFrameSourceRelativePath,
        "return new(default, precompileSuccess: false, shouldRevert: true, EvmExceptionType.OutOfGas);",
        "state.Gas = gas;\n            return new(default, precompileSuccess: false, shouldRevert: true, EvmExceptionType.OutOfGas);",
        TestName = "Rejects_full_frame_failure_install_mutation")]
    [TestCase(
        InlineSourceRelativePath,
        "TGasPolicy.TryConsumePrecompileGas(ref childGas, precompile, callData, spec)",
        "TGasPolicy.UpdateGas(ref childGas, 0)",
        TestName = "Rejects_inline_by_ref_generic_dispatch_mutation")]
    [TestCase(
        InlineSourceRelativePath,
        "TGasPolicy.RestoreChildStateGasOnHalt(ref gas, in childGas);",
        "TGasPolicy.Refund(ref gas, in childGas);",
        TestName = "Rejects_inline_pricing_failure_restore_mutation")]
    [TestCase(
        FullFrameSourceRelativePath,
        ") : VirtualMachine<EthereumGasPolicy>(",
        ") : VirtualMachine<AlternateGasPolicy>(",
        TestName = "Rejects_concrete_ethereum_vm_alternate_gas_policy_mutation")]
    [TestCase(
        MainnetDiSourceRelativePath,
        ".AddScoped<IVirtualMachine, EthereumVirtualMachine>()",
        ".AddScoped<IVirtualMachine, AlternateVirtualMachine>()",
        TestName = "Rejects_mainnet_di_alternate_virtual_machine_mutation")]
    public void Pricing_and_adapter_semantic_mutations_are_rejected(
        string relativePath,
        string original,
        string replacement)
    {
        using Fixture fixture = new();
        fixture.Replace(relativePath, original, replacement);

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractPrecompileGasPricing(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("Precompile gas pricing"));
    }

    [Test]
    public void Conditional_compilation_and_partial_kernel_or_result_are_rejected()
    {
        using Fixture directiveFixture = new();
        directiveFixture.Replace(
            KernelSourceRelativePath,
            "internal enum PrecompileGasPricingOutcome : byte",
            "#if RELEASE\n#endif\n\ninternal enum PrecompileGasPricingOutcome : byte");

        ExtractionException directiveException = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractPrecompileGasPricing(
                directiveFixture.Root,
                directiveFixture.Output))!;
        Assert.That(directiveException.Message, Does.Contain("preprocessor"));

        using Fixture partialFixture = new();
        partialFixture.Replace(
            KernelSourceRelativePath,
            "internal static class PrecompileGasPricingKernel",
            "internal static partial class PrecompileGasPricingKernel");

        ExtractionException partialException = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractPrecompileGasPricing(
                partialFixture.Root,
                partialFixture.Output))!;
        Assert.That(partialException.Message, Does.Contain("PrecompileGasPricingKernel"));

        using Fixture partialResultFixture = new();
        partialResultFixture.Replace(
            KernelSourceRelativePath,
            "internal readonly struct PrecompileGasPricingResult",
            "internal readonly partial struct PrecompileGasPricingResult");

        ExtractionException partialResultException = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractPrecompileGasPricing(
                partialResultFixture.Root,
                partialResultFixture.Output))!;
        Assert.That(partialResultException.Message, Does.Contain("result"));
    }

    [Test]
    public void Full_frame_conditional_compilation_alias_and_disabled_text_are_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(
            FullFrameSourceRelativePath,
            "using Int256;",
            """
            #if NET10_0
            using GasPolicy = Nethermind.Evm.GasPolicy.EthereumGasPolicy;
            #else
            using GasPolicy = Nethermind.Evm.GasPolicy.AlternateGasPolicy;
            #endif

            using Int256;
            """);

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractPrecompileGasPricing(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("disabled source text"));
    }

    [TestCase(
        "EthereumGasPolicy",
        "Nethermind.Evm.GasPolicy.AlternateGasPolicy",
        TestName = "Rejects_full_frame_standard_gas_policy_alias")]
    [TestCase(
        "VirtualMachine",
        "Nethermind.Evm.AlternateVirtualMachine",
        TestName = "Rejects_full_frame_virtual_machine_alias")]
    [TestCase(
        "IVirtualMachine",
        "Nethermind.Evm.AlternateVirtualMachineContract",
        TestName = "Rejects_full_frame_virtual_machine_interface_alias")]
    public void Full_frame_selected_type_aliases_are_rejected(string alias, string target)
    {
        using Fixture fixture = new();
        fixture.Replace(
            FullFrameSourceRelativePath,
            "using Int256;",
            $"using {alias} = {target};{Environment.NewLine}using Int256;");

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractPrecompileGasPricing(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("aliases"));
    }

    [TestCase(
        "IVirtualMachine",
        "Nethermind.Evm.AlternateVirtualMachineContract",
        TestName = "Rejects_mainnet_di_virtual_machine_interface_alias")]
    [TestCase(
        "EthereumVirtualMachine",
        "Nethermind.Evm.AlternateVirtualMachine",
        TestName = "Rejects_mainnet_di_virtual_machine_alias")]
    public void Mainnet_di_selected_type_aliases_are_rejected(string alias, string target)
    {
        using Fixture fixture = new();
        fixture.Replace(
            MainnetDiSourceRelativePath,
            "namespace Nethermind.Init.Modules;",
            $"namespace Nethermind.Init.Modules;{Environment.NewLine}using {alias} = {target};");

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractPrecompileGasPricing(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("aliases"));
    }

    [Test]
    public void Mainnet_di_competing_scoped_extension_and_container_builder_shadow_are_rejected()
    {
        using Fixture competingExtensionFixture = new();
        competingExtensionFixture.Append(
            MainnetDiSourceRelativePath,
            """

            public static class AlternateContainerBuilderExtensions
            {
                public static ContainerBuilder AddScoped<T, TImpl>(this ContainerBuilder builder)
                    where TImpl : T where T : notnull => builder;
            }
            """);

        ExtractionException competingExtensionException = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractPrecompileGasPricing(
                competingExtensionFixture.Root,
                competingExtensionFixture.Output))!;
        Assert.That(competingExtensionException.Message, Does.Contain("pinned Nethermind.Core"));

        using Fixture shadowFixture = new();
        shadowFixture.Append(
            MainnetDiSourceRelativePath,
            """

            public sealed class ContainerBuilder
            {
            }
            """);

        ExtractionException shadowException = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractPrecompileGasPricing(shadowFixture.Root, shadowFixture.Output))!;
        Assert.That(shadowException.Message, Does.Contain("ContainerBuilder"));
    }

    [Test]
    public void Mainnet_di_scoped_extension_source_target_is_pinned()
    {
        using Fixture fixture = new();
        fixture.Replace(
            MainnetDiExtensionSourceRelativePath,
            "public static ContainerBuilder AddScoped<T, TImpl>(this ContainerBuilder builder) where TImpl : T where T : notnull",
            "public static ContainerBuilder RegisterScoped<T, TImpl>(this ContainerBuilder builder) where TImpl : T where T : notnull");

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractPrecompileGasPricing(fixture.Root, fixture.Output))!;
        Assert.That(exception.Message, Does.Contain("registration extension"));
    }

    [Test]
    public void Adapter_symbol_shadow_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Append(
            AdapterSourceRelativePath,
            """

            internal static class PrecompileGasPricingKernel
            {
            }
            """);

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractPrecompileGasPricing(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("shadow"));
    }

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

        throw new FileNotFoundException("Could not locate the production precompile gas pricing source.", relativePath);
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly string[] SourceRelativePaths =
        [
            KernelSourceRelativePath,
            AdapterSourceRelativePath,
            ContractSourceRelativePath,
            FullFrameSourceRelativePath,
            InlineSourceRelativePath,
            MainnetDiSourceRelativePath,
            MainnetDiExtensionSourceRelativePath,
        ];

        public Fixture()
        {
            Root = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "precompile-gas-pricing-extractor-fixtures",
                Guid.NewGuid().ToString("N"));
            Output = Path.Combine(Root, "generated");
            foreach (string relativePath in SourceRelativePaths)
            {
                Write(relativePath, ReadProductionFile(relativePath));
            }
        }

        public string Root { get; }

        public string Output { get; }

        public void Replace(string relativePath, string original, string replacement)
        {
            string source = Read(relativePath);
            Assert.That(source, Does.Contain(original));
            Write(relativePath, source.Replace(original, replacement, StringComparison.Ordinal));
        }

        public void Append(string relativePath, string suffix) =>
            Write(relativePath, Read(relativePath) + suffix);

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private string Read(string relativePath) => File.ReadAllText(Path.Combine(Root, relativePath));

        private void Write(string relativePath, string source)
        {
            string path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }
}
