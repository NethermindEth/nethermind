// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.Extractor.Test;

[TestFixture]
public class ExtendedStackDecoderExtractorTests
{
    private const string SourceRelativePath =
        "src/Nethermind/Nethermind.Evm/Instructions/ExtendedStackDecoderKernel.cs";
    private const string AdapterSourceRelativePath =
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Stack.cs";
    private const string ShadowSourceRelativePath =
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Shadow.cs";
    private static readonly string[] EvmInstructionsPartialSourceRelativePaths =
    [
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Bitwise.cs",
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.cs",
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.std.cs",
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.CodeCopy.cs",
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.ControlFlow.cs",
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Create.cs",
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Crypto.cs",
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Environment.cs",
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Math1Param.cs",
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Math2Param.cs",
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Math3Param.cs",
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Shifts.cs",
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Spec.cs",
        AdapterSourceRelativePath,
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Storage.cs",
    ];

    [Test]
    public void Extracts_both_extended_stack_decoders_deterministically_with_adapter_manifest()
    {
        using Fixture fixture = new(ReadProductionFile(SourceRelativePath));

        ExtractionResult first = StateGasChargeExtractor.ExtractExtendedStackDecoder(fixture.Root, fixture.Output);
        byte[] firstIr = File.ReadAllBytes(first.IrPath);
        byte[] firstManifest = File.ReadAllBytes(first.ManifestPath);
        byte[] firstLean = File.ReadAllBytes(first.LeanPath);
        ExtractionResult second = StateGasChargeExtractor.ExtractExtendedStackDecoder(fixture.Root, fixture.Output);
        using JsonDocument manifest = JsonDocument.Parse(firstManifest);
        using JsonDocument ir = JsonDocument.Parse(firstIr);
        JsonElement root = manifest.RootElement;
        JsonElement adapter = ir.RootElement.GetProperty("adapter");
        string[] signatures = root.GetProperty("rootSignatures")
            .EnumerateArray()
            .Select(static signature => signature.GetString()!)
            .ToArray();
        string[] supportingSources = root.GetProperty("supportingSources")
            .EnumerateArray()
            .Select(static source => source.GetProperty("path").GetString()!)
            .ToArray();
        string generatedLean = File.ReadAllText(first.LeanPath);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(second.IrPath), Is.EqualTo(firstIr));
            Assert.That(File.ReadAllBytes(second.ManifestPath), Is.EqualTo(firstManifest));
            Assert.That(File.ReadAllBytes(second.LeanPath), Is.EqualTo(firstLean));
            Assert.That(first.MethodCount, Is.EqualTo(2));
            Assert.That(signatures, Has.Length.EqualTo(2));
            Assert.That(signatures[0], Does.Contain("ExtendedStackDecoderKernel.DecodePair("));
            Assert.That(signatures[1], Does.Contain("ExtendedStackDecoderKernel.DecodeSingle("));
            Assert.That(supportingSources, Is.EqualTo(EvmInstructionsPartialSourceRelativePaths));
            Assert.That(
                root.GetProperty("leanArtifact").GetProperty("sha256").GetString(),
                Is.EqualTo(Convert.ToHexString(SHA256.HashData(firstLean)).ToLowerInvariant()));
            Assert.That(adapter.GetProperty("immediateReader").GetString(), Is.EqualTo("ReadEip8024ImmediateOrZero"));
            Assert.That(adapter.GetProperty("singleDecoder").GetString(), Is.EqualTo("TryDecodeSingle"));
            Assert.That(adapter.GetProperty("pairDecoder").GetString(), Is.EqualTo("TryDecodePair"));
            Assert.That(adapter.GetProperty("adapterSteps").GetArrayLength(), Is.EqualTo(5));
            Assert.That(adapter.GetProperty("missingImmediateReadsZero").GetBoolean(), Is.True);
            Assert.That(adapter.GetProperty("kernelDecodePrecedesValidityGate").GetBoolean(), Is.True);
            Assert.That(adapter.GetProperty("invalidImmediateDoesNotAdvanceProgramCounter").GetBoolean(), Is.True);
            Assert.That(adapter.GetProperty("validImmediateAdvancesProgramCounter").GetBoolean(), Is.True);
            Assert.That(adapter.GetProperty("singleDepthMapsToDupAndOneBasedSwap").GetBoolean(), Is.True);
            Assert.That(adapter.GetProperty("pairPositionsMapToOneBasedExchange").GetBoolean(), Is.True);
            Assert.That(adapter.GetProperty("decoderInvocationsBindToTopLevelKernel").GetBoolean(), Is.True);
            Assert.That(generatedLean, Does.Contain("def decodeSingle"));
            Assert.That(generatedLean, Does.Contain("def decodePair"));
            Assert.That(generatedLean, Does.Not.Contain("theorem"));
        }
    }

    [TestCase(
        "int depth = (immediate + 145) & 0xFF;",
        "int depth = (immediate + 144) & 0xFF;",
        TestName = "Rejects_single_depth_shift_mutation")]
    [TestCase(
        "int shifted = immediate ^ 0x8F;",
        "int shifted = immediate ^ 0x8E;",
        TestName = "Rejects_pair_xor_mutation")]
    [TestCase(
        "bool isValid = (uint)(immediate - 0x52) > 0x2D;",
        "bool isValid = (uint)(immediate - 0x52) >= 0x2D;",
        TestName = "Rejects_pair_forbidden_range_mutation")]
    [TestCase(
        "[MethodImpl(MethodImplOptions.AggressiveInlining)]",
        "[MethodImpl(MethodImplOptions.NoInlining)]",
        TestName = "Rejects_kernel_inlining_mutation")]
    public void Kernel_semantic_mutations_are_rejected(string original, string replacement)
    {
        string source = ReadProductionFile(SourceRelativePath);
        Assert.That(source, Does.Contain(original));
        using Fixture fixture = new(source.Replace(original, replacement, StringComparison.Ordinal));

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractExtendedStackDecoder(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("Extended-stack"));
    }

    [Test]
    public void Rejects_unpinned_decoder_method()
    {
        string source = ReadProductionFile(SourceRelativePath).Replace(
            "internal static class ExtendedStackDecoderKernel\n{",
            "internal static class ExtendedStackDecoderKernel\n{\n    public static int Extra(int value) => value;",
            StringComparison.Ordinal);
        using Fixture fixture = new(source);

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractExtendedStackDecoder(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("pinned decoder methods"));
    }

    [Test]
    public void Rejects_decoder_invocation_that_binds_to_nested_shadow()
    {
        const string shadow = """
            namespace Nethermind.Evm;

            public static partial class EvmInstructions
            {
                private static class ExtendedStackDecoderKernel
                {
                    public static ExtendedStackSingleDecode DecodeSingle(byte immediate) => default;

                    public static ExtendedStackPairDecode DecodePair(byte immediate) => default;
                }
            }
            """;
        using Fixture fixture = new(
            ReadProductionFile(SourceRelativePath),
            additionalSources: [(ShadowSourceRelativePath, shadow)]);

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractExtendedStackDecoder(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("must bind to the pinned top-level decoder kernel method"));
    }

    [TestCase(
        "depth = decoded.Depth;",
        "depth = decoded.Depth + 1;",
        TestName = "Rejects_single_depth_projection_mutation")]
    [TestCase(
        "stack.Swap<TTracingInst, OnFlag>(depth + 1)",
        "stack.Swap<TTracingInst, OnFlag>(depth)",
        TestName = "Rejects_swap_one_based_position_mutation")]
    [TestCase(
        "n = decoded.FirstPosition;",
        "n = decoded.FirstPosition - 1;",
        TestName = "Rejects_first_exchange_position_projection_mutation")]
    [TestCase(
        "m = decoded.SecondPosition;",
        "m = decoded.SecondPosition - 1;",
        TestName = "Rejects_second_exchange_position_projection_mutation")]
    [TestCase(
        "stack.Exchange<TTracingInst>(n, m)",
        "stack.Exchange<TTracingInst>(n - 1, m)",
        TestName = "Rejects_exchange_call_position_mutation")]
    [TestCase(
        "programCounter++;",
        "programCounter += 2;",
        TestName = "Rejects_program_counter_advance_mutation")]
    [TestCase(
        ": (byte)0;",
        ": (byte)1;",
        TestName = "Rejects_missing_immediate_zero_extension_mutation")]
    [TestCase(
        "[MethodImpl(MethodImplOptions.AggressiveInlining)]",
        "[MethodImpl(MethodImplOptions.NoInlining)]",
        TestName = "Rejects_adapter_inlining_mutation")]
    [TestCase(
        "[SkipLocalsInit]",
        "[SkipLocalsInitDisabled]",
        TestName = "Rejects_adapter_skip_locals_init_mutation")]
    public void Adapter_shape_mutations_are_rejected(string original, string replacement)
    {
        string adapter = ReadProductionFile(AdapterSourceRelativePath);
        Assert.That(adapter, Does.Contain(original));
        using Fixture fixture = new(ReadProductionFile(SourceRelativePath), adapter.Replace(original, replacement, StringComparison.Ordinal));

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractExtendedStackDecoder(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("Extended-stack"));
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

        throw new FileNotFoundException("Could not locate the production extended-stack source.", relativePath);
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(
            string source,
            string? adapter = null,
            IReadOnlyList<(string RelativePath, string Source)>? additionalSources = null)
        {
            Root = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "extended-stack-decoder-extractor-fixtures",
                Guid.NewGuid().ToString("N"));
            string sourcePath = Path.Combine(Root, SourceRelativePath);
            Output = Path.Combine(Root, "generated");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllText(sourcePath, source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            foreach (string path in EvmInstructionsPartialSourceRelativePaths)
            {
                WriteSource(
                    path,
                    path == AdapterSourceRelativePath
                        ? adapter ?? ReadProductionFile(path)
                        : ReadProductionFile(path));
            }

            if (additionalSources is not null)
            {
                foreach ((string relativePath, string additionalSource) in additionalSources)
                {
                    WriteSource(relativePath, additionalSource);
                }
            }
        }

        public string Root { get; }

        public string Output { get; }

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private void WriteSource(string relativePath, string content)
        {
            string path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }
}
