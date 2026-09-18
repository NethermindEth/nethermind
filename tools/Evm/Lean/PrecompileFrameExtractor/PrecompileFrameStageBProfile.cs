// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Nethermind.Evm.Lean.PrecompileFrameExtractor;

internal static class PrecompileFrameStageBProfile
{
    internal const string ExtractorVersion = "precompile-frame-stage-b-1";
    internal const string KernelName = "standard-mainnet-amsterdam-precompile-direct-stage-b";
    internal const string IrFileName = "precompile-frame-stage-b.ir.json";
    internal const string ManifestFileName = "precompile-frame-stage-b.source-manifest.json";
    internal const string LeanFileName = "PrecompileFrameStageB.lean";
    internal const string ReferencePath =
        "tools/Evm/Lean/PrecompileFrameExtractor/Specification/PrecompileFrameStageBReference.lean";
    internal const string RefinementPath =
        "tools/Evm/Lean/PrecompileFrameExtractor/Refinement/PrecompileFrameStageB.lean";

    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default
        .WithLanguageVersion(LanguageVersion.CSharp14)
        .WithDocumentationMode(DocumentationMode.Parse)
        .WithKind(SourceCodeKind.Regular);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly string[] RelevantSourcePaths =
    [
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.cs",
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.std.cs",
        "src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs",
        "src/Nethermind/Nethermind.Evm/VirtualMachine.cs",
        "src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs",
        "src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs",
        "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs",
        "src/Nethermind/Nethermind.Evm/GasPolicy/PrecompileGasPricingKernel.cs",
        "src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionAdapterKernel.cs",
        "src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionKernel.cs",
    ];

    private static readonly string[] MutationChecks =
    [
        "direct eligibility guard inversion is rejected",
        "input-memory failure polarity is rejected",
        "pricing-before-oracle order is rejected when changed",
        "pricing overflow and ordinary OOG remain distinct",
        "returned failure and managed exception retain explicit oracle outcomes",
        "account touch before successful oracle return is rejected",
        "child refund before account touch is rejected",
        "returndata before child refund is rejected",
        "output-copy TrySave polarity is rejected",
        "TrySave pre-failure mutation is rejected",
        "pre-dispatch cancellation omission or polarity is rejected",
        "boundary Boolean operator or polarity mutation is rejected",
        "boundary break mutation is rejected",
        "boundary poll separation is rejected",
        "reference type or executePrecompile identity change is rejected",
        "independent reference decision/render architecture mutation is rejected",
        "refinement theorem identity or universal quantification change is rejected",
        "Stage A artifact hash drift is rejected",
        "Stage B IR, manifest, and Lean artifact mutation is rejected",
        "concrete child execution-gas initialization mutation is rejected",
        "concrete child reservoir transfer mutation is rejected",
        "concrete child state-used zero mutation is rejected",
        "concrete child state-spill zero mutation is rejected",
        "concrete child refunded-spill default-zero mutation is rejected",
        "concrete pricing policy and pure kernel mutations are rejected",
        "concrete refund adapter/kernel mutations are rejected",
        "concrete halt-restore adapter/kernel mutations are rejected",
        "concrete execution-gas clear mutation is rejected",
    ];

    internal static StageBExtractionResult Extract(
        string root,
        string outputDirectory,
        IrDocument stageAIr,
        SourceManifest stageAManifest,
        byte[] stageAIrBytes,
        byte[] stageAManifestBytes,
        byte[] stageALeanBytes)
    {
        StageBArtifacts artifacts = Build(
            Path.GetFullPath(root), stageAIr, stageAManifest, stageAIrBytes, stageAManifestBytes, stageALeanBytes);
        string output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        string irPath = Path.Combine(output, IrFileName);
        string manifestPath = Path.Combine(output, ManifestFileName);
        string leanPath = Path.Combine(output, LeanFileName);
        WriteDeterministic(irPath, artifacts.IrBytes);
        WriteDeterministic(manifestPath, artifacts.ManifestBytes);
        WriteDeterministic(leanPath, artifacts.LeanBytes);
        return new(
            irPath,
            manifestPath,
            leanPath,
            Hash(artifacts.IrBytes),
            Hash(artifacts.ManifestBytes),
            Hash(artifacts.LeanBytes),
            artifacts.Ir.Operational.Branches.Length,
            artifacts.Ir.OracleBindings.Length);
    }

    internal static void ValidateExistingArtifacts(
        string root,
        string stageAIrPath,
        string stageAManifestPath,
        string stageALeanPath,
        string stageBIrPath,
        string stageBManifestPath,
        string stageBLeanPath)
    {
        PrecompileFrameProfile.ValidateExistingArtifacts(root, stageAIrPath, stageAManifestPath, stageALeanPath);
        byte[] stageAIrBytes = File.ReadAllBytes(stageAIrPath);
        byte[] stageAManifestBytes = File.ReadAllBytes(stageAManifestPath);
        byte[] stageALeanBytes = File.ReadAllBytes(stageALeanPath);
        IrDocument stageAIr = Deserialize<IrDocument>(stageAIrBytes, "Stage A IR");
        SourceManifest stageAManifest = Deserialize<SourceManifest>(stageAManifestBytes, "Stage A manifest");
        StageBArtifacts expected = Build(
            Path.GetFullPath(root), stageAIr, stageAManifest, stageAIrBytes, stageAManifestBytes, stageALeanBytes);

        RequireEqual(stageBIrPath, expected.IrBytes, "Stage B IR");
        RequireEqual(stageBManifestPath, expected.ManifestBytes, "Stage B manifest");
        RequireEqual(stageBLeanPath, expected.LeanBytes, "Stage B Lean");
    }

    private static StageBArtifacts Build(
        string root,
        IrDocument stageAIr,
        SourceManifest stageAManifest,
        byte[] stageAIrBytes,
        byte[] stageAManifestBytes,
        byte[] stageALeanBytes)
    {
        ValidateStageA(stageAIr, stageAManifest, stageAIrBytes, stageAManifestBytes, stageALeanBytes);
        Dictionary<string, CompilationUnitSyntax> roots = RelevantSourcePaths.ToDictionary(
            static path => path,
            path => Parse(root, path),
            StringComparer.Ordinal);
        ValidateOperationalSources(roots);

        SourceIdentity[] sources = RelevantSourcePaths.Select(path => stageAManifest.Sources.Single(source => source.Path == path))
            .OrderBy(static source => source.Path, StringComparer.Ordinal)
            .ToArray();
        MemberIdentity[] members = CollectMembers(roots);
        StageBProofBinding reference = ReadProofBinding(
            root,
            "independentReference",
            ReferencePath,
            "Eip803x.PrecompileFrame.StageB.Reference",
            ["GasState", "LeafOracleResult", "Input", "Outcome"],
            "executePrecompile",
            theorem: null);
        StageBProofBinding refinement = ReadProofBinding(
            root,
            "refinementProof",
            RefinementPath,
            "Eip803x.PrecompileFrame.StageB.Refinement",
            ["toReferenceInput", "toReferenceOutcome"],
            "toReferenceOracle",
            "source_execute_precompile_refines_reference");
        StageBStageAIdentity stageA = new(
            new(PrecompileFrameProfile.IrFileName, Hash(stageAIrBytes)),
            new(PrecompileFrameProfile.ManifestFileName, Hash(stageAManifestBytes)),
            new(PrecompileFrameProfile.LeanFileName, Hash(stageALeanBytes)),
            stageAManifest.CombinedSourceSha256,
            stageAManifest.CombinedMemberSha256,
            stageAManifest.Sources.Length,
            stageAManifest.Members.Length,
            stageAManifest.Dependencies.Length,
            stageAIr.Routes.Length);

        StageBOracleBinding[] oracles = stageAIr.PrecompileBindings.Select(binding =>
        {
            DependencyIdentity dependency = stageAManifest.Dependencies.Single(candidate =>
                candidate.Kind == "handwrittenOracle" && candidate.Name == binding.Name);
            return new StageBOracleBinding(
                binding.Name,
                binding.Address,
                dependency.Path,
                dependency.Sha256,
                binding.OracleNamespace,
                binding.OracleEntrySymbol);
        }).OrderBy(static binding => binding.Address).ToArray();

        StageBIrDocument ir = new(
            1,
            ExtractorVersion,
            KernelName,
            "stage-b-universal-oracle-bounded-operational-refinement",
            stageA,
            new(
                "For every typed input and leaf oracle, the source-derived standard direct-precompile transition equals the independent Lean reference after structural conversion.",
                [
                    "standard direct STATICCALL eligibility and decline",
                    "pre-dispatch and successful-boundary cancellation",
                    "defensive input-memory OOG",
                    "child gas creation, pricing overflow/OOG, leaf success/failure/managed exception",
                    "concrete EthereumGasPolicy child, pricing, clear, halt-restore, and refund semantics",
                    "successful account touch, gas refund, returndata, clipped output copy, and stack result",
                    "output-copy OOG after refund with no output write or success stack push",
                ],
                [
                    "cryptographic and native leaf computation",
                    "missing-native-dependency process exit",
                    "WorldJournal implementation or composition",
                    "surrounding CALL access, EIP-150 reservation, transaction reachability, and block processing",
                    "post-dispatch RunByteCode exceptional-halt handling and final frame residue",
                    "CLR/JIT/pooling/unsafe-memory correctness",
                ],
                [
                    "the supplied leaf oracle is the result of the selected admitted precompile Run implementation",
                    "the parent input is after CALL base/access/new-account charging and child-gas reservation",
                    "inputMemoryValid records TryLoad; when true callData is its exact returned bytes, and outputCopyValid records the clipped nonempty TrySave result",
                    "baseCost and dataCost are the exact selected precompile BaseGasCost and DataGasCost results",
                    "the helper input follows a successful six-operand STATICCALL pop, so its one result push has stack capacity",
                    "address, leaf selection, and isRipemd160 are facts from the admitted standard precompile resolver",
                    "Nat/Int arithmetic represents admitted fixed-width operations without overflow outside the explicit pricing guard",
                    "completedWithoutException and boundary facts are supplied by the admitted dispatch adapter",
                ]),
            new(
                "EvmInstructions.TryInlineStaticPrecompileCall",
                "instruction tracing off && action tracing off && codeSource != RIPEMD160",
                "cancelable && completedWithoutException && opcodeCount % 1024 = 0 && nextProgramCounter < codeLength",
                "base/data UInt64 guard, then debit child execution gas or distinguish overflow/ordinary OOG",
                "oracle(address, callData) returns success bytes, returned failure, or managed exception",
                "copy take(outputLength); a nonempty failed TrySave returns OOG after touch/refund/returndata and before memory/stack success",
                Branches()),
            oracles,
            sources,
            members,
            reference,
            refinement,
            MutationChecks,
            [
                "Stage A exact source/member/dependency closure is the production admission boundary",
                "source-derived Stage B kernel does not import the independent reference",
                "independent reference does not import the generated Stage B kernel",
                "universal theorem quantifies over every generated LeafOracle and Input",
                "all 18 Run implementations remain exact typed oracle bindings",
                "standard EthereumGasPolicy child/pricing/clear/refund/halt semantics are exact AST-bound through their adapter and pure kernels",
                "output-copy OOG preserves installed returndata, refunds child gas, and performs no output write or stack success",
                "cancellation before dispatch records only its poll and no VM-state effects; eligible boundary cancellation preserves completed effects",
            ]);

        byte[] irBytes = Serialize(ir);
        byte[] leanBytes = PrecompileFrameStageBLeanEmitter.Emit(ir, Hash(irBytes));
        StageBManifest manifest = new(
            1,
            ExtractorVersion,
            KernelName,
            stageA,
            sources,
            members,
            oracles,
            reference,
            refinement,
            new(IrFileName, Hash(irBytes)),
            new(LeanFileName, Hash(leanBytes)),
            ir.SemanticBindings);
        byte[] manifestBytes = Serialize(manifest);
        ValidateArtifacts(ir, manifest, irBytes, leanBytes, manifestBytes);
        return new(ir, manifest, irBytes, manifestBytes, leanBytes);
    }

    private static StageBBranchDescriptor[] Branches() =>
    [
        new("cancelledBeforeDispatch", StageBStatus.NotStarted, StageBResult.Cancelled,
            [StageBEffect.PreDispatchCancellationPoll], false, false, false, false),
        new("declined", StageBStatus.Declined, StageBResult.Declined, [], false, false, false, false),
        new("inputMemoryOutOfGas", StageBStatus.InputMemoryOutOfGas, StageBResult.OutOfGas,
            [StageBEffect.InputLoad, StageBEffect.ReturnOutOfGas], false, false, false, false),
        new("pricingOverflow", StageBStatus.PricingOverflow, StageBResult.StackFailure,
            [StageBEffect.InputLoad, StageBEffect.ChildCreate, StageBEffect.Pricing, StageBEffect.StateGasRestore,
                StageBEffect.ReturnDataClear, StageBEffect.StackFailure], false, false, false, true),
        new("pricingOutOfGas", StageBStatus.PricingOutOfGas, StageBResult.StackFailure,
            [StageBEffect.InputLoad, StageBEffect.ChildCreate, StageBEffect.Pricing, StageBEffect.StateGasRestore,
                StageBEffect.ReturnDataClear, StageBEffect.StackFailure], false, false, false, true),
        new("returnedFailure", StageBStatus.ReturnedFailure, StageBResult.StackFailure,
            [StageBEffect.InputLoad, StageBEffect.ChildCreate, StageBEffect.Pricing, StageBEffect.RunOracle,
                StageBEffect.ExecutionGasClear, StageBEffect.StateGasRestore, StageBEffect.ReturnDataClear,
                StageBEffect.StackFailure], true, false, false, true),
        new("managedException", StageBStatus.ManagedException, StageBResult.StackFailure,
            [StageBEffect.InputLoad, StageBEffect.ChildCreate, StageBEffect.Pricing, StageBEffect.RunOracle,
                StageBEffect.ExecutionGasClear, StageBEffect.StateGasRestore, StageBEffect.ReturnDataClear,
                StageBEffect.StackFailure], true, false, false, true),
        new("outputCopyOutOfGas", StageBStatus.OutputCopyOutOfGas, StageBResult.OutOfGas,
            [StageBEffect.InputLoad, StageBEffect.ChildCreate, StageBEffect.Pricing, StageBEffect.RunOracle,
                StageBEffect.AccountTouch, StageBEffect.ChildRefund, StageBEffect.ReturnDataSet,
                StageBEffect.ReturnOutOfGas], true, true, false, false),
        new("success", StageBStatus.Success, StageBResult.StackSuccess,
            [StageBEffect.InputLoad, StageBEffect.ChildCreate, StageBEffect.Pricing, StageBEffect.RunOracle,
                StageBEffect.AccountTouch, StageBEffect.ChildRefund, StageBEffect.ReturnDataSet,
                StageBEffect.OutputCopy, StageBEffect.StackSuccess], true, true, true, true),
    ];

    private static void ValidateStageA(
        IrDocument ir,
        SourceManifest manifest,
        byte[] irBytes,
        byte[] manifestBytes,
        byte[] leanBytes)
    {
        if (ir.Kernel != PrecompileFrameProfile.KernelName || ir.Routes.Length != 18 ||
            ir.PrecompileBindings.Length != 18 || ir.TrySaveBounds is null ||
            manifest.Kernel != PrecompileFrameProfile.KernelName || manifest.Sources.Length != 122 ||
            manifest.Members.Length != 154 || manifest.Dependencies.Length != 23 ||
            manifest.Ir.Sha256 != Hash(irBytes) || manifest.Lean.Sha256 != Hash(leanBytes) ||
            manifest.Ir.Path != PrecompileFrameProfile.IrFileName ||
            manifest.Lean.Path != PrecompileFrameProfile.LeanFileName ||
            !Serialize(ir).AsSpan().SequenceEqual(irBytes) ||
            !Serialize(manifest).AsSpan().SequenceEqual(manifestBytes))
        {
            throw new ExtractionException("Stage B requires the exact accepted Stage A artifact and closure shape.");
        }
    }

    private static void ValidateOperationalSources(IReadOnlyDictionary<string, CompilationUnitSyntax> roots)
    {
        MethodDeclarationSyntax direct = FindMethod(
            roots["src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.std.cs"],
            "TryInlineStaticPrecompileCall",
            method => method.TypeParameterList?.Parameters.Count == 2);
        MethodDeclarationSyntax directDeclaration = FindMethod(
            roots["src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.cs"],
            "TryInlineStaticPrecompileCall",
            method => method.TypeParameterList?.Parameters.Count == 2 &&
                method.Body is null && method.ExpressionBody is null);
        RequireCanonical(
            MethodSignature(direct),
            "privatestaticpartialbool TryInlineStaticPrecompileCall<TGasPolicy,TTracingInst>(VirtualMachine<TGasPolicy>vm,refEvmStackstack,refTGasPolicygas,inUInt256dataOffset,UInt256dataLength,inUInt256outputOffset,UInt256outputLength,IPrecompileprecompile,Addresstarget,AddresscodeSource,ulonggasLimitUl,outEvmExceptionTyperesult)whereTGasPolicy:struct,IGasPolicy<TGasPolicy>whereTTracingInst:struct,IFlag",
            "direct precompile helper definition signature");
        RequireCanonical(
            MethodSignature(directDeclaration),
            MethodSignature(direct),
            "direct precompile helper declaration/definition signature");
        string body = Canonical(direct.Body ?? throw new ExtractionException("Direct precompile helper lost its block body."));
        RequireStatements(direct.Body!,
            [
                "Debug.Assert(vm.ReturnDataisnull,\"Inline precompiles continue the current opcode chain.\");",
                "if(TTracingInst.IsActive||vm.IsTracingActions||!vm.CanExecutePrecompileCallDirectly(precompile,codeSource)){result=default;returnfalse;}",
                "if(!vm.VmState.Memory.TryLoad(indataOffset,dataLength,outReadOnlyMemory<byte>callData)){result=EvmExceptionType.OutOfGas;returntrue;}",
                "TGasPolicychildGas=TGasPolicy.CreateChildFrameGas(refgas,gasLimitUl);",
                "IReleaseSpecspec=vm.Spec;",
                "if(!TGasPolicy.TryConsumePrecompileGas(refchildGas,precompile,callData,spec)){TGasPolicy.RestoreChildStateGasOnHalt(refgas,inchildGas);vm.ReturnDataBuffer=default;result=stack.PushZero<TTracingInst,OnFlag>();returntrue;}",
                "if(!(vm.TryRunPrecompileDirectly(precompile,callData,spec,outResult<byte[]>output)&&output)){TGasPolicy.ClearExecutionGas(refchildGas);TGasPolicy.RestoreChildStateGasOnHalt(refgas,inchildGas);vm.ReturnDataBuffer=default;result=stack.PushZero<TTracingInst,OnFlag>();returntrue;}",
                "vm.WorldState.AddToBalanceAndCreateIfNotExists(target,UInt256.Zero,spec);",
                "TGasPolicy.Refund(refgas,inchildGas);",
                "ReadOnlyMemory<byte>outputData=output.Data;",
                "vm.ReturnDataBuffer=outputData;",
                "intcopyLength=outputData.Length;",
                "if(outputLength<(UInt256)copyLength)copyLength=(int)outputLength.ToLong();",
                "if(copyLength>0){if(!vm.VmState.Memory.TrySave(inoutputOffset,outputData.Span[..copyLength])){result=EvmExceptionType.OutOfGas;returntrue;}}",
                "result=stack.PushBytes<TTracingInst>(StatusCode.SuccessBytes.Span);",
                "returntrue;",
            ],
            "direct precompile helper body");
        string[] exactFragments =
        [
            "if(TTracingInst.IsActive||vm.IsTracingActions||!vm.CanExecutePrecompileCallDirectly(precompile,codeSource))",
            "if(!vm.VmState.Memory.TryLoad(indataOffset,dataLength,outReadOnlyMemory<byte>callData))",
            "TGasPolicychildGas=TGasPolicy.CreateChildFrameGas(refgas,gasLimitUl)",
            "if(!TGasPolicy.TryConsumePrecompileGas(refchildGas,precompile,callData,spec))",
            "if(!(vm.TryRunPrecompileDirectly(precompile,callData,spec,outResult<byte[]>output)&&output))",
            "vm.WorldState.AddToBalanceAndCreateIfNotExists(target,UInt256.Zero,spec)",
            "TGasPolicy.Refund(refgas,inchildGas)",
            "vm.ReturnDataBuffer=outputData",
            "if(copyLength>0)",
            "if(!vm.VmState.Memory.TrySave(inoutputOffset,outputData.Span[..copyLength]))",
            "result=EvmExceptionType.OutOfGas;returntrue",
            "result=stack.PushBytes<TTracingInst>(StatusCode.SuccessBytes.Span)",
        ];
        RequireOrdered(body, exactFragments, "direct precompile transition");

        string[] requiredInvocations =
        [
            "vm.VmState.Memory.TryLoad",
            "TGasPolicy.CreateChildFrameGas",
            "TGasPolicy.TryConsumePrecompileGas",
            "vm.TryRunPrecompileDirectly",
            "vm.WorldState.AddToBalanceAndCreateIfNotExists",
            "TGasPolicy.Refund",
            "vm.VmState.Memory.TrySave",
            "stack.PushBytes",
        ];
        foreach (string invocation in requiredInvocations)
        {
            int count = direct.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Count(candidate => Canonical(candidate.Expression).StartsWith(invocation, StringComparison.Ordinal));
            if (count != 1)
            {
                throw new ExtractionException($"Direct precompile transition requires exactly one {invocation} invocation.");
            }
        }

        MethodDeclarationSyntax trySave = FindMethod(
            roots["src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs"],
            "TrySave",
            method => method.ParameterList.Parameters.Count == 2 &&
                Canonical(method.ParameterList.Parameters[1].Type!) == "ReadOnlySpan<byte>");
        RequireStatements(trySave.Body ?? throw new ExtractionException("TrySave lost its block body."),
            [
                "if(value.Length==0){returntrue;}",
                "CheckMemoryAccessViolation(inlocation,(ulong)value.Length,outulongnewLength,outboolisViolation);",
                "if(isViolation)returnfalse;",
                "UpdateSize(newLength,rentIfNeeded:false);",
                "SaveAfterGas(inlocation,value);",
                "returntrue;",
            ],
            "TrySave body");

        MethodDeclarationSyntax dispatch = FindMethod(
            roots["src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs"],
            "RunDispatchLoop",
            method => method.TypeParameterList?.Parameters.Count == 2);
        string dispatchBody = Canonical(dispatch.Body ?? throw new ExtractionException("Dispatch loop lost its block body."));
        RequireOrdered(dispatchBody,
            [
                "if(_txTracer.IsCancelled)ThrowOperationCanceledException()",
                "if(exceptionType!=EvmExceptionType.None||(cancelableState.OpCodeCount&CancellationCheckMask)!=0||(nuint)cancelableState.FinalProgramCounter>=(nuint)stack.CodeLength)break",
                "if(_txTracer.IsCancelled)ThrowOperationCanceledException()",
            ],
            "cancellation transition");
        WhileStatementSyntax cancellationLoop = dispatch.DescendantNodes().OfType<WhileStatementSyntax>()
            .SingleOrDefault(statement => Canonical(statement.Condition) == "true")
            ?? throw new ExtractionException("Cancelable dispatch loop is missing or ambiguous.");
        RequireStatements(cancellationLoop.Statement as BlockSyntax ??
                throw new ExtractionException("Cancelable dispatch loop lost its block body."),
            [
                "byteopcode=Unsafe.Add(refstack.Code,pc);",
                "exceptionType=opcodeHandlers[opcode](refstack,refgas,refcancelableState,pc,opCodeCount);",
                "if(exceptionType!=EvmExceptionType.None||(cancelableState.OpCodeCount&CancellationCheckMask)!=0||(nuint)cancelableState.FinalProgramCounter>=(nuint)stack.CodeLength)break;",
                "if(_txTracer.IsCancelled)ThrowOperationCanceledException();",
                "pc=cancelableState.FinalProgramCounter;",
                "opCodeCount=cancelableState.OpCodeCount;",
            ],
            "cancelable dispatch loop body");

        FixedStatementSyntax dispatchFixed = dispatch.DescendantNodes().OfType<FixedStatementSyntax>()
            .SingleOrDefault()
            ?? throw new ExtractionException("Cancelable dispatch lost its pinned opcode-table scope.");
        StatementSyntax[] dispatchStatements = (dispatchFixed.Statement as BlockSyntax ??
                throw new ExtractionException("Cancelable dispatch opcode-table scope lost its block body."))
            .Statements.ToArray();
        if (dispatchStatements.Length != 10 ||
            dispatchStatements[0] is not IfStatementSyntax ordinaryDispatch ||
            Canonical(ordinaryDispatch.Condition) != "!TCancelable.IsActive" ||
            dispatchStatements[6] != cancellationLoop)
        {
            throw new ExtractionException("Cancelable dispatch setup/teardown contains an unmodeled statement.");
        }

        RequireCanonical(Canonical(dispatchStatements[1]),
            "DispatchStatecancelableState=new(){OpcodeHandlers=opcodeHandlers,Vm=this,};",
            "cancelable dispatch state setup");
        RequireCanonical(Canonical(dispatchStatements[2]),
            "if(_txTracer.IsCancelled)ThrowOperationCanceledException();",
            "pre-dispatch cancellation poll");
        RequireCanonical(Canonical(dispatchStatements[3]), "nintpc=programCounter;", "cancelable dispatch program counter setup");
        RequireCanonical(Canonical(dispatchStatements[4]), "intopCodeCount=0;", "cancelable dispatch opcode-count setup");
        RequireCanonical(Canonical(dispatchStatements[5]), "EvmExceptionTypeexceptionType;", "cancelable dispatch result setup");
        RequireCanonical(Canonical(dispatchStatements[7]), "OpCodeCount+=cancelableState.OpCodeCount;", "cancelable dispatch opcode-count fold");
        RequireCanonical(Canonical(dispatchStatements[8]), "programCounter=cancelableState.FinalProgramCounter;", "cancelable dispatch program-counter fold");
        RequireCanonical(Canonical(dispatchStatements[9]), "returnexceptionType;", "cancelable dispatch return");

        ValidateConcreteGasPolicy(roots);
    }

    private static void ValidateConcreteGasPolicy(IReadOnlyDictionary<string, CompilationUnitSyntax> roots)
    {
        CompilationUnitSyntax policyRoot = roots["src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs"];
        MethodDeclarationSyntax createChild = FindMethod(policyRoot, "CreateChildFrameGas", _ => true);
        string createChildBody = Canonical(createChild.Body ??
            throw new ExtractionException("Concrete child-gas creation lost its block body."));
        RequireOrdered(createChildBody,
            [
                "longchildStateReservoir=parentGas.StateReservoir",
                "parentGas.StateReservoir=0",
                "returnnewEthereumGasPolicy",
                "Value=childExecutionGas",
                "StateReservoir=childStateReservoir",
                "StateGasUsed=0",
                "StateGasSpill=0",
            ],
            "concrete child-gas creation");
        RequireStatements(createChild.Body!,
            [
                "longchildStateReservoir=parentGas.StateReservoir;",
                "parentGas.StateReservoir=0;",
                "returnnewEthereumGasPolicy{Value=childExecutionGas,StateReservoir=childStateReservoir,StateGasUsed=0,StateGasSpill=0,};",
            ],
            "concrete child-gas creation");

        ObjectCreationExpressionSyntax childCreation = createChild.DescendantNodes()
            .OfType<ObjectCreationExpressionSyntax>()
            .SingleOrDefault(creation => Canonical(creation.Type) == "EthereumGasPolicy")
            ?? throw new ExtractionException("Concrete child-gas creation lost its exact EthereumGasPolicy initializer.");
        string[] assignments = childCreation.Initializer?.Expressions
            .OfType<AssignmentExpressionSyntax>()
            .Select(Canonical)
            .ToArray() ?? [];
        string[] expectedAssignments =
        [
            "Value=childExecutionGas",
            "StateReservoir=childStateReservoir",
            "StateGasUsed=0",
            "StateGasSpill=0",
        ];
        if (!assignments.SequenceEqual(expectedAssignments))
        {
            throw new ExtractionException("Concrete child-gas fields no longer match the admitted five-field state.");
        }

        StructDeclarationSyntax gasType = policyRoot.DescendantNodes().OfType<StructDeclarationSyntax>()
            .Single(type => type.Identifier.ValueText == "EthereumGasPolicy");
        VariableDeclaratorSyntax refundedSpill = gasType.Members.OfType<FieldDeclarationSyntax>()
            .Where(field => Canonical(field.Declaration.Type) == "long")
            .SelectMany(static field => field.Declaration.Variables)
            .Single(field => field.Identifier.ValueText == "StateGasSpillRefunded");
        if (refundedSpill.Initializer is not null ||
            gasType.Members.OfType<ConstructorDeclarationSyntax>()
                .Any(constructor => constructor.ParameterList.Parameters.Count == 0) ||
            childCreation.Initializer!.Expressions.OfType<AssignmentExpressionSyntax>()
                .Any(assignment => Canonical(assignment.Left) == "StateGasSpillRefunded"))
        {
            throw new ExtractionException("Concrete child refunded-spill must retain its default zero initialization.");
        }

        MethodDeclarationSyntax clear = FindMethod(policyRoot, "ClearExecutionGas", _ => true);
        if (clear.Body is not null || Canonical(clear.ExpressionBody?.Expression ??
                throw new ExtractionException("Concrete execution-gas clear lost its expression body.")) != "gas.Value=0")
        {
            throw new ExtractionException("Concrete execution-gas clear no longer zeros only Value.");
        }

        MethodDeclarationSyntax consume = FindMethod(policyRoot, "TryConsumePrecompileGas", _ => true);
        RequireStatements(consume.Body ??
                throw new ExtractionException("Concrete precompile pricing lost its block body."),
            [
                "ulongbaseGasCost=precompile.BaseGasCost(spec);",
                "ulongdataGasCost=precompile.DataGasCost(inputData,spec);",
                "PrecompileGasPricingResultresult=PrecompileGasPricingKernel.TryConsume(gas.Value,baseGasCost,dataGasCost);",
                "gas.Value=result.RemainingGas;",
                "returnresult.OutcomeisPrecompileGasPricingOutcome.Success;",
            ],
            "concrete precompile pricing");

        MethodDeclarationSyntax refund = FindMethod(policyRoot, "Refund", _ => true);
        RequireStatements(refund.Body ?? throw new ExtractionException("Concrete child refund lost its block body."),
            [
                "StateGasTransitionAdapterOutcomeoutcome=StateGasTransitionAdapterKernel.Refund(gas.Value,gas.StateReservoir,gas.StateGasUsed,gas.StateGasSpill,gas.StateGasSpillRefunded,childGas.Value,childGas.StateReservoir,childGas.StateGasUsed,childGas.StateGasSpill,childGas.StateGasSpillRefunded);",
                "ApplyStateGasTransition(refgas,inoutcome);",
            ],
            "concrete child refund");

        MethodDeclarationSyntax restore = FindMethod(policyRoot, "RestoreChildStateGasOnHalt", _ => true);
        RequireStatements(restore.Body ??
                throw new ExtractionException("Concrete child halt restoration lost its block body."),
            [
                "StateGasTransitionAdapterOutcomeoutcome=StateGasTransitionAdapterKernel.RestoreChildStateGasOnHalt(parentGas.Value,parentGas.StateReservoir,parentGas.StateGasUsed,parentGas.StateGasSpill,parentGas.StateGasSpillRefunded,childGas.StateReservoir,childGas.StateGasUsed,childGas.StateGasSpill,childGas.StateGasSpillRefunded);",
                "ApplyStateGasTransition(refparentGas,inoutcome);",
            ],
            "concrete child halt restoration");

        MethodDeclarationSyntax applyResult = FindMethod(policyRoot, "ApplyStateGasTransition",
            method => method.ParameterList.Parameters.Count == 2 &&
                Canonical(method.ParameterList.Parameters[1].Type!) == "StateGasTransitionResult");
        RequireStatements(applyResult.Body ??
                throw new ExtractionException("Concrete transition application lost its block body."),
            [
                "gas.Value=result.Value;",
                "gas.StateReservoir=result.StateReservoir;",
                "gas.StateGasUsed=result.StateGasUsed;",
                "gas.StateGasSpill=result.StateGasSpill;",
                "gas.StateGasSpillRefunded=result.StateGasSpillRefunded;",
            ],
            "concrete transition application");

        MethodDeclarationSyntax applyAdapter = FindMethod(policyRoot, "ApplyStateGasTransition",
            method => method.ParameterList.Parameters.Count == 2 &&
                Canonical(method.ParameterList.Parameters[1].Type!) == "StateGasTransitionAdapterOutcome");
        if (applyAdapter.Body is not null || Canonical(applyAdapter.ExpressionBody?.Expression ??
                throw new ExtractionException("Concrete adapter application lost its expression body.")) !=
            "ApplyStateGasTransition(refgas,inoutcome.Transition)")
        {
            throw new ExtractionException("Concrete adapter application no longer forwards the transition exactly.");
        }

        ValidatePricingKernel(roots["src/Nethermind/Nethermind.Evm/GasPolicy/PrecompileGasPricingKernel.cs"]);
        ValidatePricingResultShape(roots["src/Nethermind/Nethermind.Evm/GasPolicy/PrecompileGasPricingKernel.cs"]);
        ValidateStateGasResultShape(roots["src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionKernel.cs"]);
        ValidateStateGasAdapterOutcomeShape(roots["src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionAdapterKernel.cs"]);
        ValidateStateGasKernels(
            roots["src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionAdapterKernel.cs"],
            roots["src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionKernel.cs"]);
    }

    private static void ValidatePricingResultShape(CompilationUnitSyntax root) => ValidatePrimaryDataStruct(
            root,
            "PrecompileGasPricingResult",
            [
                ("outcome", "PrecompileGasPricingOutcome"),
                ("remainingGas", "ulong"),
                ("chargedGas", "ulong"),
            ],
            [
                ("Outcome", "PrecompileGasPricingOutcome", "outcome"),
                ("RemainingGas", "ulong", "remainingGas"),
                ("ChargedGas", "ulong", "chargedGas"),
            ],
            "precompile pricing result");

    private static void ValidateStateGasResultShape(CompilationUnitSyntax root) => ValidatePrimaryDataStruct(
            root,
            "StateGasTransitionResult",
            [
                ("value", "ulong"),
                ("stateReservoir", "long"),
                ("stateGasUsed", "long"),
                ("stateGasSpill", "long"),
                ("stateGasSpillRefunded", "long"),
                ("unappliedAmount", "long"),
            ],
            [
                ("Value", "ulong", "value"),
                ("StateReservoir", "long", "stateReservoir"),
                ("StateGasUsed", "long", "stateGasUsed"),
                ("StateGasSpill", "long", "stateGasSpill"),
                ("StateGasSpillRefunded", "long", "stateGasSpillRefunded"),
                ("UnappliedAmount", "long", "unappliedAmount"),
            ],
            "state-gas transition result");

    private static void ValidateStateGasAdapterOutcomeShape(CompilationUnitSyntax root) => ValidatePrimaryDataStruct(
            root,
            "StateGasTransitionAdapterOutcome",
            [
                ("kind", "StateGasTransitionAdapterOutcomeKind"),
                ("transition", "StateGasTransitionResult"),
            ],
            [
                ("Kind", "StateGasTransitionAdapterOutcomeKind", "kind"),
                ("Transition", "StateGasTransitionResult", "transition"),
            ],
            "state-gas adapter outcome");

    private static void ValidatePrimaryDataStruct(
        CompilationUnitSyntax root,
        string typeName,
        (string Name, string Type)[] parameters,
        (string Name, string Type, string Parameter)[] fields,
        string subject)
    {
        StructDeclarationSyntax declaration = root.DescendantNodes().OfType<StructDeclarationSyntax>()
            .SingleOrDefault(candidate => candidate.Identifier.ValueText == typeName)
            ?? throw new ExtractionException($"Stage B {subject} type is missing or ambiguous.");
        if (declaration.AttributeLists.Count != 0 ||
            !declaration.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.ReadOnlyKeyword)) ||
            declaration.BaseList is not null ||
            declaration.ParameterList?.Parameters.Count != parameters.Length ||
            declaration.Members.Count != fields.Length ||
            declaration.Members.Any(static member => member is not FieldDeclarationSyntax))
        {
            throw new ExtractionException($"Stage B {subject} must retain its readonly primary-data shape.");
        }

        ParameterSyntax[] actualParameters = declaration.ParameterList!.Parameters.ToArray();
        for (int index = 0; index < parameters.Length; index++)
        {
            (string expectedName, string expectedType) = parameters[index];
            ParameterSyntax parameter = actualParameters[index];
            if (parameter.Modifiers.Count != 0 || parameter.Identifier.ValueText != expectedName ||
                parameter.Type is null || Canonical(parameter.Type) != expectedType || parameter.Default is not null)
            {
                throw new ExtractionException($"Stage B {subject} constructor parameter shape changed.");
            }
        }

        FieldDeclarationSyntax[] actualFields = declaration.Members.Cast<FieldDeclarationSyntax>().ToArray();
        for (int index = 0; index < fields.Length; index++)
        {
            (string expectedName, string expectedType, string expectedParameter) = fields[index];
            FieldDeclarationSyntax field = actualFields[index];
            if (field.Modifiers.Count != 2 ||
                !field.Modifiers[0].IsKind(SyntaxKind.PublicKeyword) ||
                !field.Modifiers[1].IsKind(SyntaxKind.ReadOnlyKeyword) ||
                Canonical(field.Declaration.Type) != expectedType ||
                field.Declaration.Variables.Count != 1)
            {
                throw new ExtractionException($"Stage B {subject} field shape changed.");
            }

            VariableDeclaratorSyntax variable = field.Declaration.Variables[0];
            if (variable.Identifier.ValueText != expectedName ||
                variable.Initializer?.Value is not IdentifierNameSyntax initializer ||
                initializer.Identifier.ValueText != expectedParameter)
            {
                throw new ExtractionException($"Stage B {subject} field-to-parameter binding changed.");
            }
        }
    }

    private static void ValidatePricingKernel(CompilationUnitSyntax root)
    {
        MethodDeclarationSyntax pricing = FindMethod(root, "TryConsume", _ => true);
        RequireStatements(pricing.Body ?? throw new ExtractionException("Pure pricing kernel lost its block body."),
            [
                "if(baseGasCost>ulong.MaxValue-dataGasCost)returnnewPrecompileGasPricingResult(PrecompileGasPricingOutcome.BaseDataOverflow,gas,0);",
                "ulongtotalGasCost=baseGasCost+dataGasCost;",
                "if(gas<totalGasCost)returnnewPrecompileGasPricingResult(PrecompileGasPricingOutcome.OutOfGas,0,0);",
                "returnnewPrecompileGasPricingResult(PrecompileGasPricingOutcome.Success,gas-totalGasCost,totalGasCost);",
            ],
            "pure precompile pricing");
    }

    private static void ValidateStateGasKernels(CompilationUnitSyntax adapterRoot, CompilationUnitSyntax kernelRoot)
    {
        MethodDeclarationSyntax adapterRefund = FindMethod(adapterRoot, "Refund", _ => true);
        string adapterRefundBody = Canonical(adapterRefund.ExpressionBody?.Expression ??
            throw new ExtractionException("Refund adapter lost its expression body."));
        RequireCanonical(adapterRefundBody,
            "new(StateGasTransitionAdapterOutcomeKind.CompletedVoid,StateGasTransitionKernel.Refund(value,stateReservoir,stateGasUsed,stateGasSpill,stateGasSpillRefunded,childValue,childStateReservoir,childStateGasUsed,childStateGasSpill,childStateGasSpillRefunded))",
            "refund adapter");

        MethodDeclarationSyntax adapterRestore = FindMethod(adapterRoot, "RestoreChildStateGasOnHalt", _ => true);
        string adapterRestoreBody = Canonical(adapterRestore.ExpressionBody?.Expression ??
            throw new ExtractionException("Halt-restore adapter lost its expression body."));
        RequireCanonical(adapterRestoreBody,
            "new(StateGasTransitionAdapterOutcomeKind.CompletedVoid,StateGasTransitionKernel.RestoreChildStateGasOnHalt(parentValue,parentStateReservoir,parentStateGasUsed,parentStateGasSpill,parentStateGasSpillRefunded,childStateReservoir,childStateGasUsed,childStateGasSpill,childStateGasSpillRefunded))",
            "halt-restore adapter");

        MethodDeclarationSyntax refund = FindMethod(kernelRoot, "Refund", _ => true);
        RequireCanonical(Canonical(refund.ExpressionBody?.Expression ??
                throw new ExtractionException("Pure refund kernel lost its expression body.")),
            "new(unchecked(value+childValue),unchecked(stateReservoir+childStateReservoir),unchecked(stateGasUsed+childStateGasUsed),unchecked(stateGasSpill+childStateGasSpill),unchecked(stateGasSpillRefunded+childStateGasSpillRefunded),0)",
            "pure child refund");

        MethodDeclarationSyntax restore = FindMethod(kernelRoot, "RestoreChildStateGasOnHalt", _ => true);
        RequireStatements(restore.Body ?? throw new ExtractionException("Pure halt restoration lost its block body."),
            [
                "longchildNetSpill=GetUnrefundedStateGasSpill(childStateGasSpill,childStateGasSpillRefunded);",
                "returnnewStateGasTransitionResult(parentValue,unchecked(unchecked(unchecked(parentStateReservoir+childStateReservoir)+childStateGasUsed)-childNetSpill),parentStateGasUsed,parentStateGasSpill,parentStateGasSpillRefunded,0);",
            ],
            "pure child halt restoration");

        MethodDeclarationSyntax unrefunded = FindMethod(kernelRoot, "GetUnrefundedStateGasSpill", _ => true);
        RequireStatements(unrefunded.Body ??
                throw new ExtractionException("Unrefunded-spill helper lost its block body."),
            ["longunrefundedSpill=unchecked(stateGasSpill-stateGasSpillRefunded);", "returnPositivePart(unrefundedSpill);"],
            "unrefunded-spill helper");
        MethodDeclarationSyntax positive = FindMethod(kernelRoot, "PositivePart", _ => true);
        if (positive.Body is not null || Canonical(positive.ExpressionBody?.Expression ??
                throw new ExtractionException("Positive-part helper lost its expression body.")) != "value>0?value:0")
        {
            throw new ExtractionException("Positive-part helper no longer matches max(value, 0).");
        }
    }

    private static MemberIdentity[] CollectMembers(IReadOnlyDictionary<string, CompilationUnitSyntax> roots)
    {
        List<(string Path, string Name, Func<MethodDeclarationSyntax, bool> Predicate)> specifications =
        [
            ("src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.std.cs", "TryInlineStaticPrecompileCall",
                method => method.TypeParameterList?.Parameters.Count == 2),
            ("src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs", "TrySave",
                method => method.ParameterList.Parameters.Count == 2 && Canonical(method.ParameterList.Parameters[1].Type!) == "ReadOnlySpan<byte>"),
            ("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "TryRunPrecompileDirectly", _ => true),
            ("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "CanExecutePrecompileCallDirectly", _ => true),
            ("src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs", "RunDispatchLoop",
                method => method.TypeParameterList?.Parameters.Count == 2),
            ("src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs", "CreateChildFrameGas", _ => true),
            ("src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs", "TryConsumePrecompileGas", _ => true),
            ("src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs", "ClearExecutionGas", _ => true),
            ("src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs", "RestoreChildStateGasOnHalt", _ => true),
            ("src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs", "Refund", _ => true),
            ("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs", "CreateChildFrameGas", _ => true),
            ("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs", "TryConsumePrecompileGas", _ => true),
            ("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs", "ClearExecutionGas", _ => true),
            ("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs", "RestoreChildStateGasOnHalt", _ => true),
            ("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs", "Refund", _ => true),
            ("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs", "ApplyStateGasTransition",
                method => method.ParameterList.Parameters.Count == 2 &&
                    Canonical(method.ParameterList.Parameters[1].Type!) == "StateGasTransitionResult"),
            ("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs", "ApplyStateGasTransition",
                method => method.ParameterList.Parameters.Count == 2 &&
                    Canonical(method.ParameterList.Parameters[1].Type!) == "StateGasTransitionAdapterOutcome"),
            ("src/Nethermind/Nethermind.Evm/GasPolicy/PrecompileGasPricingKernel.cs", "TryConsume", _ => true),
            ("src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionAdapterKernel.cs", "Refund", _ => true),
            ("src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionAdapterKernel.cs", "RestoreChildStateGasOnHalt", _ => true),
            ("src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionKernel.cs", "Refund", _ => true),
            ("src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionKernel.cs", "RestoreChildStateGasOnHalt", _ => true),
            ("src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionKernel.cs", "GetUnrefundedStateGasSpill", _ => true),
            ("src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionKernel.cs", "PositivePart", _ => true),
        ];
        return specifications.Select(specification =>
        {
            MethodDeclarationSyntax method = FindMethod(roots[specification.Path], specification.Name, specification.Predicate);
            TypeDeclarationSyntax owner = method.Ancestors().OfType<TypeDeclarationSyntax>().First();
            return new MemberIdentity(
                specification.Path,
                owner.Identifier.ValueText,
                specification.Name,
                Canonical(method.ReturnType) + " " + specification.Name + "(" +
                    string.Join(",", method.ParameterList.Parameters.Select(Canonical)) + ")",
                method.Kind().ToString(),
                Hash(Utf8WithoutBom.GetBytes(CanonicalTokens(method))));
        }).OrderBy(static member => member.SourcePath, StringComparer.Ordinal)
            .ThenBy(static member => member.Owner, StringComparer.Ordinal)
            .ThenBy(static member => member.Member, StringComparer.Ordinal)
            .ToArray();
    }

    private static StageBProofBinding ReadProofBinding(
        string root,
        string kind,
        string path,
        string expectedNamespace,
        string[] types,
        string definition,
        string? theorem)
    {
        string fullPath = Resolve(root, path);
        byte[] bytes = File.ReadAllBytes(fullPath);
        string text = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal);
        bool independentReferenceShapeChanged = kind == "independentReference" &&
            (!ContainsDeclaration(text, "BodyDecision") ||
             !ContainsDeclaration(text, "DispatchDecision") ||
             !ContainsDefinition(text, "decideBody") ||
             !ContainsDefinition(text, "renderBodyDecision") ||
             !ContainsDefinition(text, "decideDispatch") ||
             !ContainsDefinition(text, "renderDispatch") ||
             !text.Contains(
                 "def executeBody (oracle : LeafOracle) (input : Input) : Outcome :=\n  renderBodyDecision input (decideBody oracle input)",
                 StringComparison.Ordinal) ||
             !text.Contains(
                 "def executePrecompile (oracle : LeafOracle) (input : Input) : Outcome :=\n  renderDispatch input (decideDispatch oracle input)",
                 StringComparison.Ordinal));
        if (!text.Split('\n').Any(line => line.Trim() == $"namespace {expectedNamespace}") ||
            types.Any(type => !ContainsDeclaration(text, type)) ||
            !ContainsDefinition(text, definition) ||
            independentReferenceShapeChanged ||
            (theorem is not null && (!ContainsTheorem(text, theorem) ||
                !text.Contains("(oracle : G.LeafOracle) (input : G.Input)", StringComparison.Ordinal) ||
                !text.Contains("G.executePrecompile oracle input", StringComparison.Ordinal) ||
                !text.Contains("R.executePrecompile (toReferenceOracle oracle) (toReferenceInput input)", StringComparison.Ordinal))))
        {
            throw new ExtractionException($"Stage B {kind} identity changed at {path}.");
        }

        return new(kind, path, Hash(bytes), expectedNamespace, types, definition, theorem);
    }

    private static bool ContainsDeclaration(string text, string name) =>
        text.Split('\n').Select(static line => line.TrimStart()).Any(line =>
            StartsWithNamedDeclaration(line, "inductive", name) ||
            StartsWithNamedDeclaration(line, "structure", name) ||
            StartsWithNamedDeclaration(line, "def", name));

    private static bool StartsWithNamedDeclaration(string line, string keyword, string name)
    {
        string prefix = $"{keyword} {name}";
        return line.StartsWith(prefix, StringComparison.Ordinal) &&
            (line.Length == prefix.Length || line[prefix.Length] is ' ' or '\t' or '(' or ':');
    }

    private static bool ContainsDefinition(string text, string name) =>
        text.Split('\n').Select(static line => line.TrimStart()).Count(line =>
            line.StartsWith($"def {name}", StringComparison.Ordinal) &&
            (line.Length == 4 + name.Length || line[4 + name.Length] is ' ' or '\t' or '(' or ':')) == 1;

    private static bool ContainsTheorem(string text, string name) =>
        text.Split('\n').Select(static line => line.TrimStart()).Count(line =>
            line.StartsWith($"theorem {name}", StringComparison.Ordinal) &&
            (line.Length == 8 + name.Length || line[8 + name.Length] is ' ' or '\t' or '(' or ':')) == 1;

    private static void ValidateArtifacts(
        StageBIrDocument ir,
        StageBManifest manifest,
        byte[] irBytes,
        byte[] leanBytes,
        byte[] manifestBytes)
    {
        if (ir.SchemaVersion != 1 || ir.Kernel != KernelName || ir.Operational.Branches.Length != 9 ||
            ir.OracleBindings.Length != 18 || ir.Sources.Length != RelevantSourcePaths.Length ||
            ir.Members.Length != 24 || ir.MutationChecks.Length != MutationChecks.Length ||
            manifest.SchemaVersion != 1 || manifest.Kernel != KernelName ||
            manifest.Ir.Path != IrFileName || manifest.Ir.Sha256 != Hash(irBytes) ||
            manifest.Lean.Path != LeanFileName || manifest.Lean.Sha256 != Hash(leanBytes) ||
            !manifest.Sources.SequenceEqual(ir.Sources) || !manifest.Members.SequenceEqual(ir.Members) ||
            !manifest.OracleBindings.SequenceEqual(ir.OracleBindings) ||
            manifest.Reference != ir.Reference || manifest.Refinement != ir.Refinement ||
            !manifest.SemanticBindings.SequenceEqual(ir.SemanticBindings) ||
            !Serialize(ir).AsSpan().SequenceEqual(irBytes) ||
            !Serialize(manifest).AsSpan().SequenceEqual(manifestBytes) ||
            !PrecompileFrameStageBLeanEmitter.Emit(ir, Hash(irBytes)).AsSpan().SequenceEqual(leanBytes))
        {
            throw new ExtractionException("Stage B artifacts do not match the closed operational profile.");
        }
    }

    private static CompilationUnitSyntax Parse(string root, string path)
    {
        string fullPath = Resolve(root, path);
        byte[] bytes = File.ReadAllBytes(fullPath);
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            SourceText.From(bytes, bytes.Length, Encoding.UTF8, canBeEmbedded: true), ParseOptions, fullPath);
        Diagnostic[] errors = tree.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0)
        {
            throw new ExtractionException($"Stage B source {path} has Roslyn parse errors.");
        }

        return tree.GetCompilationUnitRoot();
    }

    private static MethodDeclarationSyntax FindMethod(
        CompilationUnitSyntax root,
        string name,
        Func<MethodDeclarationSyntax, bool> predicate)
    {
        MethodDeclarationSyntax[] methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == name && predicate(method)).ToArray();
        return methods.Length == 1
            ? methods[0]
            : throw new ExtractionException($"Stage B requires one exact {name} member, found {methods.Length}.");
    }

    private static void RequireOrdered(string source, string[] fragments, string subject)
    {
        int offset = -1;
        foreach (string fragment in fragments)
        {
            int next = source.IndexOf(fragment, offset + 1, StringComparison.Ordinal);
            if (next < 0)
            {
                throw new ExtractionException($"Stage B {subject} lost or reordered '{fragment}'.");
            }

            offset = next;
        }
    }

    private static void RequireStatements(BlockSyntax body, string[] expected, string subject)
    {
        string[] actual = body.Statements.Select(Canonical).ToArray();
        if (!actual.SequenceEqual(expected))
        {
            throw new ExtractionException($"Stage B {subject} no longer has the exact admitted statements.");
        }
    }

    private static void RequireCanonical(string actual, string expected, string subject)
    {
        if (actual != expected)
        {
            throw new ExtractionException($"Stage B {subject} no longer has the exact admitted expression.");
        }
    }

    private static string Canonical(SyntaxNode node) =>
        string.Concat(node.DescendantTokens(descendIntoTrivia: false).Select(static token => token.Text));

    private static string MethodSignature(MethodDeclarationSyntax method) =>
        string.Concat(method.Modifiers.Select(static token => token.Text)) +
        Canonical(method.ReturnType) + " " + method.Identifier.ValueText +
        Canonical(method.TypeParameterList ?? throw new ExtractionException("Method lost its type-parameter list.")) +
        "(" + string.Join(",", method.ParameterList.Parameters.Select(Canonical)) + ")" +
        string.Concat(method.ConstraintClauses.Select(Canonical));

    private static string CanonicalTokens(SyntaxNode node) => string.Join("\n",
        node.DescendantTokens(descendIntoTrivia: false).Select(static token => $"{token.RawKind}:{token.Text}"));

    private static byte[] Serialize<T>(T value) => Utf8WithoutBom.GetBytes(
        JsonSerializer.Serialize(value, JsonOptions).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");

    private static T Deserialize<T>(byte[] bytes, string subject)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, JsonOptions)
                ?? throw new ExtractionException($"{subject} is empty.");
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"{subject} is invalid: {exception.Message}");
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string Resolve(string root, string relativePath)
    {
        string current = root;
        foreach (string segment in relativePath.Split('/'))
        {
            string[] matches = Directory.EnumerateFileSystemEntries(current)
                .Where(path => Path.GetFileName(path).Equals(segment, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1)
            {
                throw new ExtractionException($"Stage B path '{relativePath}' is missing or case-ambiguous at '{segment}'.");
            }

            current = matches[0];
        }

        string resolved = Path.GetFullPath(current);
        string prefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ExtractionException($"Stage B path escapes the repository: {relativePath}.");
        }

        return resolved;
    }

    private static void WriteDeterministic(string path, byte[] bytes)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return;
        File.WriteAllBytes(path, bytes);
    }

    private static void RequireEqual(string path, byte[] expected, string subject)
    {
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(expected))
        {
            throw new ExtractionException($"Checked-in {subject} does not match deterministic regeneration.");
        }
    }

    private sealed record StageBArtifacts(
        StageBIrDocument Ir,
        StageBManifest Manifest,
        byte[] IrBytes,
        byte[] ManifestBytes,
        byte[] LeanBytes);
}
