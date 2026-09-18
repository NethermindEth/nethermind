// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.Extractor.Test;

[TestFixture]
public class TransactionSettlementExtractorTests
{
    private const string SourceRelativePath =
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionSettlementKernel.cs";
    private const string BlockDependencyRelativePath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/Eip8037BlockGasInclusionCheck.cs";
    private const string UInt64DependencyRelativePath =
        "src/Nethermind/Nethermind.Core/Extensions/UInt64Extensions.cs";

    [Test]
    public void Extracts_settlement_kernel_deterministically_with_pinned_dependencies()
    {
        using Fixture fixture = Fixture.FromProduction();

        ExtractionResult first = StateGasChargeExtractor.ExtractTransactionSettlement(fixture.Root, fixture.Output);
        byte[] firstIr = File.ReadAllBytes(first.IrPath);
        byte[] firstManifest = File.ReadAllBytes(first.ManifestPath);
        byte[] firstLean = File.ReadAllBytes(first.LeanPath);
        ExtractionResult second = StateGasChargeExtractor.ExtractTransactionSettlement(fixture.Root, fixture.Output);

        using JsonDocument manifest = JsonDocument.Parse(firstManifest);
        JsonElement root = manifest.RootElement;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(second.IrPath), Is.EqualTo(firstIr));
            Assert.That(File.ReadAllBytes(second.ManifestPath), Is.EqualTo(firstManifest));
            Assert.That(File.ReadAllBytes(second.LeanPath), Is.EqualTo(firstLean));
            Assert.That(first.MethodCount, Is.EqualTo(1));
            Assert.That(root.GetProperty("rootSignatures").GetArrayLength(), Is.EqualTo(1));
            Assert.That(root.GetProperty("supportingSources").GetArrayLength(), Is.EqualTo(2));
            Assert.That(
                root.GetProperty("leanArtifact").GetProperty("sha256").GetString(),
                Is.EqualTo(Convert.ToHexString(SHA256.HashData(firstLean)).ToLowerInvariant()));
            Assert.That(File.ReadAllText(first.LeanPath), Does.Contain("def calculateNormalized"));
            Assert.That(File.ReadAllText(first.LeanPath), Does.Contain("def wrapInt32"));
        }
    }

    [TestCase(
        "Math.Min(unchecked((long)(gasUsedBeforeRefund / refundQuotient)), totalToRefund)",
        "Math.Max(unchecked((long)(gasUsedBeforeRefund / refundQuotient)), totalToRefund)",
        TestName = "refund cap mutation is rejected")]
    [TestCase(
        "refund >= 0",
        "refund > 0",
        TestName = "signed refund boundary mutation is rejected")]
    [TestCase(
        "if (isEip8037Enabled)",
        "if (isEip7778Enabled)",
        TestName = "fork projection mutation is rejected")]
    public void Rejects_semantic_source_mutations(string original, string replacement)
    {
        using Fixture fixture = Fixture.FromProduction(
            settlementMutation: source => source.Replace(original, replacement, StringComparison.Ordinal));

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractTransactionSettlement(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Is.Not.Empty);
    }

    [Test]
    public void Rejects_result_field_rebinding()
    {
        using Fixture fixture = Fixture.FromProduction(settlementMutation: source => source
            .Replace("public readonly ulong SpentGas = spentGas;", "public readonly ulong SpentGas = operationGas;", StringComparison.Ordinal)
            .Replace("public readonly ulong OperationGas = operationGas;", "public readonly ulong OperationGas = spentGas;", StringComparison.Ordinal));

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractTransactionSettlement(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("corresponding primary-constructor parameters"));
    }

    [Test]
    public void Rejects_changed_saturating_subtraction_dependency()
    {
        using Fixture fixture = Fixture.FromProduction(uint64Mutation: source => source.Replace(
            "a > b ? a - b : 0UL",
            "a >= b ? a - b : 0UL",
            StringComparison.Ordinal));

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractTransactionSettlement(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("saturating subtraction dependency"));
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string settlement, string blockDependency, string uint64Dependency)
        {
            Root = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "transaction-settlement-extractor-fixtures",
                Guid.NewGuid().ToString("N"));
            Output = Path.Combine(Root, "generated");
            Write(SourceRelativePath, settlement);
            Write(BlockDependencyRelativePath, blockDependency);
            Write(UInt64DependencyRelativePath, uint64Dependency);
        }

        public string Root { get; }

        public string Output { get; }

        public static Fixture FromProduction(
            Func<string, string>? settlementMutation = null,
            Func<string, string>? uint64Mutation = null)
        {
            string settlement = ReadProduction(SourceRelativePath);
            string blockDependency = ReadProduction(BlockDependencyRelativePath);
            string uint64Dependency = ReadProduction(UInt64DependencyRelativePath);
            return new Fixture(
                settlementMutation?.Invoke(settlement) ?? settlement,
                blockDependency,
                uint64Mutation?.Invoke(uint64Dependency) ?? uint64Dependency);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private void Write(string relativePath, string content)
        {
            string path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        private static string ReadProduction(string relativePath)
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

            throw new FileNotFoundException("Could not locate pinned production source.", relativePath);
        }
    }
}
