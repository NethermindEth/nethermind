// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.Extractor.Test;

[TestFixture]
public class BlockReceiptGasAccountingExtractorTests
{
    private const string KernelSourceRelativePath =
        "src/Nethermind/Nethermind.Blockchain/Tracing/BlockReceiptGasAccountingKernel.cs";
    private const string TracerSourceRelativePath =
        "src/Nethermind/Nethermind.Blockchain/Tracing/BlockReceiptsTracer.cs";
    private const string EthereumGasPolicySourceRelativePath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs";
    private const string TransactionGasInitializationSourceRelativePath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/TransactionGasInitializationKernel.cs";

    [Test]
    public void Extracts_block_receipt_gas_accounting_deterministically_with_pinned_tracer_delegations()
    {
        using Fixture fixture = Fixture.FromProduction();

        ExtractionResult first = StateGasChargeExtractor.ExtractBlockReceiptGasAccounting(fixture.Root, fixture.Output);
        byte[] firstIr = File.ReadAllBytes(first.IrPath);
        byte[] firstManifest = File.ReadAllBytes(first.ManifestPath);
        byte[] firstLean = File.ReadAllBytes(first.LeanPath);
        ExtractionResult second = StateGasChargeExtractor.ExtractBlockReceiptGasAccounting(fixture.Root, fixture.Output);

        using JsonDocument manifest = JsonDocument.Parse(firstManifest);
        JsonElement manifestRoot = manifest.RootElement;
        string[] signatures = manifestRoot.GetProperty("rootSignatures")
            .EnumerateArray()
            .Select(static signature => signature.GetString()!)
            .ToArray();
        JsonElement[] supportingSources = manifestRoot.GetProperty("supportingSources").EnumerateArray().ToArray();
        using JsonDocument ir = JsonDocument.Parse(firstIr);
        JsonElement[] delegations = ir.RootElement.GetProperty("program")
            .GetProperty("productionDelegations")
            .EnumerateArray()
            .ToArray();
        string generatedLean = File.ReadAllText(first.LeanPath);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(second.IrPath), Is.EqualTo(firstIr));
            Assert.That(File.ReadAllBytes(second.ManifestPath), Is.EqualTo(firstManifest));
            Assert.That(File.ReadAllBytes(second.LeanPath), Is.EqualTo(firstLean));
            Assert.That(first.MethodCount, Is.EqualTo(2));
            Assert.That(signatures, Has.Length.EqualTo(2));
            Assert.That(signatures, Has.Some.Contains("BlockReceiptGasAccountingKernel.Accumulate(ulong previousExecutionGas"));
            Assert.That(signatures, Has.Some.Contains("BlockReceiptGasAccountingKernel.FromTotals(ulong cumulativeExecutionGas"));
            Assert.That(
                supportingSources.Select(static source => source.GetProperty("path").GetString()),
                Is.EquivalentTo(
                    new[]
                    {
                        TracerSourceRelativePath.Replace('\\', '/'),
                        EthereumGasPolicySourceRelativePath.Replace('\\', '/'),
                        TransactionGasInitializationSourceRelativePath.Replace('\\', '/'),
                    }));
            Assert.That(delegations, Has.Length.EqualTo(2));
            Assert.That(
                delegations.Single(static delegation => delegation.GetProperty("methodName").GetString() == "UpdateCumulativeGasTracking")
                    .GetProperty("kernelMethod").GetString(),
                Is.EqualTo("Accumulate"));
            Assert.That(
                delegations.Single(static delegation => delegation.GetProperty("methodName").GetString() == "UpdateCumulativeGasTracking")
                    .GetProperty("headerUpdateGuard").GetString(),
                Is.EqualTo("!parallel"));
            Assert.That(
                delegations.Single(static delegation => delegation.GetProperty("methodName").GetString() == "Restore")
                    .GetProperty("kernelMethod").GetString(),
                Is.EqualTo("FromTotals"));
            Assert.That(
                manifestRoot.GetProperty("leanArtifact").GetProperty("sha256").GetString(),
                Is.EqualTo(Convert.ToHexString(SHA256.HashData(firstLean)).ToLowerInvariant()));
            Assert.That(generatedLean, Does.Contain("import Eip803x.Generated.TransactionGasInitializationKernel"));
            Assert.That(generatedLean, Does.Contain("def combineBlockGas"));
            Assert.That(generatedLean, Does.Contain("def accumulateNormalized"));
            Assert.That(generatedLean, Does.Contain("def fromTotalsNormalized"));
            Assert.That(SemanticBody(generatedLean), Does.Not.Contain("max "));
        }
    }

    [TestCase(
        "unchecked(previousExecutionGas + transactionExecutionGas)",
        "unchecked(previousExecutionGas + transactionStateGas)",
        TestName = "Execution_counter_operand_mutation_changes_Lean")]
    [TestCase(
        "unchecked(previousReceiptGas + transactionPaidGas)",
        "unchecked(previousReceiptGas + transactionStateGas)",
        TestName = "Receipt_counter_operand_mutation_changes_Lean")]
    public void Kernel_semantic_mutations_change_generated_Lean(string original, string replacement)
    {
        using Fixture baseline = Fixture.FromProduction();
        using Fixture mutated = Fixture.FromProduction(kernelMutation: source => source.Replace(
            original,
            replacement,
            StringComparison.Ordinal));

        ExtractionResult baselineResult = StateGasChargeExtractor.ExtractBlockReceiptGasAccounting(
            baseline.Root,
            baseline.Output);
        ExtractionResult mutatedResult = StateGasChargeExtractor.ExtractBlockReceiptGasAccounting(
            mutated.Root,
            mutated.Output);

        Assert.That(
            SemanticBody(File.ReadAllText(mutatedResult.LeanPath)),
            Is.Not.EqualTo(SemanticBody(File.ReadAllText(baselineResult.LeanPath))));
    }

    [Test]
    public void Rejects_checked_counter_arithmetic()
    {
        using Fixture fixture = Fixture.FromProduction(kernelMutation: source => source.Replace(
            "unchecked(previousExecutionGas + transactionExecutionGas)",
            "checked(previousExecutionGas + transactionExecutionGas)",
            StringComparison.Ordinal));

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractBlockReceiptGasAccounting(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("checked arithmetic"));
    }

    [Test]
    public void Rejects_changed_shared_block_gas_combine_semantics()
    {
        using Fixture fixture = Fixture.FromProduction(transactionGasInitializationMutation: source => source.Replace(
            "Math.Max(blockExecutionGas, blockStateGas)",
            "Math.Min(blockExecutionGas, blockStateGas)",
            StringComparison.Ordinal));

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractBlockReceiptGasAccounting(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("must retain the pinned Math.Max delegation"));
    }

    [Test]
    public void Rejects_ethereum_policy_combine_bypass()
    {
        using Fixture fixture = Fixture.FromProduction(ethereumGasPolicyMutation: source => source.Replace(
            "BlockGasAccountingKernel.Combine(blockExecutionGas, blockStateGas)",
            "0UL",
            StringComparison.Ordinal));

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractBlockReceiptGasAccounting(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("must retain the pinned BlockGasAccountingKernel.Combine delegation"));
    }

    [Test]
    public void Rejects_tracer_header_projection_bypass()
    {
        using Fixture fixture = Fixture.FromProduction(tracerMutation: source => source.Replace(
            "Block.Header.GasUsed = accounting.HeaderGasUsed;",
            "Block.Header.GasUsed = accounting.CumulativeReceiptGas;",
            StringComparison.Ordinal));

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractBlockReceiptGasAccounting(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("must assign Header.GasUsed from the accounting result"));
    }

    [Test]
    public void Rejects_tracer_restore_kernel_bypass()
    {
        using Fixture fixture = Fixture.FromProduction(tracerMutation: source => source.Replace(
            "BlockReceiptGasAccountingKernel.FromTotals(",
            "BlockReceiptGasAccountingKernel.Accumulate(",
            StringComparison.Ordinal));

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractBlockReceiptGasAccounting(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("must construct its pinned accounting result"));
    }

    [Test]
    public void Rejects_unpinned_accounting_root()
    {
        using Fixture fixture = Fixture.FromProduction(kernelMutation: source => source.Replace(
            "internal static class BlockReceiptGasAccountingKernel\n{",
            "internal static class BlockReceiptGasAccountingKernel\n{\n    public static BlockReceiptGasAccountingResult Extra(ulong value) => default;",
            StringComparison.Ordinal));

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractBlockReceiptGasAccounting(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("pinned accounting method set"));
    }

    private static string SemanticBody(string generatedLean)
    {
        const string NamespaceMarker = "namespace Eip803x.Generated.BlockReceiptGasAccountingKernel";
        int start = generatedLean.IndexOf(NamespaceMarker, StringComparison.Ordinal);
        return start < 0
            ? throw new AssertionException("Generated Lean namespace marker was not found.")
            : generatedLean[start..];
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string kernel, string tracer, string ethereumGasPolicy, string transactionGasInitialization)
        {
            Root = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "block-receipt-gas-accounting-extractor-fixtures",
                Guid.NewGuid().ToString("N"));
            Output = Path.Combine(Root, "generated");
            Write(KernelSourceRelativePath, kernel);
            Write(TracerSourceRelativePath, tracer);
            Write(EthereumGasPolicySourceRelativePath, ethereumGasPolicy);
            Write(TransactionGasInitializationSourceRelativePath, transactionGasInitialization);
        }

        public string Root { get; }

        public string Output { get; }

        public static Fixture FromProduction(
            Func<string, string>? kernelMutation = null,
            Func<string, string>? tracerMutation = null,
            Func<string, string>? ethereumGasPolicyMutation = null,
            Func<string, string>? transactionGasInitializationMutation = null)
        {
            string kernel = ReadProduction(KernelSourceRelativePath);
            string tracer = ReadProduction(TracerSourceRelativePath);
            string ethereumGasPolicy = ReadProduction(EthereumGasPolicySourceRelativePath);
            string transactionGasInitialization = ReadProduction(TransactionGasInitializationSourceRelativePath);
            return new Fixture(
                kernelMutation?.Invoke(kernel) ?? kernel,
                tracerMutation?.Invoke(tracer) ?? tracer,
                ethereumGasPolicyMutation?.Invoke(ethereumGasPolicy) ?? ethereumGasPolicy,
                transactionGasInitializationMutation?.Invoke(transactionGasInitialization) ?? transactionGasInitialization);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private void Write(string relativePath, string source)
        {
            string path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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
