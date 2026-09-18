// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.Extractor.Test;

[TestFixture]
public class IdentityPrecompileExtractorTests
{
    private const string SourceRelativePath =
        "src/Nethermind/Nethermind.Evm.Precompiles/IdentityPrecompileKernel.cs";
    private const string AdapterSourceRelativePath =
        "src/Nethermind/Nethermind.Evm.Precompiles/IdentityPrecompile.cs";

    [Test]
    public void Extracts_identity_pricing_deterministically_with_adapter_manifest()
    {
        using Fixture fixture = new(ReadProductionFile(SourceRelativePath));

        ExtractionResult first = StateGasChargeExtractor.ExtractIdentityPrecompile(fixture.Root, fixture.Output);
        byte[] firstIr = File.ReadAllBytes(first.IrPath);
        byte[] firstManifest = File.ReadAllBytes(first.ManifestPath);
        byte[] firstLean = File.ReadAllBytes(first.LeanPath);
        ExtractionResult second = StateGasChargeExtractor.ExtractIdentityPrecompile(fixture.Root, fixture.Output);
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
            Assert.That(first.MethodCount, Is.EqualTo(2));
            Assert.That(root.GetProperty("rootSignatures").GetArrayLength(), Is.EqualTo(2));
            Assert.That(root.GetProperty("supportingSources").GetArrayLength(), Is.EqualTo(1));
            Assert.That(
                root.GetProperty("leanArtifact").GetProperty("sha256").GetString(),
                Is.EqualTo(Convert.ToHexString(SHA256.HashData(firstLean)).ToLowerInvariant()));
            Assert.That(adapter.GetProperty("address").GetUInt32(), Is.EqualTo(4));
            Assert.That(adapter.GetProperty("name").GetString(), Is.EqualTo("ID"));
            Assert.That(adapter.GetProperty("supportsCaching").GetBoolean(), Is.False);
            Assert.That(adapter.GetProperty("runExpression").GetString(), Is.EqualTo("inputData.ToArray()"));
            Assert.That(generatedLean, Does.Contain("def dataGasCost"));
            Assert.That(generatedLean, Does.Not.Contain("theorem"));
        }
    }

    [TestCase("15UL", "16UL", TestName = "Rejects_base_price_mutation")]
    [TestCase("3UL *", "4UL *", TestName = "Rejects_word_price_mutation")]
    [TestCase("+ 31UL", "+ 30UL", TestName = "Rejects_ceiling_mutation")]
    [TestCase(">> 5", ">> 4", TestName = "Rejects_word_size_mutation")]
    public void Kernel_semantic_mutations_are_rejected(string original, string replacement)
    {
        string source = ReadProductionFile(SourceRelativePath);
        Assert.That(source, Does.Contain(original));
        using Fixture fixture = new(source.Replace(original, replacement, StringComparison.Ordinal));

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractIdentityPrecompile(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("Identity-precompile"));
    }

    [Test]
    public void Kernel_conditional_compilation_is_rejected()
    {
        string source = ReadProductionFile(SourceRelativePath);
        const string original = "public static ulong BaseGasCost() => 15UL;";
        const string replacement = """
            #if RELEASE
                public static ulong BaseGasCost() => 16UL;
            #else
                public static ulong BaseGasCost() => 15UL;
            #endif
            """;
        Assert.That(source, Does.Contain(original));
        using Fixture fixture = new(source.Replace(original, replacement, StringComparison.Ordinal));

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractIdentityPrecompile(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("preprocessor"));
    }

    [Test]
    public void Kernel_partial_type_is_rejected()
    {
        string source = ReadProductionFile(SourceRelativePath);
        Assert.That(source, Does.Contain("internal static class IdentityPrecompileKernel"));
        using Fixture fixture = new(source.Replace(
            "internal static class IdentityPrecompileKernel",
            "internal static partial class IdentityPrecompileKernel",
            StringComparison.Ordinal));

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractIdentityPrecompile(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("IdentityPrecompileKernel"));
    }

    [TestCase("Address.FromNumber(4)", "Address.FromNumber(5)", TestName = "Rejects_address_mutation")]
    [TestCase(
        "namespace Nethermind.Evm.Precompiles;",
        "namespace Nethermind.Evm.Other;",
        TestName = "Rejects_namespace_mutation")]
    [TestCase("public class IdentityPrecompile", "internal class IdentityPrecompile", TestName = "Rejects_visibility_mutation")]
    [TestCase("private IdentityPrecompile()", "public IdentityPrecompile()", TestName = "Rejects_constructor_mutation")]
    [TestCase("public ulong BaseGasCost", "public uint BaseGasCost", TestName = "Rejects_signature_mutation")]
    [TestCase("=> \"ID\"", "=> \"NOT-ID\"", TestName = "Rejects_name_mutation")]
    [TestCase("=> false", "=> true", TestName = "Rejects_cache_mutation")]
    [TestCase(
        "IdentityPrecompileKernel.DataGasCost((uint)inputData.Length)",
        "IdentityPrecompileKernel.DataGasCost((uint)inputData.Length + 1U)",
        TestName = "Rejects_data_adapter_mutation")]
    [TestCase("=> inputData.ToArray()", "=> Array.Empty<byte>()", TestName = "Rejects_output_mutation")]
    public void Adapter_semantic_mutations_are_rejected(string original, string replacement)
    {
        string source = ReadProductionFile(SourceRelativePath);
        string adapter = ReadProductionFile(AdapterSourceRelativePath);
        Assert.That(adapter, Does.Contain(original));
        using Fixture fixture = new(
            source,
            adapter.Replace(original, replacement, StringComparison.Ordinal));

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractIdentityPrecompile(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("Identity-precompile adapter"));
    }

    [Test]
    public void Adapter_conditional_compilation_is_rejected()
    {
        string source = ReadProductionFile(SourceRelativePath);
        string adapter = ReadProductionFile(AdapterSourceRelativePath);
        const string original =
            "IdentityPrecompileKernel.DataGasCost((uint)inputData.Length)";
        string replacement = """
            #if RELEASE
                    IdentityPrecompileKernel.DataGasCost((uint)inputData.Length + 65U)
            #else
                    IdentityPrecompileKernel.DataGasCost((uint)inputData.Length)
            #endif
            """;
        Assert.That(adapter, Does.Contain(original));
        using Fixture fixture = new(
            source,
            adapter.Replace(original, replacement, StringComparison.Ordinal));

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractIdentityPrecompile(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("preprocessor"));
    }

    [Test]
    public void Adapter_symbol_shadow_is_rejected()
    {
        string source = ReadProductionFile(SourceRelativePath) + """

            public readonly struct ReadOnlyMemory<T>
            {
                public int Length => 0;
                public byte[] ToArray() => [];
            }
            """;
        using Fixture fixture = new(source);

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractIdentityPrecompile(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("did not bind to the pinned target"));
    }

    [Test]
    public void Adapter_partial_type_is_rejected()
    {
        string source = ReadProductionFile(SourceRelativePath);
        string adapter = ReadProductionFile(AdapterSourceRelativePath);
        Assert.That(adapter, Does.Contain("public class IdentityPrecompile"));
        using Fixture fixture = new(
            source,
            adapter.Replace(
                "public class IdentityPrecompile",
                "public partial class IdentityPrecompile",
                StringComparison.Ordinal));

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractIdentityPrecompile(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("Identity-precompile adapter"));
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

        throw new FileNotFoundException("Could not locate the production identity-precompile source.", relativePath);
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(string source, string? adapter = null)
        {
            Root = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "identity-precompile-extractor-fixtures",
                Guid.NewGuid().ToString("N"));
            string sourcePath = Path.Combine(Root, SourceRelativePath);
            string adapterPath = Path.Combine(Root, AdapterSourceRelativePath);
            Output = Path.Combine(Root, "generated");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllText(sourcePath, source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.WriteAllText(
                adapterPath,
                adapter ?? ReadProductionFile(AdapterSourceRelativePath),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        public string Root { get; }

        public string Output { get; }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
