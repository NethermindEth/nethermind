// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.Extractor.Test;

[TestFixture]
public class StateGasTransitionAdapterExtractorTests
{
    private const string AdapterSourceRelativePath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionAdapterKernel.cs";
    private const string TransitionSourceRelativePath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionKernel.cs";
    private const string EthereumGasPolicySourceRelativePath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs";

    [Test]
    public void Extracts_state_gas_transition_adapter_deterministically_with_pinned_delegation()
    {
        using Fixture fixture = Fixture.FromProduction();

        ExtractionResult first = StateGasChargeExtractor.ExtractStateGasTransitionAdapter(fixture.Root, fixture.Output);
        byte[] firstIr = File.ReadAllBytes(first.IrPath);
        byte[] firstManifest = File.ReadAllBytes(first.ManifestPath);
        byte[] firstLean = File.ReadAllBytes(first.LeanPath);
        ExtractionResult second = StateGasChargeExtractor.ExtractStateGasTransitionAdapter(fixture.Root, fixture.Output);

        using JsonDocument manifest = JsonDocument.Parse(firstManifest);
        JsonElement root = manifest.RootElement;
        string[] signatures = root.GetProperty("rootSignatures")
            .EnumerateArray()
            .Select(static signature => signature.GetString()!)
            .ToArray();
        JsonElement[] supportingSources = root.GetProperty("supportingSources").EnumerateArray().ToArray();
        using JsonDocument ir = JsonDocument.Parse(firstIr);
        JsonElement[] delegations = ir.RootElement.GetProperty("program")
            .GetProperty("productionDelegations")
            .EnumerateArray()
            .ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(second.IrPath), Is.EqualTo(firstIr));
            Assert.That(File.ReadAllBytes(second.ManifestPath), Is.EqualTo(firstManifest));
            Assert.That(File.ReadAllBytes(second.LeanPath), Is.EqualTo(firstLean));
            Assert.That(first.MethodCount, Is.EqualTo(9));
            Assert.That(signatures, Has.Length.EqualTo(9));
            Assert.That(signatures, Has.Some.Contains("StateGasTransitionAdapterKernel.RemoveStateGasRefundFromReservoir"));
            Assert.That(supportingSources.Select(static source => source.GetProperty("path").GetString()), Is.EquivalentTo(
                new[]
                {
                    TransitionSourceRelativePath.Replace('\\', '/'),
                    EthereumGasPolicySourceRelativePath.Replace('\\', '/'),
                }));
            Assert.That(delegations, Has.Length.EqualTo(9));
            Assert.That(delegations.Single(static delegation =>
                    delegation.GetProperty("name").GetString() == "DiscardStateGas")
                .GetProperty("returnsUnappliedAmount").GetBoolean(), Is.True);
            Assert.That(delegations.Single(static delegation =>
                    delegation.GetProperty("name").GetString() == "RemoveStateGasRefundFromReservoir")
                .GetProperty("preservesNegativeAmountException").GetBoolean(), Is.True);
            Assert.That(
                root.GetProperty("leanArtifact").GetProperty("sha256").GetString(),
                Is.EqualTo(Convert.ToHexString(SHA256.HashData(firstLean)).ToLowerInvariant()));
            Assert.That(File.ReadAllText(first.LeanPath), Does.Contain("inductive OutcomeKind"));
            Assert.That(File.ReadAllText(first.LeanPath), Does.Contain("def removeStateGasRefundFromReservoir"));
            Assert.That(File.ReadAllText(first.LeanPath), Does.Contain("if amount < 0 then"));
        }
    }

    [TestCase(
        "amount < 0",
        "amount <= 0",
        TestName = "Negative_amount_boundary_mutation_is_rejected")]
    [TestCase(
        "StateGasTransitionAdapterOutcomeKind.CompletedDiscard",
        "StateGasTransitionAdapterOutcomeKind.CompletedVoid",
        TestName = "Discard_outcome_mutation_is_rejected")]
    [TestCase(
        "StateGasTransitionKernel.RepayStateGasSpill(",
        "StateGasTransitionKernel.Refund(",
        TestName = "Core_call_mutation_is_rejected")]
    public void Adapter_semantic_mutations_are_rejected(string original, string replacement)
    {
        using Fixture fixture = Fixture.FromProduction(adapterMutation: source =>
            source.Replace(original, replacement, StringComparison.Ordinal));

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractStateGasTransitionAdapter(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Not.Contain("did not retain exactly"));
    }

    [Test]
    public void Rejects_production_adapter_bypass()
    {
        using Fixture fixture = Fixture.FromProduction(ethereumGasPolicyMutation: source => source.Replace(
            "StateGasTransitionAdapterKernel.RepayStateGasSpill(",
            "StateGasTransitionKernel.RepayStateGasSpill(",
            StringComparison.Ordinal));

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractStateGasTransitionAdapter(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("does not delegate exactly once"));
    }

    [Test]
    public void Rejects_missing_pre_mutation_exception_path()
    {
        using Fixture fixture = Fixture.FromProduction(ethereumGasPolicyMutation: source => source.Replace(
            "_ = Math.Clamp(gas.StateReservoir, 0, amount);",
            "_ = amount;",
            StringComparison.Ordinal));

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractStateGasTransitionAdapter(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("pre-mutation ArgumentException"));
    }

    [Test]
    public void Rejects_extra_production_adapter_statement()
    {
        using Fixture fixture = Fixture.FromProduction(ethereumGasPolicyMutation: source => source.Replace(
            "ApplyStateGasTransition(ref gas, in outcome);",
            "ApplyStateGasTransition(ref gas, in outcome);\n        System.GC.KeepAlive(outcome);",
            StringComparison.Ordinal));

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractStateGasTransitionAdapter(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("statements outside the pinned value-boundary shape"));
    }

    [Test]
    public void Rejects_unpinned_adapter_root()
    {
        using Fixture fixture = Fixture.FromProduction(adapterMutation: source => source.Replace(
            "internal static class StateGasTransitionAdapterKernel\n{",
            "internal static class StateGasTransitionAdapterKernel\n{\n    public static StateGasTransitionAdapterOutcome Extra(ulong value) => default;",
            StringComparison.Ordinal));

        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => StateGasChargeExtractor.ExtractStateGasTransitionAdapter(fixture.Root, fixture.Output))!;

        Assert.That(exception.Message, Does.Contain("pinned adapter method set"));
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string adapter, string transition, string ethereumGasPolicy)
        {
            Root = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "state-gas-transition-adapter-extractor-fixtures",
                Guid.NewGuid().ToString("N"));
            Output = Path.Combine(Root, "generated");
            Write(AdapterSourceRelativePath, adapter);
            Write(TransitionSourceRelativePath, transition);
            Write(EthereumGasPolicySourceRelativePath, ethereumGasPolicy);
        }

        public string Root { get; }

        public string Output { get; }

        public static Fixture FromProduction(
            Func<string, string>? adapterMutation = null,
            Func<string, string>? ethereumGasPolicyMutation = null)
        {
            string adapter = ReadProduction(AdapterSourceRelativePath);
            string transition = ReadProduction(TransitionSourceRelativePath);
            string ethereumGasPolicy = ReadProduction(EthereumGasPolicySourceRelativePath);
            return new Fixture(
                adapterMutation?.Invoke(adapter) ?? adapter,
                transition,
                ethereumGasPolicyMutation?.Invoke(ethereumGasPolicy) ?? ethereumGasPolicy);
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
