// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.OrdinaryStaticAdmissionExtractor.Test;

[TestFixture]
public sealed class ExtractorTests
{
    [Test]
    public void Production_sources_extract_deterministically()
    {
        string root = FindRepoRoot();
        using TemporaryDirectory first = new();
        using TemporaryDirectory second = new();
        ExtractionResult firstResult = Extractor.Extract(root, first.Path,
            Path.Combine(first.Path, Extractor.LeanFileName));
        ExtractionResult secondResult = Extractor.Extract(root, second.Path,
            Path.Combine(second.Path, Extractor.LeanFileName));

        Assert.That(firstResult.SourceCount, Is.EqualTo(20));
        Assert.That(firstResult.BranchCount, Is.EqualTo(12));
        Assert.That(File.ReadAllBytes(firstResult.IrPath), Is.EqualTo(File.ReadAllBytes(secondResult.IrPath)));
        Assert.That(File.ReadAllBytes(firstResult.ManifestPath), Is.EqualTo(File.ReadAllBytes(secondResult.ManifestPath)));
        Assert.That(File.ReadAllBytes(firstResult.LeanPath), Is.EqualTo(File.ReadAllBytes(secondResult.LeanPath)));
    }

    [Test]
    public void Checked_in_artifacts_match_fresh_extraction()
    {
        string root = FindRepoRoot();
        using TemporaryDirectory temporary = new();
        ExtractionResult result = Extractor.Extract(root, temporary.Path,
            Path.Combine(temporary.Path, Extractor.LeanFileName));
        string checkedIn = Path.Combine(root, Extractor.PackageRelativePath, "Generated");

        Assert.That(File.ReadAllBytes(result.IrPath), Is.EqualTo(File.ReadAllBytes(
            Path.Combine(checkedIn, Extractor.IrFileName))));
        Assert.That(File.ReadAllBytes(result.ManifestPath), Is.EqualTo(File.ReadAllBytes(
            Path.Combine(checkedIn, Extractor.ManifestFileName))));
        Assert.That(File.ReadAllBytes(result.LeanPath), Is.EqualTo(File.ReadAllBytes(
            Path.Combine(checkedIn, Extractor.LeanFileName))));
    }

    [Test]
    public void Generated_Lean_is_theorem_free_and_exposes_both_pure_helpers()
    {
        string root = FindRepoRoot();
        string package = Path.Combine(root, Extractor.PackageRelativePath);
        string lean = File.ReadAllText(Path.Combine(package, "Generated", Extractor.LeanFileName));
        string reference = File.ReadAllText(Path.Combine(package, "Reference", "OrdinaryStaticAdmissionReference.lean"));

        Assert.That(lean, Does.Contain("def validateStatic"));
        Assert.That(lean, Does.Contain("def calculateAvailableGas"));
        Assert.That(lean, Does.Contain("def defaultAvailablePolicy"));
        Assert.That(lean, Does.Contain("def policyFromInitialization"));
        Assert.That(lean, Does.Contain("import Eip803x.Generated.TransactionGasInitializationKernel"));
        Assert.That(lean, Does.Not.Contain("theorem "));
        Assert.That(lean, Does.Not.Contain("sorry"));
        Assert.That(lean, Does.Not.Contain("admit"));
        Assert.That(lean, Does.Not.Contain("axiom "));
        foreach (string field in new[] { "value", "stateReservoir", "stateGasUsed", "stateGasSpill", "stateGasSpillRefunded" })
        {
            Assert.That(lean, Does.Contain($"{field} :="));
            Assert.That(reference, Does.Contain($"{field} :="));
        }
    }

    [Test]
    public void Typed_ir_preserves_ordered_paths_and_legacy_fallthrough()
    {
        IrDocument document = Extractor.BuildForTest(FindRepoRoot()).Document;
        string[] expected =
        [
            "sender-absent", "nonce-overflow", "initcode-oversize", "setcode-creation",
            "setcode-auth-empty", "intrinsic-cap", "execution-intrinsic", "floor-intrinsic",
            "minimum-intrinsic", "eip8037-block-limit", "legacy-block-limit", "ok",
        ];
        int[] depths = [1, 1, 1, 2, 2, 1, 1, 1, 1, 3, 3, 0];

        Assert.That(document.Branches.Select(branch => branch.Id), Is.EqualTo(expected));
        Assert.That(document.Branches.Select(branch => branch.GuardPath.Length), Is.EqualTo(depths));
        IrBranch legacy = document.Branches.Single(branch => branch.Id == "legacy-block-limit");
        Assert.That(legacy.GuardPath.Count(guard => guard.IsSyntheticFallthrough), Is.EqualTo(1));
        Assert.That(legacy.GuardPath[1].Source, Does.Contain("IsEip8037Enabled"));
        Assert.That(document.CallMappings.Single().OutputFields, Is.EqualTo(
            new[] { "Value", "StateReservoir", "StateGasUsed", "StateGasSpill", "StateGasSpillRefunded" }));
    }

    [Test]
    public void Supported_comparison_mutation_changes_executable_Lean_body()
    {
        using Fixture fixture = new();
        byte[] original = Extractor.BuildForTest(fixture.Root).LeanBytes;
        fixture.Replace(Extractor.ProcessorPath,
            "if (tx.GasLimit < standardGasUsed)", "if (tx.GasLimit <= standardGasUsed)");
        byte[] mutated = Extractor.BuildForTest(fixture.Root).LeanBytes;

        Assert.That(mutated, Is.Not.EqualTo(original));
        Assert.That(Encoding.UTF8.GetString(mutated), Does.Contain(
            "input.txGasLimit <= normalizeUInt64 input.standardValue"));
    }

    [TestCase("if (tx.GasLimit < standardGasUsed)", "if (standardGasUsed < tx.GasLimit)")]
    [TestCase("if (tx.GasLimit < floorGasUsed)", "if (floorGasUsed < tx.GasLimit)")]
    [TestCase("if (tx.GasLimit < minGasRequired)", "if (minGasRequired < tx.GasLimit)")]
    [TestCase("if (tx.GasLimit > header.GasLimit)", "if (header.GasLimit > tx.GasLimit)")]
    [TestCase("if (tx.GasLimit > maxTransactionGasLimit)", "if (maxTransactionGasLimit > tx.GasLimit)")]
    public void Comparison_operand_order_mutations_fail_closed(string original, string replacement)
    {
        using Fixture fixture = new();
        fixture.Replace(Extractor.ProcessorPath, original, replacement);
        AssertRejected(fixture);
    }

    [Test]
    public void Manifest_semantic_hash_is_the_embedded_semantic_ir_hash()
    {
        (IrDocument document, _, _, byte[] manifestBytes) = Extractor.BuildForTest(FindRepoRoot());
        using JsonDocument manifest = JsonDocument.Parse(manifestBytes);
        Assert.That(manifest.RootElement.GetProperty("semanticIrSha256").GetString(),
            Is.EqualTo(document.IrSha256));
    }

    [TestCase("GetRemainingGas(in EthereumGasPolicy gas) => gas.Value;",
        "GetRemainingGas(in EthereumGasPolicy gas) => gas.Value + 1;")]
    [TestCase("GetStateReservoir(in EthereumGasPolicy gas) => gas.StateReservoir;",
        "GetStateReservoir(in EthereumGasPolicy gas) => gas.StateReservoir + 1;")]
    public void Ethereum_policy_projection_mutations_fail_closed(string original, string replacement)
    {
        using Fixture fixture = new();
        fixture.Replace(Extractor.GasPolicyPath, original, replacement);
        AssertRejected(fixture);
    }

    [TestCase("TGasPolicy standard = intrinsicGas.Standard;", "TGasPolicy standard = intrinsicGas.FloorGas;")]
    [TestCase("TGasPolicy floorGas = intrinsicGas.FloorGas;", "TGasPolicy floorGas = intrinsicGas.Standard;")]
    [TestCase("ulong standardGasUsed = TGasPolicy.GetRemainingGas(in standard);",
        "ulong standardGasUsed = TGasPolicy.GetRemainingGas(in floorGas);")]
    [TestCase("ulong floorGasUsed = TGasPolicy.GetRemainingGas(in floorGas);",
        "ulong floorGasUsed = TGasPolicy.GetRemainingGas(in standard);")]
    [TestCase("ulong minGasRequired = intrinsicGas.MinRequiredGasLimit;",
        "ulong minGasRequired = intrinsicGas.StandardGas;")]
    public void Static_intrinsic_local_mapping_mutations_fail_closed(string original, string replacement)
    {
        using Fixture fixture = new();
        fixture.Replace(Extractor.ProcessorPath, original, replacement);
        AssertRejected(fixture);
    }

    [TestCase("return ValidateGas(tx, header, spec, in standard, minGasRequired, validate);",
        "return ValidateGas(tx, header, spec, in standard, minGasRequired, !validate);")]
    [TestCase("intrinsicGas.ExceedsCap(Eip7825Constants.DefaultTxGasLimitCap, out ulong execution, out ulong floor)",
        "intrinsicGas.ExceedsCap(Eip7825Constants.DefaultTxGasLimitCap, out ulong floor, out ulong execution)")]
    [TestCase("ulong gasUsedForAllowance = _parallel ? 0 : header.GasUsed;",
        "ulong gasUsedForAllowance = _parallel ? header.GasUsed : 0;")]
    public void Static_bridge_and_allowance_dataflow_mutations_fail_closed(string original, string replacement)
    {
        using Fixture fixture = new();
        fixture.Replace(Extractor.ProcessorPath, original, replacement);
        AssertRejected(fixture);
    }

    [Test]
    public void Calculate_available_gas_polarity_mutation_fails_closed()
    {
        using Fixture fixture = new();
        fixture.Replace(Extractor.ProcessorPath,
            "? TransactionResult.Ok\n                : TransactionResult.GasLimitBelowIntrinsicGas",
            "? TransactionResult.GasLimitBelowIntrinsicGas\n                : TransactionResult.Ok");
        AssertRejected(fixture);
    }

    [TestCase("result.Outcome is TransactionGasInitializationOutcome.IntrinsicGasExceedsLimit",
        "result.Outcome is TransactionGasInitializationOutcome.Success")]
    [TestCase("StateReservoir = result.StateReservoir,", "StateReservoir = result.StateGasUsed,")]
    public void Available_policy_outcome_and_field_mutations_fail_closed(string original, string replacement)
    {
        using Fixture fixture = new();
        fixture.Replace(Extractor.GasPolicyPath, original, replacement);
        AssertRejected(fixture);
    }

    [TestCase("public bool SupportsAuthorizationList => Type.SupportsAuthorizationList();",
        "public bool SupportsAuthorizationList => Type.SupportsAuthorizationList() && false;")]
    [TestCase("tx.IsAboveInitCode(spec)", "tx.IsAboveInitCode(null)")]
    public void Transaction_projection_and_call_mapping_mutations_fail_closed(string original, string replacement)
    {
        using Fixture fixture = new();
        fixture.Replace(original.StartsWith("public", StringComparison.Ordinal)
            ? Extractor.TransactionPath
            : Extractor.ProcessorPath, original, replacement);
        AssertRejected(fixture);
    }

    [TestCase("execution = TGasPolicy.GetRemainingGas(in standard);", "execution = 0;")]
    [TestCase("floor = TGasPolicy.GetRemainingGas(in floorGas);", "floor = 0;")]
    public void Intrinsic_cap_projection_mutations_fail_closed(string original, string replacement)
    {
        using Fixture fixture = new();
        fixture.Replace(Extractor.GasPolicyInterfacePath, original, replacement);
        AssertRejected(fixture);
    }

    [Test]
    public void Validation_result_boolean_mutation_fails_closed()
    {
        using Fixture fixture = new();
        fixture.Replace(Extractor.ValidationResultPath, "public bool AsBool() => Error is null;",
            "public bool AsBool() => Error is not null;");
        AssertRejected(fixture);
    }

    [TestCase("BlockGasLimitExceeded = new(ErrorType.BlockGasLimitExceeded",
        "BlockGasLimitExceeded = new(ErrorType.GasLimitBelowIntrinsicGas")]
    [TestCase("new(errorType, errorDescription: detail)",
        "new(errorType, evmException: EvmExceptionType.OutOfGas, errorDescription: detail)")]
    public void Transaction_result_mapping_mutations_fail_closed(string original, string replacement)
    {
        using Fixture fixture = new();
        fixture.Replace(Extractor.ProcessorPath, original, replacement);
        AssertRejected(fixture);
    }

    [Test]
    public void Transaction_instance_extension_shadow_fails_closed()
    {
        using Fixture fixture = new();
        fixture.Replace(Extractor.TransactionPath,
            "public partial class Transaction\n    {",
            "public partial class Transaction\n    {\n        public bool IsAboveInitCode(Nethermind.Core.Specs.IReleaseSpec spec) => false;");
        AssertRejected(fixture);
    }

    [Test]
    public void Canonical_initializer_source_identity_rejects_unregenerated_source()
    {
        using Fixture fixture = new();
        fixture.Replace(Extractor.InitializationPath,
            "if (gasLimit < intrinsicTotal)", "if (intrinsicTotal < gasLimit)");
        AssertRejected(fixture);
    }

    [Test]
    public void Standard_processor_helper_overrides_fail_closed()
    {
        const string injected =
            "\n        protected override TransactionResult ValidateGas() => TransactionResult.Ok;";

        using Fixture generic = new();
        generic.Replace(Extractor.ProcessorPath,
            "where TGasPolicy : struct, IGasPolicy<TGasPolicy>\n    {\n    }",
            $"where TGasPolicy : struct, IGasPolicy<TGasPolicy>\n    {{{injected}\n    }}");
        AssertRejected(generic);

        using Fixture synchronous = new();
        synchronous.Replace(Extractor.ProcessorPath,
            ": EthereumTransactionProcessorBase(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager, parallel);\n\n    public class BlobBaseFeeCalculator",
            $": EthereumTransactionProcessorBase(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager, parallel)\n    {{{injected}\n    }}\n\n    public class BlobBaseFeeCalculator");
        AssertRejected(synchronous);

        using Fixture synchronousBase = new();
        synchronousBase.Replace(Extractor.ProcessorPath,
            ": TransactionProcessorBase<EthereumGasPolicy>(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager, parallel);\n\n    public readonly struct TransactionResult",
            $": TransactionProcessorBase<EthereumGasPolicy>(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager, parallel)\n    {{{injected}\n    }}\n\n    public readonly struct TransactionResult");
        AssertRejected(synchronousBase);
    }

    [TestCase("if (validate && tx.Nonce == ulong.MaxValue)", "if (validate || tx.Nonce == ulong.MaxValue)",
        TestName = "nonce connective mutation")]
    [TestCase("return execution > cap || floor > cap;", "return execution > cap && floor > cap;",
        TestName = "cap connective mutation")]
    [TestCase("tx.IsContractCreation && spec.IsEip3860Enabled && tx.DataLength > spec.MaxInitCodeSize",
        "tx.IsContractCreation || spec.IsEip3860Enabled && tx.DataLength > spec.MaxInitCodeSize",
        TestName = "initcode connective mutation")]
    public void Supported_guard_connective_mutations_fail_closed(string original, string replacement)
    {
        using Fixture fixture = new();
        string path = original.StartsWith("return", StringComparison.Ordinal)
            ? Extractor.GasPolicyInterfacePath
            : original.StartsWith("tx.IsContractCreation", StringComparison.Ordinal)
                ? Extractor.TransactionExtensionsPath
                : Extractor.ProcessorPath;
        fixture.Replace(path, original, replacement);
        AssertRejected(fixture);
    }

    [TestCase("if (tx.SenderAddress is null)", "if ((tx.SenderAddress is null) | true)")]
    [TestCase("if (tx.IsAboveInitCode(spec))", "if (tx.IsAboveInitCode(spec) | true)")]
    [TestCase("if (tx.SupportsAuthorizationList)", "if (tx.SupportsAuthorizationList | true)")]
    [TestCase("if (!noCreation)", "if (!noCreation | true)")]
    [TestCase("if (!authList)", "if (!authList ^ true)")]
    [TestCase("if (validate)", "if (validate | true)")]
    [TestCase("if (spec.IsEip8037Enabled)", "if (spec.IsEip8037Enabled | true)")]
    [TestCase(
        "if (spec.IsEip8037Enabled && intrinsicGas.ExceedsCap(Eip7825Constants.DefaultTxGasLimitCap, out ulong execution, out ulong floor))",
        "if (spec.IsEip8037Enabled && intrinsicGas.ExceedsCap(Eip7825Constants.DefaultTxGasLimitCap, out ulong execution, out ulong floor) | true)")]
    public void Exact_guard_expression_mutations_fail_closed(string original, string replacement)
    {
        using Fixture fixture = new();
        fixture.Replace(Extractor.ProcessorPath, original, replacement);
        AssertRejected(fixture);
    }

    [Test]
    public void SetCode_validator_mapping_and_null_empty_mutations_fail_closed()
    {
        using Fixture noCreation = new();
        noCreation.Replace(Extractor.SetCodeValidationPath,
            "transaction.IsContractCreation\n            ? TxErrorMessages.NotAllowedCreateTransaction\n            : ValidationResult.Success",
            "transaction.IsContractCreation\n            ? ValidationResult.Success\n            : TxErrorMessages.NotAllowedCreateTransaction");
        AssertRejected(noCreation);

        using Fixture authorization = new();
        authorization.Replace(Extractor.SetCodeValidationPath,
            "null or { Length: 0 }", "null or { Length: 1 }");
        AssertRejected(authorization);
    }

    [Test]
    public void SkipValidation_is_used_only_by_nonce_and_block_limit_guards()
    {
        string lean = Encoding.UTF8.GetString(Extractor.BuildForTest(FindRepoRoot()).LeanBytes);
        Assert.That(lean, Does.Contain("let validate := !input.skipValidation"));
        Assert.That(lean, Does.Contain("if validate && input.nonce == uint64Max"));
        Assert.That(lean, Does.Contain("if validate && input.eip8037Enabled"));
        Assert.That(lean, Does.Contain("if validate && !input.eip8037Enabled"));
        Assert.That(lean, Does.Contain("if isAboveInitCode input"));
        Assert.That(lean, Does.Contain("if input.eip8037Enabled &&"));
        Assert.That(lean, Does.Not.Contain("validate && isAboveInitCode"));
        Assert.That(lean, Does.Not.Contain("validate && input.eip8037Enabled && ("));
    }

    [Test]
    public void Bridge_and_initializer_argument_or_field_mutations_fail_closed()
    {
        using Fixture bridge = new();
        bridge.Replace(Extractor.GasPolicyPath,
            "intrinsicGas.StateReservoir,",
            "intrinsicGas.Value,");
        AssertRejected(bridge);

        using Fixture resultField = new();
        resultField.Replace(Extractor.InitializationPath,
            "public readonly long StateGasSpill = stateGasSpill;",
            "public readonly long StateGasSpillChanged = stateGasSpill;");
        AssertRejected(resultField);
    }

    [Test]
    public void Return_mapping_and_initializer_arithmetic_mutations_fail_closed()
    {
        using Fixture returnMapping = new();
        returnMapping.Replace(Extractor.ProcessorPath,
            "TransactionResult.ErrorType.GasLimitBelowFloorGas.WithDetail(",
            "TransactionResult.ErrorType.GasLimitBelowIntrinsicGas.WithDetail(");
        AssertRejected(returnMapping);

        using Fixture arithmetic = new();
        arithmetic.Replace(Extractor.InitializationPath,
            "unchecked(intrinsicExecutionGas + unchecked((ulong)intrinsicStateGas))",
            "unchecked(intrinsicExecutionGas - unchecked((ulong)intrinsicStateGas))");
        AssertRejected(arithmetic);
    }

    [TestCase("return TransactionResult.Ok;",
        "return true ? TransactionResult.BlockGasLimitExceeded : TransactionResult.Ok;")]
    [TestCase("return TransactionResult.BlockGasLimitExceeded;",
        "return true ? TransactionResult.Ok : TransactionResult.BlockGasLimitExceeded;")]
    [TestCase("return TransactionResult.SenderNotSpecified;",
        "return true ? TransactionResult.NonceOverflow : TransactionResult.SenderNotSpecified;")]
    public void Wrapped_return_mapping_mutations_fail_closed(string original, string replacement)
    {
        using Fixture fixture = new();
        fixture.Replace(Extractor.ProcessorPath, original, replacement);
        AssertRejected(fixture);
    }

    [Test]
    public void Gas_cap_literal_mutation_fails_closed()
    {
        using Fixture fixture = new();
        fixture.Replace(Extractor.GasLimitCapPath,
            "DefaultTxGasLimitCap = 16_777_216;",
            "DefaultTxGasLimitCap = 16_777_215;\n    // DefaultTxGasLimitCap = 16_777_216;");
        AssertRejected(fixture);
    }

    [TestCase("public enum TxType : byte", "public enum TxType : int")]
    [TestCase("SetCode = 4", "SetCode = 5")]
    public void Transaction_type_width_and_value_mutations_fail_closed(string original, string replacement)
    {
        using Fixture fixture = new();
        fixture.Replace(Extractor.TransactionTypePath, original, replacement);
        AssertRejected(fixture);
    }

    [TestCase(Extractor.TransactionPath, "public ulong Nonce { get; set; }", "public uint Nonce { get; set; }")]
    [TestCase(Extractor.TransactionPath, "public ulong GasLimit { get; set; }", "public uint GasLimit { get; set; }")]
    [TestCase(Extractor.TransactionPath, "public int DataLength => Data.Length;", "public long DataLength => Data.Length;")]
    [TestCase(Extractor.BlockHeaderPath, "public ulong GasUsed { get; set; }", "public uint GasUsed { get; set; }")]
    [TestCase(Extractor.ReleaseSpecPath, "long MaxCodeSize { get; }", "int MaxCodeSize { get; }")]
    [TestCase(Extractor.ProcessorPath, "private readonly bool _parallel;", "private readonly int _parallel;")]
    [TestCase(Extractor.GasPolicyPath, "public ulong Value;", "public uint Value;")]
    [TestCase(Extractor.GasPolicyPath,
        "public static long GetStateReservoir(in EthereumGasPolicy gas)",
        "public static int GetStateReservoir(in EthereumGasPolicy gas)")]
    [TestCase(Extractor.TransactionPath, "public TxType Type { get; set; }", "public byte Type { get; set; }")]
    [TestCase(Extractor.TransactionPath,
        "public AuthorizationTuple[]? AuthorizationList { get; set; }",
        "public AuthorizationTuple? AuthorizationList { get; set; }")]
    [TestCase(Extractor.TransactionPath, "public Address? To { get; set; }", "public object? To { get; set; }")]
    [TestCase(Extractor.TransactionPath,
        "public Address? SenderAddress { get; set; }", "public object? SenderAddress { get; set; }")]
    [TestCase(Extractor.ReleaseSpecPath,
        "bool IsEip3860Enabled { get; }", "int IsEip3860Enabled { get; }")]
    [TestCase(Extractor.ReleaseSpecPath,
        "public bool IsEip8037Enabled { get; }", "public int IsEip8037Enabled { get; }")]
    [TestCase(Extractor.ReleaseSpecExtensionsPath,
        "public long MaxInitCodeSize =>", "public int MaxInitCodeSize =>")]
    [TestCase(Extractor.BlockHeaderPath,
        "public ulong GasLimit { get; set; }", "public uint GasLimit { get; set; }")]
    [TestCase(Extractor.GasPolicyPath, "public long StateReservoir;", "public int StateReservoir;")]
    [TestCase(Extractor.GasPolicyPath, "public long StateGasUsed;", "public int StateGasUsed;")]
    [TestCase(Extractor.GasPolicyPath, "public long StateGasSpill;", "public int StateGasSpill;")]
    [TestCase(Extractor.GasPolicyPath,
        "public long StateGasSpillRefunded;", "public int StateGasSpillRefunded;")]
    [TestCase(Extractor.GasPolicyPath,
        "public static ulong GetRemainingGas(in EthereumGasPolicy gas)",
        "public static uint GetRemainingGas(in EthereumGasPolicy gas)")]
    [TestCase(Extractor.GasPolicyPath,
        "TryCreateAvailableFromIntrinsic(ulong gasLimit, in EthereumGasPolicy intrinsicGas",
        "TryCreateAvailableFromIntrinsic(uint gasLimit, in EthereumGasPolicy intrinsicGas")]
    [TestCase(Extractor.GasPolicyInterfacePath,
        "public ulong StandardGas =>", "public uint StandardGas =>")]
    [TestCase(Extractor.GasPolicyInterfacePath,
        "public ulong MinRequiredGasLimit =>", "public uint MinRequiredGasLimit =>")]
    [TestCase(Extractor.GasPolicyInterfacePath,
        "public bool ExceedsCap(ulong cap, out ulong execution, out ulong floor)",
        "public int ExceedsCap(ulong cap, out ulong execution, out ulong floor)")]
    [TestCase(Extractor.InitializationPath,
        "TransactionGasInitializationOutcome outcome,\n    ulong value,",
        "TransactionGasInitializationOutcome outcome,\n    uint value,")]
    [TestCase(Extractor.InitializationPath,
        "public readonly ulong Value = value;", "public readonly uint Value = value;")]
    [TestCase(Extractor.InitializationPath,
        "public readonly long StateGasSpillRefunded = stateGasSpillRefunded;",
        "public readonly int StateGasSpillRefunded = stateGasSpillRefunded;")]
    [TestCase(Extractor.InitializationPath,
        "long intrinsicStateGas,\n        bool eip8037Enabled,",
        "int intrinsicStateGas,\n        bool eip8037Enabled,")]
    [TestCase(Extractor.InitializationPath,
        "enum TransactionGasInitializationOutcome : byte",
        "enum TransactionGasInitializationOutcome : sbyte")]
    public void Fixed_width_source_mutations_fail_closed(string path, string original, string replacement)
    {
        using Fixture fixture = new();
        fixture.Replace(path, original, replacement);
        AssertRejected(fixture);
    }

    [TestCase("BlockGasLimitExceeded = new(ErrorType.BlockGasLimitExceeded, errorDescription:",
        "BlockGasLimitExceeded = new(ErrorType.BlockGasLimitExceeded, EvmExceptionType.OutOfGas, errorDescription:")]
    [TestCase("GasLimitBelowIntrinsicGas = new(ErrorType.GasLimitBelowIntrinsicGas, errorDescription:",
        "GasLimitBelowIntrinsicGas = new(ErrorType.GasLimitBelowIntrinsicGas, evmException: EvmExceptionType.OutOfGas, errorDescription:")]
    public void Static_result_evm_exception_mutations_fail_closed(string original, string replacement)
    {
        using Fixture fixture = new();
        fixture.Replace(Extractor.ProcessorPath, original, replacement);
        AssertRejected(fixture);
    }

    [Test]
    public void Transaction_result_constructor_extra_assignment_fails_closed()
    {
        using Fixture fixture = new();
        fixture.Replace(Extractor.ProcessorPath,
            "EvmExceptionType = evmException;",
            "EvmExceptionType = evmException;\n            EvmExceptionType = EvmExceptionType.OutOfGas;");
        AssertRejected(fixture);
    }

    [Test]
    public void Validation_result_string_conversion_mutation_fails_closed()
    {
        using Fixture fixture = new();
        fixture.Replace(Extractor.ValidationResultPath,
            "implicit operator ValidationResult(string error) => new(error)",
            "implicit operator ValidationResult(string error) => new(null)");
        AssertRejected(fixture);
    }

    [TestCase(Extractor.InitializationPath,
        "    Success,\n    IntrinsicGasExceedsLimit,",
        "    Success,\n    IntrinsicGasExceedsLimit = 0,")]
    [TestCase(Extractor.ProcessorPath,
        "            None,\n            BlockGasLimitExceeded,\n            GasLimitBelowIntrinsicGas,",
        "            None,\n            BlockGasLimitExceeded = 0,\n            GasLimitBelowIntrinsicGas,")]
    [TestCase(Extractor.EvmExceptionPath, "None = 0,", "None = -1,")]
    public void Modeled_enum_alias_mutations_fail_closed(string path, string original, string replacement)
    {
        using Fixture fixture = new();
        fixture.Replace(path, original, replacement);
        AssertRejected(fixture);
    }

    [Test]
    public void Serialized_artifact_validation_is_structural_after_independent_deserialization()
    {
        string package = Path.Combine(FindRepoRoot(), Extractor.PackageRelativePath, "Generated");
        byte[] ir = File.ReadAllBytes(Path.Combine(package, Extractor.IrFileName));
        byte[] manifest = File.ReadAllBytes(Path.Combine(package, Extractor.ManifestFileName));

        JsonObject irObject = JsonNode.Parse(ir)!.AsObject();
        JsonObject manifestObject = JsonNode.Parse(manifest)!.AsObject();
        Extractor.ValidateSerializedIr(ir, Encoding.UTF8.GetBytes(irObject.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
        Extractor.ValidateSerializedManifest(manifest,
            Encoding.UTF8.GetBytes(manifestObject.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
    }

    [Test]
    public void Serialized_identity_and_semantic_tampering_is_rejected()
    {
        string package = Path.Combine(FindRepoRoot(), Extractor.PackageRelativePath, "Generated");
        byte[] ir = File.ReadAllBytes(Path.Combine(package, Extractor.IrFileName));
        byte[] manifest = File.ReadAllBytes(Path.Combine(package, Extractor.ManifestFileName));

        JsonObject irMutation = JsonNode.Parse(ir)!.AsObject();
        irMutation["branches"]!.AsArray()[5]! ["comparisonOperator"] = "LessThan";
        Assert.Throws<ExtractionException>(() => Extractor.ValidateSerializedIr(ir,
            Encoding.UTF8.GetBytes(irMutation.ToJsonString(new JsonSerializerOptions { WriteIndented = true }))));

        JsonObject manifestMutation = JsonNode.Parse(manifest)!.AsObject();
        manifestMutation["sources"]!.AsArray()[0]! ["sha256"] = new string('0', 64);
        Assert.Throws<ExtractionException>(() => Extractor.ValidateSerializedManifest(manifest,
            Encoding.UTF8.GetBytes(manifestMutation.ToJsonString(new JsonSerializerOptions { WriteIndented = true }))));
    }

    [Test]
    public void Fixed_width_wrap_and_all_initializer_fields_are_emitted_and_pinned()
    {
        string package = Path.Combine(FindRepoRoot(), Extractor.PackageRelativePath);
        string generated = File.ReadAllText(Path.Combine(package, "Generated", Extractor.LeanFileName));
        string reference = File.ReadAllText(Path.Combine(package, "Reference", "OrdinaryStaticAdmissionReference.lean"));
        foreach (string operation in new[] { "normalizeUInt64", "addUInt64", "subUInt64", "int64ToUInt64", "uint64ToInt64" })
        {
            Assert.That(generated, Does.Contain(operation));
            Assert.That(reference, Does.Contain(operation));
        }
        Assert.That(generated, Does.Contain("defaultAvailablePolicy"));
        Assert.That(generated, Does.Contain("calculateAvailableGasFailure"));
        Assert.That(generated, Does.Contain("calculateAvailableGasSuccess"));
    }

    [Test]
    public void Check_mode_accepts_checked_in_artifacts()
    {
        string root = FindRepoRoot();
        string package = Path.Combine(root, Extractor.PackageRelativePath);
        Extractor.ValidateExistingArtifacts(root, Path.Combine(package, "Generated"),
            Path.Combine(package, "Generated", Extractor.LeanFileName));
    }

    private static void AssertRejected(Fixture fixture) =>
        Assert.Throws<ExtractionException>(() => Extractor.BuildForTest(fixture.Root));

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, Extractor.ProcessorPath)))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the formal Nethermind repository root.");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _productionRoot = FindRepoRoot();

        public Fixture()
        {
            Root = Path.Combine(TestContext.CurrentContext.WorkDirectory,
                "ordinary-static-admission-fixtures", Guid.NewGuid().ToString("N"));
            foreach (string relativePath in Extractor.SourcePaths.Concat(new[]
                     {
                         Extractor.CanonicalInitializerIrPath,
                         Extractor.CanonicalInitializerManifestPath,
                         Extractor.CanonicalInitializerGeneratedPath,
                         Extractor.CanonicalInitializerRefinementPath,
                         Extractor.CanonicalTransactionGasPath,
                     }))
            {
                string source = Path.Combine(_productionRoot, relativePath);
                string destination = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllBytes(destination, File.ReadAllBytes(source));
            }
        }

        public string Root { get; }

        public void Replace(string relativePath, string original, string replacement)
        {
            string path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            string source = File.ReadAllText(path);
            Assert.That(source, Does.Contain(original), $"Fixture anchor missing in {relativePath}.");
            File.WriteAllText(path, source.Replace(original, replacement, StringComparison.Ordinal),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory,
                "ordinary-static-admission-output", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
