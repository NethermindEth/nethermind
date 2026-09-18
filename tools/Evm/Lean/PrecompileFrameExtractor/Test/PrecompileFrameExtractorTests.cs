// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;
using NJsonSchema;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Nethermind.Evm.Lean.PrecompileFrameExtractor.Test;

[TestFixture]
internal sealed class PrecompileFrameExtractorTests
{
    private static string RepositoryRoot => FindRepositoryRoot(TestContext.CurrentContext.TestDirectory);

    [Test]
    public void StandardBuildProducesDeterministicArtifacts()
    {
        string root = RepositoryRoot;
        string first = CreateDirectory();
        string second = CreateDirectory();

        ExtractionResult firstResult = PrecompileFrameProfile.Extract(root, first);
        ExtractionResult secondResult = PrecompileFrameProfile.Extract(root, second);

        Assert.That(firstResult.RouteCount, Is.EqualTo(18));
        Assert.That(firstResult.SourceCount, Is.EqualTo(122));
        Assert.That(firstResult.MemberCount, Is.EqualTo(154));
        Assert.That(firstResult.DependencyCount, Is.GreaterThan(0));
        Assert.That(File.ReadAllBytes(firstResult.IrPath), Is.EqualTo(File.ReadAllBytes(secondResult.IrPath)));
        Assert.That(File.ReadAllBytes(firstResult.ManifestPath), Is.EqualTo(File.ReadAllBytes(secondResult.ManifestPath)));
        Assert.That(File.ReadAllBytes(firstResult.LeanPath), Is.EqualTo(File.ReadAllBytes(secondResult.LeanPath)));
        Assert.That(File.ReadAllBytes(firstResult.StageB.IrPath), Is.EqualTo(File.ReadAllBytes(secondResult.StageB.IrPath)));
        Assert.That(File.ReadAllBytes(firstResult.StageB.ManifestPath), Is.EqualTo(File.ReadAllBytes(secondResult.StageB.ManifestPath)));
        Assert.That(File.ReadAllBytes(firstResult.StageB.LeanPath), Is.EqualTo(File.ReadAllBytes(secondResult.StageB.LeanPath)));

        string generated = Path.Combine(root, "tools", "Evm", "Lean", "PrecompileFrameExtractor", "Generated");
        Assert.That(File.ReadAllBytes(firstResult.IrPath), Is.EqualTo(File.ReadAllBytes(Path.Combine(generated, "precompile-frame-stage-a.ir.json"))));
        Assert.That(File.ReadAllBytes(firstResult.ManifestPath), Is.EqualTo(File.ReadAllBytes(Path.Combine(generated, "precompile-frame-stage-a.source-manifest.json"))));
        Assert.That(File.ReadAllBytes(firstResult.LeanPath), Is.EqualTo(File.ReadAllBytes(Path.Combine(generated, "PrecompileFrameStageA.lean"))));
        Assert.That(File.ReadAllBytes(firstResult.StageB.IrPath), Is.EqualTo(File.ReadAllBytes(Path.Combine(generated, "precompile-frame-stage-b.ir.json"))));
        Assert.That(File.ReadAllBytes(firstResult.StageB.ManifestPath), Is.EqualTo(File.ReadAllBytes(Path.Combine(generated, "precompile-frame-stage-b.source-manifest.json"))));
        Assert.That(File.ReadAllBytes(firstResult.StageB.LeanPath), Is.EqualTo(File.ReadAllBytes(Path.Combine(generated, "PrecompileFrameStageB.lean"))));
    }

    [Test]
    public void GeneratedArtifactsMatchTheirSchemas()
    {
        string root = RepositoryRoot;
        string output = CreateDirectory();
        ExtractionResult result = PrecompileFrameProfile.Extract(root, output);

        JsonSchema irSchema = JsonSchema.FromJsonAsync(File.ReadAllText(Path.Combine(
            root, "tools", "Evm", "Lean", "PrecompileFrameExtractor", "Schema", "precompile-frame-stage-a.ir.schema.json")))
            .GetAwaiter().GetResult();
        JsonSchema manifestSchema = JsonSchema.FromJsonAsync(File.ReadAllText(Path.Combine(
            root, "tools", "Evm", "Lean", "PrecompileFrameExtractor", "Schema", "precompile-frame-stage-a.source-manifest.schema.json")))
            .GetAwaiter().GetResult();
        JsonSchema stageBIrSchema = JsonSchema.FromJsonAsync(File.ReadAllText(Path.Combine(
            root, "tools", "Evm", "Lean", "PrecompileFrameExtractor", "Schema", "precompile-frame-stage-b.ir.schema.json")))
            .GetAwaiter().GetResult();
        JsonSchema stageBManifestSchema = JsonSchema.FromJsonAsync(File.ReadAllText(Path.Combine(
            root, "tools", "Evm", "Lean", "PrecompileFrameExtractor", "Schema", "precompile-frame-stage-b.source-manifest.schema.json")))
            .GetAwaiter().GetResult();

        Assert.That(irSchema.Validate(File.ReadAllText(result.IrPath)), Is.Empty);
        Assert.That(manifestSchema.Validate(File.ReadAllText(result.ManifestPath)), Is.Empty);
        Assert.That(stageBIrSchema.Validate(File.ReadAllText(result.StageB.IrPath)), Is.Empty);
        Assert.That(stageBManifestSchema.Validate(File.ReadAllText(result.StageB.ManifestPath)), Is.Empty);
    }

    [Test]
    public void StageBOperationalIrIsUniversalOracleBoundedAndSourceClosed()
    {
        ExtractionResult result = PrecompileFrameProfile.Extract(RepositoryRoot, CreateDirectory());
        using JsonDocument ir = JsonDocument.Parse(File.ReadAllText(result.StageB.IrPath));
        JsonElement root = ir.RootElement;

        Assert.That(root.GetProperty("acceptanceState").GetString(), Is.EqualTo(
            "stage-b-universal-oracle-bounded-operational-refinement"));
        Assert.That(root.GetProperty("operational").GetProperty("branches").GetArrayLength(), Is.EqualTo(9));
        Assert.That(root.GetProperty("oracleBindings").GetArrayLength(), Is.EqualTo(18));
        Assert.That(root.GetProperty("sources").GetArrayLength(), Is.EqualTo(10));
        Assert.That(root.GetProperty("members").GetArrayLength(), Is.EqualTo(24));
        Assert.That(root.GetProperty("mutationChecks").GetArrayLength(), Is.EqualTo(28));
        Assert.That(root.GetProperty("stageA").GetProperty("sourceCount").GetInt32(), Is.EqualTo(122));
        Assert.That(root.GetProperty("stageA").GetProperty("memberCount").GetInt32(), Is.EqualTo(154));
        Assert.That(root.GetProperty("stageA").GetProperty("dependencyCount").GetInt32(), Is.EqualTo(23));
        Assert.That(root.GetProperty("refinement").GetProperty("theorem").GetString(), Is.EqualTo(
            "source_execute_precompile_refines_reference"));
    }

    [Test]
    public void StageBConcreteEthereumGasPolicyClosureIsPinned()
    {
        ExtractionResult result = PrecompileFrameProfile.Extract(RepositoryRoot, CreateDirectory());
        using JsonDocument ir = JsonDocument.Parse(File.ReadAllText(result.StageB.IrPath));
        JsonElement root = ir.RootElement;
        string[] sources = root.GetProperty("sources").EnumerateArray()
            .Select(source => source.GetProperty("path").GetString()!)
            .ToArray();
        (string Source, string Member)[] members = root.GetProperty("members").EnumerateArray()
            .Select(member => (
                member.GetProperty("sourcePath").GetString()!,
                member.GetProperty("member").GetString()!))
            .ToArray();

        Assert.That(sources, Does.Contain("src/Nethermind/Nethermind.Evm/GasPolicy/PrecompileGasPricingKernel.cs"));
        Assert.That(members, Does.Contain(("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs", "CreateChildFrameGas")));
        Assert.That(members, Does.Contain(("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs", "TryConsumePrecompileGas")));
        Assert.That(members, Does.Contain(("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs", "ClearExecutionGas")));
        Assert.That(members, Does.Contain(("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs", "Refund")));
        Assert.That(members, Does.Contain(("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs", "RestoreChildStateGasOnHalt")));
        Assert.That(members, Does.Contain(("src/Nethermind/Nethermind.Evm/GasPolicy/PrecompileGasPricingKernel.cs", "TryConsume")));
        Assert.That(members, Does.Contain(("src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionKernel.cs", "Refund")));
        Assert.That(members, Does.Contain(("src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionKernel.cs", "RestoreChildStateGasOnHalt")));
    }

    [Test]
    public void StageBOutputCopyOutOfGasIsTypedAndHasNoWriteOrStackSuccess()
    {
        ExtractionResult result = PrecompileFrameProfile.Extract(RepositoryRoot, CreateDirectory());
        using JsonDocument ir = JsonDocument.Parse(File.ReadAllText(result.StageB.IrPath));
        JsonElement branch = ir.RootElement.GetProperty("operational").GetProperty("branches").EnumerateArray()
            .Single(candidate => candidate.GetProperty("name").GetString() == "outputCopyOutOfGas");

        Assert.That(branch.GetProperty("status").GetString(), Is.EqualTo("outputCopyOutOfGas"));
        Assert.That(branch.GetProperty("result").GetString(), Is.EqualTo("outOfGas"));
        Assert.That(branch.GetProperty("invokesOracle").GetBoolean(), Is.True);
        Assert.That(branch.GetProperty("refundsChildGas").GetBoolean(), Is.True);
        Assert.That(branch.GetProperty("writesOutput").GetBoolean(), Is.False);
        Assert.That(branch.GetProperty("pushesStack").GetBoolean(), Is.False);
        Assert.That(branch.GetProperty("effects").EnumerateArray().Select(effect => effect.GetString()).ToArray(), Is.EqualTo(
            ["inputLoad", "childCreate", "pricing", "runOracle", "accountTouch", "childRefund", "returnDataSet", "returnOutOfGas"]));
    }

    [Test]
    public void StageBReferenceAndGeneratedKernelRemainIndependent()
    {
        string reference = File.ReadAllText(Path.Combine(RepositoryRoot,
            "tools", "Evm", "Lean", "PrecompileFrameExtractor", "Specification", "PrecompileFrameStageBReference.lean"));
        string generatedEmitter = File.ReadAllText(Path.Combine(RepositoryRoot,
            "tools", "Evm", "Lean", "PrecompileFrameExtractor", "PrecompileFrameStageBLeanEmitter.cs"));
        string refinement = File.ReadAllText(Path.Combine(RepositoryRoot,
            "tools", "Evm", "Lean", "PrecompileFrameExtractor", "Refinement", "PrecompileFrameStageB.lean"));

        Assert.That(reference, Does.Not.Contain("PrecompileFrameStageB.Generated"));
        Assert.That(reference, Does.Not.Contain("PrecompileFrameStageB.lean"));
        Assert.That(reference, Does.Contain("abbrev Bytes := List UInt8"));
        Assert.That(reference, Does.Contain("inductive BodyDecision"));
        Assert.That(reference, Does.Contain("inductive DispatchDecision"));
        Assert.That(reference, Does.Contain("def decideBody"));
        Assert.That(reference, Does.Contain("def renderBodyDecision"));
        Assert.That(generatedEmitter, Does.Contain("abbrev Bytes := List UInt8"));
        Assert.That(generatedEmitter, Does.Not.Contain("inductive BodyDecision"));
        Assert.That(generatedEmitter, Does.Not.Contain("def renderBodyDecision"));
        Assert.That(generatedEmitter, Does.Not.Contain("File.ReadAllText(PrecompileFrameStageBProfile.ReferencePath"));
        Assert.That(refinement, Does.Contain("(oracle : G.LeafOracle) (input : G.Input)"));
        Assert.That(refinement, Does.Contain("G.executePrecompile oracle input"));
        Assert.That(refinement, Does.Contain("R.executePrecompile (toReferenceOracle oracle) (toReferenceInput input)"));
    }

    [TestCase(
        "tools/Evm/Lean/PrecompileFrameExtractor/Specification/PrecompileFrameStageBReference.lean",
        "def executePrecompile",
        "def executePrecompileMutated",
        TestName = "Stage B independent reference identity mutation is rejected")]
    [TestCase(
        "tools/Evm/Lean/PrecompileFrameExtractor/Specification/PrecompileFrameStageBReference.lean",
        "inductive BodyDecision",
        "inductive BodyDecisionMutated",
        TestName = "Stage B independent reference architecture mutation is rejected")]
    [TestCase(
        "tools/Evm/Lean/PrecompileFrameExtractor/Specification/PrecompileFrameStageBReference.lean",
        "renderBodyDecision input (decideBody oracle input)",
        "renderBodyDecision input .declined",
        TestName = "Stage B independent reference architecture bypass is rejected")]
    [TestCase(
        "tools/Evm/Lean/PrecompileFrameExtractor/Refinement/PrecompileFrameStageB.lean",
        "theorem source_execute_precompile_refines_reference",
        "theorem source_execute_precompile_refines_reference_mutated",
        TestName = "Stage B universal theorem identity mutation is rejected")]
    public void StageBProofIdentityMutationIsRejected(string relativePath, string original, string replacement)
    {
        string copy = CopyRepository();
        string path = Path.Combine(copy, relativePath.Replace('/', Path.DirectorySeparatorChar));
        string source = File.ReadAllText(path);
        Assert.That(source.Contains(original, StringComparison.Ordinal), Is.True);
        File.WriteAllText(path, source.Replace(original, replacement, StringComparison.Ordinal));

        Assert.That(() => PrecompileFrameProfile.Extract(copy, CreateDirectory()), Throws.TypeOf<ExtractionException>());
    }

    [TestCase("ir", TestName = "Stage B IR artifact mutation is rejected")]
    [TestCase("manifest", TestName = "Stage B manifest artifact mutation is rejected")]
    [TestCase("lean", TestName = "Stage B Lean artifact mutation is rejected")]
    public void StageBArtifactMutationIsRejected(string artifact)
    {
        string output = CreateDirectory();
        ExtractionResult result = PrecompileFrameProfile.Extract(RepositoryRoot, output);
        string path = artifact switch
        {
            "ir" => result.StageB.IrPath,
            "manifest" => result.StageB.ManifestPath,
            "lean" => result.StageB.LeanPath,
            _ => throw new ArgumentOutOfRangeException(nameof(artifact)),
        };
        File.AppendAllText(path, "mutated");

        Assert.That(() => PrecompileFrameStageBProfile.ValidateExistingArtifacts(
            RepositoryRoot,
            result.IrPath,
            result.ManifestPath,
            result.LeanPath,
            result.StageB.IrPath,
            result.StageB.ManifestPath,
            result.StageB.LeanPath), Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void ZkEvmSelectionIsRejected() => Assert.That(
        () => PrecompileFrameProfile.Extract(RepositoryRoot, CreateDirectory(), enableZkEvm: true),
        Throws.TypeOf<ExtractionException>());

    [TestCase(
        "src/Nethermind/Nethermind.Blockchain/EthereumPrecompileProvider.cs",
        "[ECRecoverPrecompile.Address] = new(ECRecoverPrecompile.Instance)",
        "[ECRecoverPrecompile.Address] = new(Sha256Precompile.Instance)",
        TestName = "01 provider entry swap is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Blockchain/EthereumPrecompileProvider.cs",
        "            [SecP256r1Precompile.Address] = new(SecP256r1Precompile.Instance),\n",
        "",
        TestName = "02 provider entry omission is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Blockchain/EthereumPrecompileProvider.cs",
        "            [SecP256r1Precompile.Address] = new(SecP256r1Precompile.Instance),\n",
        "            [SecP256r1Precompile.Address] = new(SecP256r1Precompile.Instance),\n            [SecP256r1Precompile.Address] = new(SecP256r1Precompile.Instance),\n",
        TestName = "02b duplicate provider entry is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Specs/ReleaseSpec.cs",
        "if (IsEip2537Enabled)",
        "if (IsEip4844Enabled)",
        TestName = "03 fork activation mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Core/Precompiles/PrecompiledAddresses.cs",
        "Address.FromNumber(0x0100)",
        "Address.FromNumber(0x0101)",
        TestName = "04 address initializer mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Core/Precompiles/PrecompiledAddresses.cs",
        "    public static readonly AddressAsKey P256Verify = Address.FromNumber(0x0100);\n",
        "    public static readonly AddressAsKey P256Verify = Address.FromNumber(0x0100);\n    public static readonly AddressAsKey P256VerifyCopy = Address.FromNumber(0x0100);\n",
        TestName = "04b duplicate address initializer is rejected")]
    [TestCase(
        "src/Nethermind/Directory.Build.targets",
        "Compile Remove=\"**/zkevm/**/*.cs\"",
        "Compile Remove=\"**/std/**/*.cs\"",
        TestName = "05 standard selector inversion is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Blockchain/EthereumPrecompileProvider.cs",
        "FrozenDictionary<AddressAsKey, CodeInfo>",
        "Dictionary<AddressAsKey, CodeInfo>",
        TestName = "06 wildcard provider shape is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Init/Modules/PrewarmerModule.cs",
        "AddDecorator<ICodeInfoRepository>",
        "AddDecorator<IPrecompileProvider>",
        TestName = "07 unknown cache decorator is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/CodeInfoRepository.cs",
        "private const int MaxIndexedNumber = 0x100;",
        "private const int MaxIndexedNumber = 0x101;",
        TestName = "08 low lookup cap mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/CodeInfoRepository.cs",
        "if (vmSpec.IsPrecompile(codeSource))",
        "if (!vmSpec.IsPrecompile(codeSource))",
        TestName = "09 precompile-before-delegation mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/State/PrecompileCaches.cs",
        "ReferenceEquals(Spec, other.Spec)",
        "true",
        TestName = "10 cache address/spec key mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/State/PrecompileCaches.cs",
        "Address == other.Address",
        "true",
        TestName = "11 cache address key mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Blockchain/PrecompileCachedCodeInfoRepository.cs",
        "ReadOnlyMemory<byte> effectiveInput = precompile.NormalizeInput(inputData);",
        "ReadOnlyMemory<byte> effectiveInput = inputData;",
        TestName = "12 cache normalization mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Blockchain/PrecompileCachedCodeInfoRepository.cs",
        "if (result is { IsError: true, Error: Errors.InvalidInputLength })",
        "if (false)",
        TestName = "13 invalid-input cache update mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/VirtualMachine.cs",
        "!codeSource.Equals(Ripemd160Address)",
        "true",
        TestName = "14 RIPEMD direct route mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.cs",
        "if (TOpCall.ExecutionType == ExecutionType.STATICCALL && codeInfo.Precompile is { } precompile &&",
        "if (TOpCall.ExecutionType == ExecutionType.CALL && codeInfo.Precompile is { } precompile &&",
        TestName = "15 direct route for CALL mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.std.cs",
        "if (!TGasPolicy.TryConsumePrecompileGas",
        "if (true || !TGasPolicy.TryConsumePrecompileGas",
        TestName = "16 direct pricing guard mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs",
        "            if (_txTracer.IsCancelled)\n                ThrowOperationCanceledException();\n",
        "",
        TestName = "17 cancellation poll omission is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs",
        "                if (exceptionType != EvmExceptionType.None ||\n                    (cancelableState.OpCodeCount & CancellationCheckMask) != 0 ||",
        "                if (exceptionType != EvmExceptionType.None &&\n                    (cancelableState.OpCodeCount & CancellationCheckMask) != 0 ||",
        TestName = "17b cancellation boundary OR-to-AND mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs",
        "exceptionType != EvmExceptionType.None",
        "exceptionType == EvmExceptionType.None",
        TestName = "17c cancellation exception polarity mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs",
        "(cancelableState.OpCodeCount & CancellationCheckMask) != 0",
        "(cancelableState.OpCodeCount & CancellationCheckMask) == 0",
        TestName = "17d cancellation batch polarity mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs",
        "(nuint)cancelableState.FinalProgramCounter >= (nuint)stack.CodeLength",
        "(nuint)cancelableState.FinalProgramCounter < (nuint)stack.CodeLength",
        TestName = "17e cancellation successor polarity mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs",
        "if (_txTracer.IsCancelled)\n                ThrowOperationCanceledException();",
        "if (!_txTracer.IsCancelled)\n                ThrowOperationCanceledException();",
        TestName = "17f cancellation poll polarity mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/VirtualMachine.cs",
        "failure = VirtualMachineStatics.PrecompileExecutionFailureException;",
        "failure = PrecompileOutOfGasException;",
        TestName = "18 returned failure classification mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/VirtualMachine.cs",
        "exceptionType: !success ? EvmExceptionType.PrecompileFailure : EvmExceptionType.None",
        "exceptionType: !success ? EvmExceptionType.OutOfGas : EvmExceptionType.None",
        TestName = "19 managed exception classification mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.std.cs",
        "vm.WorldState.AddToBalanceAndCreateIfNotExists(target, UInt256.Zero, spec);",
        "vm.ReturnDataBuffer = outputData;",
        TestName = "20 direct touch residue mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.std.cs",
        "vm.ReturnDataBuffer = outputData;",
        "vm.WorldState.AddToBalanceAndCreateIfNotExists(target, UInt256.Zero, spec);",
        TestName = "21 direct returndata order mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.std.cs",
        "if (!vm.VmState.Memory.TrySave(in outputOffset, outputData.Span[..copyLength]))",
        "if (vm.VmState.Memory.TrySave(in outputOffset, outputData.Span[..copyLength]))",
        TestName = "21b direct output-copy OOG polarity mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs",
        "        if (isViolation) return false;\n\n        UpdateSize(newLength, rentIfNeeded: false);",
        "        if (isViolation)\n        {\n            UpdateSize(newLength, rentIfNeeded: false);\n            return false;\n        }\n\n        UpdateSize(newLength, rentIfNeeded: false);",
        TestName = "21c TrySave mutation before bounds failure is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs",
        "        if (isViolation) return false;\n\n        UpdateSize(newLength, rentIfNeeded: false);",
        "        UpdateSize(newLength, rentIfNeeded: false);\n        if (isViolation) return false;\n\n        UpdateSize(newLength, rentIfNeeded: false);",
        TestName = "21c2 TrySave inserted pre-failure mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs",
        "Value = childExecutionGas,",
        "Value = childExecutionGas + 1,",
        TestName = "21d concrete child execution gas mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs",
        "long childStateReservoir = parentGas.StateReservoir;",
        "long childStateReservoir = 0;",
        TestName = "21e concrete child reservoir transfer mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs",
        "            StateGasUsed = 0,\n            StateGasSpill = 0,\n        };",
        "            StateGasUsed = 1,\n            StateGasSpill = 0,\n        };",
        TestName = "21f concrete child state-used zero mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs",
        "            StateGasUsed = 0,\n            StateGasSpill = 0,\n        };",
        "            StateGasUsed = 0,\n            StateGasSpill = 1,\n        };",
        TestName = "21g concrete child state-spill zero mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs",
        "            StateGasUsed = 0,\n            StateGasSpill = 0,\n        };",
        "            StateGasUsed = 0,\n            StateGasSpill = 0,\n            StateGasSpillRefunded = 1,\n        };",
        TestName = "21h concrete child refunded-spill default-zero mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs",
        "    public long StateGasSpillRefunded;\n",
        "    public long StateGasSpillRefunded;\n\n    public EthereumGasPolicy()\n    {\n        Value = 0;\n        StateReservoir = 0;\n        StateGasUsed = 0;\n        StateGasSpill = 0;\n        StateGasSpillRefunded = 1;\n    }\n",
        TestName = "21h2 concrete child default-constructor mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs",
        "gas.Value = result.RemainingGas;",
        "gas.Value = 0;",
        TestName = "21i concrete precompile pricing mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs",
        "public static void ClearExecutionGas(ref EthereumGasPolicy gas) => gas.Value = 0;",
        "public static void ClearExecutionGas(ref EthereumGasPolicy gas) => gas.StateReservoir = 0;",
        TestName = "21j concrete execution-gas clear mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionKernel.cs",
        "unchecked(stateReservoir + childStateReservoir),",
        "stateReservoir,",
        TestName = "21k concrete refund kernel mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionKernel.cs",
        "            parentValue,\n            unchecked(unchecked(unchecked(parentStateReservoir + childStateReservoir) + childStateGasUsed) - childNetSpill),",
        "            unchecked(parentValue + (ulong)childNetSpill),\n            unchecked(unchecked(unchecked(parentStateReservoir + childStateReservoir) + childStateGasUsed) - childNetSpill),",
        TestName = "21l concrete halt-restore kernel mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.std.cs",
        "        TGasPolicy.Refund(ref gas, in childGas);",
        "        TGasPolicy.Refund(ref gas, in childGas);\n        vm.ReturnDataBuffer = default;",
        TestName = "21p unmodeled direct-helper statement insertion is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.std.cs",
        "        TGasPolicy.Refund(ref gas, in childGas);",
        "        TGasPolicy.Refund(ref gas, in childGas);\n        gas.StateReservoir = 0;",
        TestName = "21p2 unmodeled direct-helper gas mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.std.cs",
        "        TGasPolicy.Refund(ref gas, in childGas);",
        "        TGasPolicy.Refund(ref gas, in childGas);\n        vm.WorldState.AddToBalanceAndCreateIfNotExists(target, UInt256.Zero, spec);",
        TestName = "21p3 unmodeled direct-helper world mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.std.cs",
        "        result = stack.PushBytes<TTracingInst>(StatusCode.SuccessBytes.Span);",
        "        result = stack.PushZero<TTracingInst, OnFlag>();",
        TestName = "21p4 direct-helper result mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs",
        "                pc = cancelableState.FinalProgramCounter;",
        "                pc = cancelableState.FinalProgramCounter;\n                pc = cancelableState.FinalProgramCounter;",
        TestName = "21q unmodeled cancellation-loop statement insertion is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs",
        "                Vm = this,\n            };\n\n            if (_txTracer.IsCancelled)",
        "                Vm = this,\n            };\n\n            cancelableState = new();\n\n            if (_txTracer.IsCancelled)",
        TestName = "21q2 unmodeled pre-dispatch statement insertion is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.std.cs",
        "        UInt256 dataLength,",
        "        ulong dataLength,",
        TestName = "21r direct-helper signature mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.cs",
        "        out EvmExceptionType result)\n        where TGasPolicy : struct, IGasPolicy<TGasPolicy>",
        "        out int result)\n        where TGasPolicy : struct, IGasPolicy<TGasPolicy>",
        TestName = "21s direct-helper partial declaration mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/GasPolicy/PrecompileGasPricingKernel.cs",
        "public readonly ulong RemainingGas = remainingGas;",
        "public readonly ulong RemainingGas = 0;",
        TestName = "21m concrete pricing result binding mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionKernel.cs",
        "public readonly long StateGasUsed = stateGasUsed;",
        "public readonly long StateGasUsed = stateGasSpill;",
        TestName = "21n concrete state-gas result binding mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionAdapterKernel.cs",
        "public readonly StateGasTransitionResult Transition = transition;",
        "public readonly StateGasTransitionResult Transition = new();",
        TestName = "21o concrete adapter outcome binding mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.cs",
        "Address target = TOpCall.ExecutionType != ExecutionType.DELEGATECALL && TOpCall.ExecutionType != ExecutionType.CALLCODE\n            ? codeSource\n            : env.ExecutingAccount;",
        "Address target = TOpCall.ExecutionType != ExecutionType.DELEGATECALL && TOpCall.ExecutionType != ExecutionType.CALLCODE\n            ? codeSource\n            : codeSource;",
        TestName = "22 CALLCODE dead-recipient target mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.cs",
        "codeInfo = spec.IsPrecompile(delegated)\n                ? CodeInfo.Empty",
        "codeInfo = spec.IsPrecompile(delegated)\n                ? codeInfo",
        TestName = "23 delegated precompile suppression mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs",
        "EvmInstructions.InstructionCall<TGasPolicy, TOpCall, TTracingInst, TEip8037, TEip7708, TSpec>",
        "EvmInstructions.InstructionCall<TGasPolicy, TOpCall, TTracingInst, TEip8037, TEip7708, TSpecMutated>",
        TestName = "23b actual InstructionCall selector mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Init/Steps/InitializePrecompiles.cs",
        "await KzgPolynomialCommitments.InitializeAsync(logger, initConfig.KzgSetupPath);",
        "await Task.CompletedTask;",
        TestName = "24 KZG initialization mutation is rejected")]
    [TestCase(
        "src/Nethermind/Nethermind.Evm.Precompiles/ECRecoverPrecompile.cs",
        "EthereumEcdsa.RecoverAddressRaw(signature, recoveryId, message, publicKey)",
        "EthereumEcdsa.RecoverAddressRawMutated(signature, recoveryId, message, publicKey)",
        TestName = "25 production-to-oracle symbol substitution is rejected")]
    public void ProductionMutationIsRejected(string relativePath, string original, string replacement)
    {
        string copy = CopyRepository();
        string path = Path.Combine(copy, relativePath.Replace('/', Path.DirectorySeparatorChar));
        string source = File.ReadAllText(path);
        Assert.That(source.Contains(original, StringComparison.Ordinal), Is.True, "mutation fixture was not anchored");
        File.WriteAllText(path, source.Replace(original, replacement, StringComparison.Ordinal));

        Assert.That(
            () => PrecompileFrameProfile.Extract(copy, CreateDirectory()),
            Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void FullFramePriceRunReorderIsRejected()
    {
        string copy = CopyRepository();
        string path = Path.Combine(copy, "src", "Nethermind", "Nethermind.Evm", "VirtualMachine.cs");
        string source = File.ReadAllText(path);
        const string priceAnchor = "        if (!TGasPolicy.TryConsumePrecompileGas";
        const string runStatement = "        return ExecutePrecompileCall(state, precompile, callData, spec);";
        int priceStart = source.IndexOf(priceAnchor, StringComparison.Ordinal);
        int runStart = source.IndexOf(runStatement, priceStart, StringComparison.Ordinal);
        Assert.That(priceStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(runStart, Is.GreaterThan(priceStart));

        source = source.Remove(runStart, runStatement.Length)
            .Insert(priceStart, runStatement + "\n\n");
        File.WriteAllText(path, source);

        Assert.That(
            () => PrecompileFrameProfile.Extract(copy, CreateDirectory()),
            Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void GeneratedCancellationSeparatesBoundaryEligibilityAndObservation()
    {
        string output = CreateDirectory();
        ExtractionResult result = PrecompileFrameProfile.Extract(RepositoryRoot, output);
        string lean = File.ReadAllText(result.LeanPath);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(result.IrPath));

        Assert.That(lean, Does.Contain("cancelledBeforeDispatch : Bool"));
        Assert.That(lean, Does.Contain("cancelledAtBoundary : Bool"));
        Assert.That(lean, Does.Contain("completedWithoutException : Bool"));
        Assert.That(document.RootElement.GetProperty("routes")[0].GetProperty("cancellation").GetProperty("boundaryPredicate").GetString(), Is.EqualTo(
            "cancelable && completedWithoutException && (opcodeCount & checkMask) = 0 && nextProgramCounter < codeLength"));
        Assert.That(lean, Does.Contain("facts.cancelable && facts.completedWithoutException && facts.opcodeCount % 1024 = 0 && facts.nextProgramCounter < facts.codeLength"));
        Assert.That(lean, Does.Contain("cancellationBoundary facts && facts.cancelledAtBoundary"));
        Assert.That(lean, Does.Contain("facts.cancelable && (facts.cancelledBeforeDispatch || cancellationAtBoundary facts)"));
    }

    [Test]
    public void DirectOrderMetadataMatchesInlineRoute()
    {
        ExtractionResult result = PrecompileFrameProfile.Extract(RepositoryRoot, CreateDirectory());
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(result.IrPath));
        JsonElement route = document.RootElement.GetProperty("routes")[0];

        Assert.That(route.GetProperty("directSuccessOrder").GetString(), Is.EqualTo(
            "price -> Run -> account touch -> refund child gas -> returndata -> output copy -> stack success"));
        Assert.That(route.GetProperty("directFailureOrder").GetString(), Is.EqualTo(
            "pricing hard: price -> parent state-gas restore -> returndata clear -> stack failure; leaf failure: price -> Run -> execution-gas clear -> parent state-gas restore -> returndata clear -> stack failure; output-copy OOG: price -> Run -> account touch -> refund child gas -> returndata -> return OutOfGas"));
        Assert.That(route.GetProperty("directSuccessOrder").GetString(), Does.Not.Contain("snapshot"));
        Assert.That(route.GetProperty("directFailureOrder").GetString(), Does.Not.Contain("snapshot"));
        Assert.That(route.GetProperty("directSuccessOrder").GetString(), Does.Not.Contain("commit"));
        Assert.That(route.GetProperty("directFailureOrder").GetString(), Does.Not.Contain("Handle"));
        JsonElement directOutcomes = route.GetProperty("directOutcomes");
        Assert.That(directOutcomes.GetArrayLength(), Is.EqualTo(5));
        JsonElement outputCopy = directOutcomes.EnumerateArray()
            .Single(outcome => outcome.GetProperty("outcome").GetString() == "outputCopyOutOfGas");
        Assert.That(outputCopy.GetProperty("result").GetString(), Is.EqualTo("outOfGas"));
        Assert.That(outputCopy.GetProperty("effects").EnumerateArray().Select(effect => effect.GetString()).ToArray(), Is.EqualTo(
            ["pricing", "run", "accountTouchOrCreate", "childRefund", "returnData", "returnOutOfGas"]));
        Assert.That(outputCopy.GetProperty("runsLeaf").GetBoolean(), Is.True);
        Assert.That(outputCopy.GetProperty("touchesAccount").GetBoolean(), Is.True);
        Assert.That(outputCopy.GetProperty("returnsRefund").GetBoolean(), Is.True);
        Assert.That(outputCopy.GetProperty("clearsReturnData").GetBoolean(), Is.False);
        Assert.That(outputCopy.GetProperty("pushesSuccess").GetBoolean(), Is.False);
        Assert.That(outputCopy.GetProperty("restoresSnapshot").GetBoolean(), Is.False);
        Assert.That(outputCopy.GetProperty("effects").EnumerateArray().Select(effect => effect.GetString()).ToArray(), Does.Not.Contain("outputCopy"));
        Assert.That(outputCopy.GetProperty("effects").EnumerateArray().Select(effect => effect.GetString()).ToArray(), Does.Not.Contain("stackSuccess"));
    }

    [Test]
    public void CancellationIrMutationCannotRemainHardcodedInEmitter()
    {
        ExtractionResult result = PrecompileFrameProfile.Extract(RepositoryRoot, CreateDirectory());
        JsonSerializerOptions options = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };
        IrDocument ir = JsonSerializer.Deserialize<IrDocument>(File.ReadAllBytes(result.IrPath), options)!;
        RouteDescriptor[] routes = ir.Routes.Select(route => route with
        {
            Cancellation = route.Cancellation with
            {
                BoundaryPredicate = route.Cancellation.BoundaryPredicate.Replace(
                    "completedWithoutException", "cancelledAtBoundary", StringComparison.Ordinal),
            },
        }).ToArray();
        IrDocument mutated = ir with { Routes = routes };

        Assert.That(
            () => PrecompileFrameLeanEmitter.Emit(mutated, "0", "0"),
            Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void CancellationPollCompositionIsDerivedFromTypedIr()
    {
        ExtractionResult result = PrecompileFrameProfile.Extract(RepositoryRoot, CreateDirectory());
        JsonSerializerOptions options = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };
        IrDocument ir = JsonSerializer.Deserialize<IrDocument>(File.ReadAllBytes(result.IrPath), options)!;
        RouteDescriptor[] routes = ir.Routes.Select(route => route with
        {
            Cancellation = route.Cancellation with
            {
                ChecksAtBoundary = false,
            },
        }).ToArray();
        IrDocument mutated = ir with { Routes = routes };

        string lean = System.Text.Encoding.UTF8.GetString(PrecompileFrameLeanEmitter.Emit(mutated, "0", "0"));
        Assert.That(lean, Does.Contain("def cancellationAtBoundary (facts : CallFacts) : Bool :=\n  false"));
        Assert.That(lean, Does.Contain("def cancellationRequested (facts : CallFacts) : Bool :=\n  facts.cancelable && facts.cancelledBeforeDispatch"));
        Assert.That(lean, Does.Not.Contain("cancellationBoundary facts && facts.cancelledAtBoundary"));
    }

    [Test]
    public void FullFrameResiduesAreTypedAndOrdered()
    {
        ExtractionResult result = PrecompileFrameProfile.Extract(RepositoryRoot, CreateDirectory());
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(result.IrPath));
        JsonElement outcomes = document.RootElement.GetProperty("routes")[0].GetProperty("fullFrameOutcomes");

        JsonElement pricing = outcomes.EnumerateArray()
            .Single(outcome => outcome.GetProperty("outcome").GetString() == "pricingHardFailure");
        JsonElement returned = outcomes.EnumerateArray()
            .Single(outcome => outcome.GetProperty("outcome").GetString() == "returnedLeafHardFailure");
        JsonElement managed = outcomes.EnumerateArray()
            .Single(outcome => outcome.GetProperty("outcome").GetString() == "managedNestedSoftRevert");
        JsonElement success = outcomes.EnumerateArray()
            .Single(outcome => outcome.GetProperty("outcome").GetString() == "success");

        Assert.That(pricing.GetProperty("effects").EnumerateArray().Select(effect => effect.GetString()).ToArray(), Is.EqualTo(
            ["transferLog", "accountTouchOrCreate", "ripemdTouchLatch", "pricing", "snapshotRestore", "returnDataClear", "stateGasRestore", "stackFailure"]));
        Assert.That(returned.GetProperty("effects").EnumerateArray().Select(effect => effect.GetString()).ToArray(), Is.EqualTo(
            ["transferLog", "accountTouchOrCreate", "ripemdTouchLatch", "pricing", "run", "snapshotRestore", "returnDataClear", "stateGasRestore", "stackFailure"]));
        Assert.That(managed.GetProperty("effects").EnumerateArray().Select(effect => effect.GetString()).ToArray(), Is.EqualTo(
            ["transferLog", "accountTouchOrCreate", "ripemdTouchLatch", "pricing", "run", "executionGasClear", "stateGasRestore", "handleRevert", "snapshotRestore", "returnData", "stackFailure"]));
        Assert.That(success.GetProperty("effects").EnumerateArray().Select(effect => effect.GetString()).ToArray(), Is.EqualTo(
            ["transferLog", "accountTouchOrCreate", "ripemdTouchLatch", "pricing", "run", "childRefund", "handleReturn", "returnData", "childCommit", "stackSuccess"]));
        Assert.That(pricing.GetProperty("clearsReturnData").GetBoolean(), Is.True);
        Assert.That(returned.GetProperty("clearsReturnData").GetBoolean(), Is.True);
        Assert.That(managed.GetProperty("clearsReturnData").GetBoolean(), Is.False);
        Assert.That(success.GetProperty("returnsRefund").GetBoolean(), Is.True);
    }

    [Test]
    public void DirectOutputCopyOutOfGasIsASeparateTypedResidue()
    {
        ExtractionResult result = PrecompileFrameProfile.Extract(RepositoryRoot, CreateDirectory());
        string lean = File.ReadAllText(result.LeanPath);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(result.IrPath));
        JsonElement direct = document.RootElement.GetProperty("routes")[0].GetProperty("directOutcomes");
        JsonElement outputCopy = direct.EnumerateArray()
            .Single(outcome => outcome.GetProperty("outcome").GetString() == "outputCopyOutOfGas");

        Assert.That(outputCopy.GetProperty("result").GetString(), Is.EqualTo("outOfGas"));
        Assert.That(outputCopy.GetProperty("effects").EnumerateArray().Select(effect => effect.GetString()).ToArray(), Is.EqualTo(
            ["pricing", "run", "accountTouchOrCreate", "childRefund", "returnData", "returnOutOfGas"]));
        Assert.That(lean, Does.Contain(".outputCopyOutOfGas => { result := .outOfGas"));
    }

    [Test]
    public void TrySaveBoundsAdmissionIsTypedAndManifested()
    {
        ExtractionResult result = PrecompileFrameProfile.Extract(RepositoryRoot, CreateDirectory());
        using JsonDocument ir = JsonDocument.Parse(File.ReadAllText(result.IrPath));
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(result.ManifestPath));
        string lean = File.ReadAllText(result.LeanPath);

        JsonElement bounds = ir.RootElement.GetProperty("trySaveBounds");
        Assert.That(ir.RootElement.GetProperty("mutationChecks").GetArrayLength(), Is.EqualTo(26));
        Assert.That(bounds.GetProperty("sourcePath").GetString(), Is.EqualTo(
            "src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs"));
        Assert.That(bounds.GetProperty("owner").GetString(), Is.EqualTo("EvmPooledMemory"));
        Assert.That(bounds.GetProperty("member").GetString(), Is.EqualTo("TrySave"));
        Assert.That(bounds.GetProperty("validationHelper").GetString(), Is.EqualTo("CheckMemoryAccessViolation"));
        Assert.That(bounds.GetProperty("failureFlag").GetString(), Is.EqualTo("isViolation"));
        Assert.That(bounds.GetProperty("failureEffects").EnumerateArray().Select(item => item.GetString()).ToArray(), Is.EqualTo(
            ["returnFalse", "noUpdateSize", "noSaveAfterGas", "noMemoryMutation", "noOutputWrite"]));
        Assert.That(bounds.GetProperty("boundsFailurePrecedesMutation").GetBoolean(), Is.True);
        Assert.That(lean, Does.Contain("def trySaveBounds : TrySaveBounds :="));
        Assert.That(lean, Does.Contain(".noOutputWrite"));

        JsonElement source = manifest.RootElement.GetProperty("sources").EnumerateArray()
            .Single(item => item.GetProperty("path").GetString() ==
                "src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs");
        Assert.That(source.GetProperty("role").GetString(), Is.EqualTo("direct output-copy memory bounds"));
        JsonElement member = manifest.RootElement.GetProperty("members").EnumerateArray()
            .Single(item => item.GetProperty("sourcePath").GetString() ==
                "src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs" &&
                item.GetProperty("owner").GetString() == "EvmPooledMemory" &&
                item.GetProperty("member").GetString() == "TrySave");
        Assert.That(member.GetProperty("signature").GetString(), Does.StartWith("bool TrySave(UInt256 location,ReadOnlySpan<byte> value)"));
    }

    [Test]
    public void ManifestMutationIsRejected()
    {
        string output = CreateDirectory();
        ExtractionResult result = PrecompileFrameProfile.Extract(RepositoryRoot, output);
        string manifest = File.ReadAllText(result.ManifestPath);
        File.WriteAllText(result.ManifestPath, manifest.Replace("schemaVersion", "schemaVersionMutated", StringComparison.Ordinal));

        Assert.That(
            () => PrecompileFrameProfile.ValidateExistingArtifacts(
                RepositoryRoot, result.IrPath, result.ManifestPath, result.LeanPath),
            Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void IrFieldOmissionIsRejected()
    {
        string output = CreateDirectory();
        ExtractionResult result = PrecompileFrameProfile.Extract(RepositoryRoot, output);
        JsonObject ir = JsonNode.Parse(File.ReadAllText(result.IrPath))!.AsObject();
        ir.Remove("routes");
        File.WriteAllText(result.IrPath, ir.ToJsonString());

        Assert.That(
            () => PrecompileFrameProfile.ValidateExistingArtifacts(
                RepositoryRoot, result.IrPath, result.ManifestPath, result.LeanPath),
            Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void IrNullFieldIsRejected()
    {
        string output = CreateDirectory();
        ExtractionResult result = PrecompileFrameProfile.Extract(RepositoryRoot, output);
        JsonObject ir = JsonNode.Parse(File.ReadAllText(result.IrPath))!.AsObject();
        ir["cache"] = null;
        File.WriteAllText(result.IrPath, ir.ToJsonString());

        Assert.That(
            () => PrecompileFrameProfile.ValidateExistingArtifacts(
                RepositoryRoot, result.IrPath, result.ManifestPath, result.LeanPath),
            Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void IrDuplicateFieldIsRejected()
    {
        string output = CreateDirectory();
        ExtractionResult result = PrecompileFrameProfile.Extract(RepositoryRoot, output);
        string ir = File.ReadAllText(result.IrPath);
        const string marker = "\"kernel\":\"";
        int markerStart = ir.IndexOf(marker, StringComparison.Ordinal);
        Assert.That(markerStart, Is.GreaterThanOrEqualTo(0));
        int valueEnd = ir.IndexOf('"', markerStart + marker.Length);
        string duplicate = ir.Insert(valueEnd + 1, ",\"kernel\":\"standard-mainnet-amsterdam-precompile-frame-stage-a\"");
        File.WriteAllText(result.IrPath, duplicate);

        Assert.That(
            () => PrecompileFrameProfile.ValidateExistingArtifacts(
                RepositoryRoot, result.IrPath, result.ManifestPath, result.LeanPath),
            Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void IrUnknownFieldIsRejected()
    {
        string output = CreateDirectory();
        ExtractionResult result = PrecompileFrameProfile.Extract(RepositoryRoot, output);
        JsonObject ir = JsonNode.Parse(File.ReadAllText(result.IrPath))!.AsObject();
        ir["unknownField"] = true;
        File.WriteAllText(result.IrPath, ir.ToJsonString());

        Assert.That(
            () => PrecompileFrameProfile.ValidateExistingArtifacts(
                RepositoryRoot, result.IrPath, result.ManifestPath, result.LeanPath),
            Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void CaseSensitiveManifestPathIsRejected()
    {
        string output = CreateDirectory();
        ExtractionResult result = PrecompileFrameProfile.Extract(RepositoryRoot, output);
        string manifest = File.ReadAllText(result.ManifestPath);
        int marker = manifest.IndexOf("\"path\":\"Directory.Packages.props\"", StringComparison.Ordinal);
        Assert.That(marker, Is.GreaterThanOrEqualTo(0));
        manifest = manifest.Remove(marker, "\"path\":\"Directory.Packages.props\"".Length)
            .Insert(marker, "\"path\":\"directory.Packages.props\"");
        File.WriteAllText(result.ManifestPath, manifest);

        Assert.That(
            () => PrecompileFrameProfile.ValidateExistingArtifacts(
                RepositoryRoot, result.IrPath, result.ManifestPath, result.LeanPath),
            Throws.TypeOf<ExtractionException>());
    }

    [TestCase("\"sources\"", TestName = "Source digest mutation is rejected")]
    [TestCase("\"members\"", TestName = "Member digest mutation is rejected")]
    [TestCase("\"dependencies\"", TestName = "Dependency digest mutation is rejected")]
    public void ManifestDigestMutationIsRejected(string section)
    {
        string output = CreateDirectory();
        ExtractionResult result = PrecompileFrameProfile.Extract(RepositoryRoot, output);
        string manifest = File.ReadAllText(result.ManifestPath);
        int sectionStart = manifest.IndexOf(section, StringComparison.Ordinal);
        Assert.That(sectionStart, Is.GreaterThanOrEqualTo(0));
        int marker = manifest.IndexOf("\"sha256\":\"", sectionStart, StringComparison.Ordinal);
        Assert.That(marker, Is.GreaterThanOrEqualTo(0));
        int valueStart = marker + "\"sha256\":\"".Length;
        manifest = manifest.Remove(valueStart, 64).Insert(valueStart, new string('0', 64));
        File.WriteAllText(result.ManifestPath, manifest);

        Assert.That(
            () => PrecompileFrameProfile.ValidateExistingArtifacts(
                RepositoryRoot, result.IrPath, result.ManifestPath, result.LeanPath),
            Throws.TypeOf<ExtractionException>());
    }

    [TestCase("\"ir\":{", TestName = "IR artifact digest mutation is rejected")]
    [TestCase("\"lean\":{", TestName = "Lean artifact digest mutation is rejected")]
    [TestCase("\"combinedSourceSha256\":\"", TestName = "Combined source digest mutation is rejected")]
    [TestCase("\"combinedMemberSha256\":\"", TestName = "Combined member digest mutation is rejected")]
    public void ManifestArtifactDigestMutationIsRejected(string markerText)
    {
        string output = CreateDirectory();
        ExtractionResult result = PrecompileFrameProfile.Extract(RepositoryRoot, output);
        string manifest = File.ReadAllText(result.ManifestPath);
        int marker = manifest.IndexOf(markerText, StringComparison.Ordinal);
        Assert.That(marker, Is.GreaterThanOrEqualTo(0));
        int valueStart;
        if (markerText.StartsWith("\"combined", StringComparison.Ordinal))
        {
            valueStart = marker + markerText.Length;
        }
        else
        {
            int hashMarker = manifest.IndexOf("\"sha256\":\"", marker, StringComparison.Ordinal);
            Assert.That(hashMarker, Is.GreaterThanOrEqualTo(0));
            valueStart = hashMarker + "\"sha256\":\"".Length;
        }
        manifest = manifest.Remove(valueStart, 64).Insert(valueStart, new string('f', 64));
        File.WriteAllText(result.ManifestPath, manifest);

        Assert.That(
            () => PrecompileFrameProfile.ValidateExistingArtifacts(
                RepositoryRoot, result.IrPath, result.ManifestPath, result.LeanPath),
            Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void ManifestNullArrayIsRejected()
    {
        string output = CreateDirectory();
        ExtractionResult result = PrecompileFrameProfile.Extract(RepositoryRoot, output);
        JsonObject manifest = JsonNode.Parse(File.ReadAllText(result.ManifestPath))!.AsObject();
        manifest["members"] = null;
        File.WriteAllText(result.ManifestPath, manifest.ToJsonString());

        Assert.That(
            () => PrecompileFrameProfile.ValidateExistingArtifacts(
                RepositoryRoot, result.IrPath, result.ManifestPath, result.LeanPath),
            Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void CanonicalNewlineMutationIsRejected()
    {
        string output = CreateDirectory();
        ExtractionResult result = PrecompileFrameProfile.Extract(RepositoryRoot, output);
        File.AppendAllText(result.ManifestPath, "\r\n");

        Assert.That(
            () => PrecompileFrameProfile.ValidateExistingArtifacts(
                RepositoryRoot, result.IrPath, result.ManifestPath, result.LeanPath),
            Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void CheckedInArtifactsMatchTheCurrentProfile()
    {
        string generated = Path.Combine(RepositoryRoot, "tools", "Evm", "Lean", "PrecompileFrameExtractor", "Generated");
        PrecompileFrameProfile.ValidateExistingArtifacts(
            RepositoryRoot,
            Path.Combine(generated, "precompile-frame-stage-a.ir.json"),
            Path.Combine(generated, "precompile-frame-stage-a.source-manifest.json"),
            Path.Combine(generated, "PrecompileFrameStageA.lean"));
        PrecompileFrameStageBProfile.ValidateExistingArtifacts(
            RepositoryRoot,
            Path.Combine(generated, "precompile-frame-stage-a.ir.json"),
            Path.Combine(generated, "precompile-frame-stage-a.source-manifest.json"),
            Path.Combine(generated, "PrecompileFrameStageA.lean"),
            Path.Combine(generated, "precompile-frame-stage-b.ir.json"),
            Path.Combine(generated, "precompile-frame-stage-b.source-manifest.json"),
            Path.Combine(generated, "PrecompileFrameStageB.lean"));
    }

    private static string CreateDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "precompile-frame-stage-a", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string CopyRepository()
    {
        string destination = CreateDirectory();
        foreach (string relativePath in SourceClosurePaths())
        {
            string source = Path.Combine(RepositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            string target = Path.Combine(destination, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
        }

        return destination;
    }

    private static IEnumerable<string> SourceClosurePaths()
    {
        string manifestPath = Path.Combine(RepositoryRoot, "tools", "Evm", "Lean", "PrecompileFrameExtractor", "Generated",
            "precompile-frame-stage-a.source-manifest.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        HashSet<string> paths = new(StringComparer.Ordinal);
        foreach (JsonElement source in document.RootElement.GetProperty("sources").EnumerateArray())
        {
            paths.Add(source.GetProperty("path").GetString()!);
        }

        foreach (JsonElement dependency in document.RootElement.GetProperty("dependencies").EnumerateArray())
        {
            paths.Add(dependency.GetProperty("path").GetString()!);
        }

        paths.Add(PrecompileFrameStageBProfile.ReferencePath);
        paths.Add(PrecompileFrameStageBProfile.RefinementPath);

        return paths.Order(StringComparer.Ordinal);
    }

    private static string FindRepositoryRoot(string start)
    {
        DirectoryInfo? directory = new(start);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "Nethermind")) &&
                Directory.Exists(Path.Combine(directory.FullName, "tools", "Evm", "Lean")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the formal repository root.");
    }
}
