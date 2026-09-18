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

namespace Nethermind.Evm.Lean.AuthorizationStateGasFoldExtractor;

internal sealed class ExtractionException(string message) : Exception(message);

internal enum GasOwner
{
    GasAvailable,
    Baseline,
}

internal enum GasField
{
    Value,
    StateReservoir,
    StateGasUsed,
    StateGasSpill,
    StateGasSpillRefunded,
}

internal enum ChargeKind
{
    NewAccountState,
    AccountWriteExecution,
    PerAuthorizationState,
}

internal enum AdmissionStage
{
    InitialIntrinsicStateGas,
    CalculateAvailableGas,
    ProcessDelegationsGuard,
    CapturePreAuthorizationStateGasUsed,
    ValidateAuthorization,
    ChargeNewAccountState,
    ChargeAccountWriteExecution,
    ChargePerAuthorizationState,
    ApplyAuthorityMutation,
    ComputeAuthorizationStateGasDelta,
    FoldTopFrameStateGas,
}

internal sealed record ExtractionResult(string IrPath, string ManifestPath, string LeanPath, int SourceCount);

internal sealed record ParameterShape(string Name, string Type, string RefKind);

internal sealed record MethodShape(
    string Namespace,
    string Owner,
    string Name,
    string ReturnType,
    ParameterShape[] Parameters,
    string Accessibility,
    string[] Modifiers);

internal sealed record SourceAnchor(
    AdmissionStage Stage,
    string Method,
    string CanonicalSyntax,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn);

internal sealed record FoldWrite(
    GasOwner Owner,
    GasField Field,
    string Operator,
    string Value,
    int Order,
    SourceAnchor Anchor);

internal sealed record FoldShape(
    MethodShape Method,
    string NoOpPredicate,
    string[] GasPolicyFields,
    FoldWrite[] Writes,
    SourceAnchor[] Anchors);

internal sealed record CallShape(
    string Callee,
    string[] Arguments,
    string Method,
    SourceAnchor Anchor);

internal sealed record ChargeShape(
    ChargeKind Kind,
    string Callee,
    string[] Arguments,
    string Guard,
    int Order,
    SourceAnchor Anchor);

internal sealed record DeltaShape(
    string Variable,
    string Left,
    string Operator,
    string Right,
    SourceAnchor Anchor);

internal sealed record FailureShape(
    string MethodReturn,
    string CallerFailureBranch,
    string PartialStateContract,
    string ResetOrder,
    SourceAnchor[] Anchors);

internal sealed record ExecutionChargeShape(
    MethodShape Method,
    MethodShape RemainingGasMethod,
    string RemainingGasExpression,
    string Guard,
    string FailureMutation,
    CallShape SuccessCall,
    MethodShape ConsumeRawMethod,
    string SuccessMutation,
    string SuccessReturn,
    SourceAnchor[] Anchors);

internal sealed record StateChargeAdapterShape(
    MethodShape Method,
    CallShape KernelCall,
    string OutOfGasGuard,
    string[] SuccessFieldProjection,
    string SuccessReturn,
    SourceAnchor[] Anchors);

internal sealed record DelegationShape(
    MethodShape Method,
    CallShape ExecuteCall,
    string ExecuteGuard,
    string InitialStateUsedExpression,
    ChargeShape[] Charges,
    string ValidAuthorizationGuard,
    string AuthorityMutationOrder,
    DeltaShape Delta,
    CallShape FoldCall,
    StateChargeAdapterShape StateChargeAdapter,
    ExecutionChargeShape ExecutionCharge,
    FailureShape Failure,
    SourceAnchor[] Anchors);

internal sealed record InitialIntrinsicShape(
    MethodShape AuthorizationCostMethod,
    string[] ZeroStateCostReturns,
    MethodShape CalculateMethod,
    string CostCall,
    string TotalStateCostAssignment,
    string StandardStateProjection,
    MethodShape ExecuteMethod,
    CallShape CalculateAvailableGasCall,
    MethodShape CalculateAvailableGasMethod,
    CallShape AvailableGasForwardCall,
    MethodShape AvailableGasMethod,
    CallShape InitializationKernelCall,
    string[] AvailableFieldProjection,
    SourceAnchor[] Anchors);

internal sealed record ReachabilityShape(
    string ConcreteProcessor,
    string EthereumBase,
    string ClosedGenericBase,
    string Registration,
    SourceAnchor RegistrationAnchor);

internal sealed record DependencyIdentity(
    string Name,
    string ManifestPath,
    string ManifestSha256,
    string RefinementPath,
    string RefinementSha256,
    string LeanPath,
    string LeanSha256,
    string RootSignature,
    string TheoremName,
    string TheoremSignature);

internal sealed record IrDocument(
    int SchemaVersion,
    string ExtractorVersion,
    string AncestorBaselineCommit,
    string SourceIdentityAuthority,
    string Kernel,
    InitialIntrinsicShape InitialIntrinsic,
    ReachabilityShape Reachability,
    DelegationShape Delegations,
    FoldShape Fold,
    DependencyIdentity[] Dependencies,
    string[] ExternalObligations);

internal sealed record SourceIdentity(string Path, string Sha256);

internal sealed record ArtifactIdentity(string Path, string Sha256);

internal sealed record AdmissionIdentity(string Key, string SourceSha256);

internal sealed record Manifest(
    int SchemaVersion,
    string ExtractorVersion,
    string CompilerVersion,
    string LanguageVersion,
    string AncestorBaselineCommit,
    string SourceIdentityAuthority,
    string Kernel,
    SourceIdentity[] Sources,
    AdmissionIdentity[] Admissions,
    ArtifactIdentity Ir,
    ArtifactIdentity Lean,
    string CombinedSha256,
    DependencyIdentity[] Dependencies,
    string[] SemanticBindings);

internal static class Extractor
{
    internal const string AncestorBaselineCommit = "b2478235e71e6a7ec2a509aa0155e25d5fdfff80";
    internal const string SourceIdentityAuthority =
        "The source-manifest SHA-256 identities are authoritative for the admitted current source; the ancestor baseline records lineage only.";

    internal const string EthereumGasPolicyPath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs";
    internal const string TransactionProcessorPath =
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs";
    internal const string IntrinsicGasCalculatorPath =
        "src/Nethermind/Nethermind.Evm/IntrinsicGasCalculator.cs";
    internal const string MainnetDiPath =
        "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs";

    internal const string TransactionGasInitializationManifestPath =
        "tools/Evm/Lean/Extractor/Generated/TransactionGasInitializationKernel.source-manifest.json";
    internal const string StateGasChargeManifestPath =
        "tools/Evm/Lean/Extractor/Generated/StateGasChargeKernel.source-manifest.json";
    internal const string StateGasTransitionManifestPath =
        "tools/Evm/Lean/Extractor/Generated/StateGasTransitionKernel.source-manifest.json";

    internal const string TransactionGasInitializationSourcePath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/TransactionGasInitializationKernel.cs";
    internal const string StateGasChargeSourcePath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/StateGasChargeKernel.cs";
    internal const string StateGasTransitionSourcePath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionKernel.cs";

    internal const string TransactionGasInitializationRefinementPath =
        "tools/Evm/Lean/Eip803x/Refinement/TransactionGasInitialization.lean";
    internal const string StateGasChargeRefinementPath =
        "tools/Evm/Lean/Eip803x/Refinement/StateGasCharge.lean";
    internal const string StateGasTransitionRefinementPath =
        "tools/Evm/Lean/Eip803x/Refinement/StateGasTransition.lean";

    internal const string DefaultLeanPath =
        "tools/Evm/Lean/AuthorizationStateGasFoldExtractor/Generated/AuthorizationStateGasFold.lean";

    private const string ExtractorVersion = "1.2.0";
    private const string IrFileName = "AuthorizationStateGasFold.ir.json";
    private const string ManifestFileName = "AuthorizationStateGasFold.source-manifest.json";
    private const string KernelName = "Nethermind.Evm.AuthorizationStateGasFold";

    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default
        .WithLanguageVersion(LanguageVersion.CSharp14)
        .WithDocumentationMode(DocumentationMode.Parse)
        .WithKind(SourceCodeKind.Regular);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly string[] ExpectedSemanticBindings =
    [
        "EthereumGasPolicy.Calculate -> IntrinsicGasCalculator.AuthorizationListCost -> zero intrinsic state gas",
        "TransactionProcessorBase<TGasPolicy>.Execute -> CalculateAvailableGas -> TransactionGasInitializationKernel.TryCreate",
        "EthereumTransactionProcessorBase -> TransactionProcessorBase<EthereumGasPolicy> -> BlockProcessingModule standard registration",
        "ExecuteEvmTransaction: prePreparationGas -> EIP-7702/EIP-8037 ProcessDelegations guard -> failure reset",
        "ProcessDelegations: EIP-8037 logical account existence -> new-account state charge -> account-write execution charge -> per-authorization state charge",
        "EthereumGasPolicy.TryConsumeStateGas -> StateGasChargeKernel.TryCharge -> exact five-field success projection and no mutation on out-of-gas",
        "ProcessDelegations: partial charge failure returns false before later authority mutation",
        "ProcessDelegations: GetStateGasUsed post-state minus pre-state is authorizationStateGasUsed",
        "ProcessDelegations: FoldTopFrameStateGas(gasAvailable, executionIntrinsicGasStandard, delta) only after loop success",
        "EthereumGasPolicy.FoldTopFrameStateGas: exact five fields and source order",
        "composition depends on exact current TransactionGasInitialization, StateGasCharge, and StateGasTransition manifests/theorem signatures",
    ];

    private static readonly DependencyExpectation[] DependencyExpectations =
    [
        new(
            "TransactionGasInitialization",
            TransactionGasInitializationManifestPath,
            TransactionGasInitializationRefinementPath,
            TransactionGasInitializationSourcePath,
            "Nethermind.Evm.GasPolicy.TransactionGasInitializationKernel",
            "Nethermind.Evm.GasPolicy.TransactionGasInitializationResult Nethermind.Evm.GasPolicy.TransactionGasInitializationKernel.TryCreate(ulong gasLimit, ulong intrinsicExecutionGas, long intrinsicStateGas, bool eip8037Enabled, ulong executionGasLimitCap)",
            "generatedTryCreate_refines_initializeTransactionGas",
            "c4081417da301771539d0509bf281a42f40a1a719cc4611c051e60fc8d6c7202",
            "8986504ad786cc621dcb0c74400980366847c43d832830973f52a5d38c290578",
            "83ce996d6dcc93a588114e73737a25a14cb21689e9e8900e6dc68a07fb170b6f",
            "0445e8f8132c720cb95043a444517e9eb499d7e7dd4df6cac971321c2595fa04",
            [
                "theorem generatedTryCreate_refines_initializeTransactionGas",
                "intrinsicStateGas : Int",
                "result.stateGasUsed = (model.stateGasUsed : Int)",
                "result.stateReservoir = (model.stateGasReservoir : Int)",
            ],
            "65ab0742596ea7ebf49d2c79601eae00152d8d039ac8e417076bf63123be1e82"),
        new(
            "StateGasCharge",
            StateGasChargeManifestPath,
            StateGasChargeRefinementPath,
            StateGasChargeSourcePath,
            "Nethermind.Evm.GasPolicy.StateGasChargeKernel.TryCharge",
            "Nethermind.Evm.GasPolicy.StateGasChargeResult Nethermind.Evm.GasPolicy.StateGasChargeKernel.TryCharge(ulong value, long stateReservoir, long stateGasUsed, long stateGasSpill, long stateGasSpillRefunded, long stateGasCost)",
            "generatedTryCharge_refines_chargeState",
            "bc62e03880343c8cd27dedd238fa386125a412e112548bec70d4a3bba4a0e575",
            "4cb359f611ef04300618f9109ac89ca6b32c857c8e9e6c2305e11a0323d57ead",
            "d6a09be29c449e3f005d84cde988a619e03c4d2b0a69fcf0989f879b2fc87337",
            "c110383aee25b24bb611e3511eb6c0f9bf54a839bb314bfbd338f2793186267e",
            [
                "theorem generatedTryCharge_refines_chargeState",
                "ProductionGas.Represents production model",
                "ProductionGas.WellFormed production",
                "ProductionGas.ChargeNoOverflow amount production",
            ],
            "ec3276958cbc6b0255fadedf15dbe49691195aa95570b332937f8d1703b103fd"),
        new(
            "StateGasTransition",
            StateGasTransitionManifestPath,
            StateGasTransitionRefinementPath,
            StateGasTransitionSourcePath,
            "Nethermind.Evm.GasPolicy.StateGasTransitionKernel",
            "Nethermind.Evm.GasPolicy.StateGasTransitionResult Nethermind.Evm.GasPolicy.StateGasTransitionKernel.AddStateGasRefundToReservoir(ulong value, long stateReservoir, long stateGasUsed, long stateGasSpill, long stateGasSpillRefunded, long amount, bool trackSpillRefund)",
            "generated_refund_matches_spec",
            "362031db8ef6bbfdcab657694f69a810369273f2ca41b7e1a233465e05c4cf85",
            "a3d3beb8004c1431e2550a702f84d816751657ad90c8320ddc5809956da41cec",
            "f284d83951b94cbfaf10ad8b5a58b0015b2c96f4ac86e97c9de8b3865dee1dce",
            "9c5e5fb5ecd72c29b2782754eb12ce1a50f59badf916d54cfed6b2329ca785e5",
            [
                "theorem generated_refund_matches_spec",
                "Spec.refundNoOverflow parent child",
                "StateGasTransitionKernel.refund",
                "= Spec.refund parent child",
            ],
            "4a31dc76fb8284a8ea5994de9a63810abe0fd7efbe6add4aa8b3d6cb564a75cc"),
    ];

    internal static ExtractionResult Extract(string repoRoot, string outputDirectory, string? leanOutputPath = null)
    {
        string canonicalRoot = Path.GetFullPath(repoRoot);
        SourceFile[] sources =
        [
            Read(canonicalRoot, EthereumGasPolicyPath, "Ethereum gas policy"),
            Read(canonicalRoot, TransactionProcessorPath, "transaction processor"),
            Read(canonicalRoot, IntrinsicGasCalculatorPath, "intrinsic gas calculator"),
            Read(canonicalRoot, MainnetDiPath, "standard mainnet registration"),
        ];

        foreach (SourceFile source in sources)
        {
            RejectErrors(source);
        }

        SourceFile policy = sources[0];
        SourceFile processor = sources[1];
        SourceFile intrinsic = sources[2];
        SourceFile mainnet = sources[3];

        FoldShape fold = ValidateFold(policy);
        InitialIntrinsicShape initialIntrinsic = ValidateInitialIntrinsic(policy, processor, intrinsic);
        (DelegationShape delegations, ReachabilityShape reachability) = ValidateDelegations(policy, processor, mainnet, fold);
        DependencyIdentity[] dependencies = ValidateDependencies(canonicalRoot);

        IrDocument document = new(
            SchemaVersion: 2,
            ExtractorVersion,
            AncestorBaselineCommit,
            SourceIdentityAuthority,
            KernelName,
            initialIntrinsic,
            reachability,
            delegations,
            fold,
            dependencies,
            ExternalObligations:
            [
                "This is an admission/fold control and normalized-effect execution claim; it is not a proof of the complete production authorization loop.",
                "The logical-versus-physical existence selectors are source-bound; authorization validity, signature recovery, nonce/chain-id/code checks, world-state read correctness, and authority mutation remain external.",
                "The normalized authorization effects supplied to the mathematical model are an assumed adapter boundary, not a source-derived translation or proof of ProcessDelegations validation predicates.",
                "Account-write execution gas is represented as a fixed-width Value charge; its pricing constant and EIP-8038 policy semantics remain external.",
                "StateGasCharge and StateGasTransition behavior is imported only through the exact pinned generated artifacts and theorem signatures recorded in Dependencies.",
                "The partial-failure result is the ProcessDelegations boundary state; the caller reset projection is modeled separately and does not assert atomicity of preparation.",
                "Lean's normalized-effect interpreter uses unbounded Nat/Int and requires nonnegative state costs; fixed-width C#/Lean equivalence additionally requires the named per-transition, delta, and fold no-wrap obligations.",
                "Snapshot restoration, world-state rollback, gas settlement, fees, receipts, tracing, DI resolution, CLR/JIT/AOT, and persistence are outside the claim.",
            ]);

        ValidateIrShape(document);
        byte[] irBytes = Serialize(document);
        string irHash = Sha256(irBytes);
        IrDocument serializedDocument = DeserializeIr(irBytes);
        ValidateRoundTrippedIr(document, serializedDocument);
        byte[] leanBytes = EmitLean(serializedDocument, irHash);

        string output = Path.GetFullPath(outputDirectory);
        string leanPath = Path.GetFullPath(leanOutputPath ?? Path.Combine(canonicalRoot, DefaultLeanPath));
        EnsureWithin(leanOutputPath is null ? canonicalRoot : output, leanPath);
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(Path.GetDirectoryName(leanPath)!);
        string irPath = Path.Combine(output, IrFileName);
        string manifestPath = Path.Combine(output, ManifestFileName);
        Write(irPath, irBytes);
        Write(leanPath, leanBytes);

        SourceIdentity[] sourceIdentities = sources
            .Select(static source => new SourceIdentity(source.RelativePath, source.Hash))
            .ToArray();
        Manifest manifest = new(
            SchemaVersion: 2,
            ExtractorVersion,
            CompilerVersion: typeof(CSharpCompilation).Assembly.GetName().Version?.ToString() ?? "unknown",
            LanguageVersion: LanguageVersion.CSharp14.ToDisplayString(),
            AncestorBaselineCommit,
            SourceIdentityAuthority,
            KernelName,
            Sources: sourceIdentities,
            Admissions: BuildAdmissions(sourceIdentities),
            Ir: new ArtifactIdentity(IrFileName, irHash),
            Lean: new ArtifactIdentity(Normalize(DefaultLeanPath), Sha256(leanBytes)),
            CombinedSha256: CombinedHash(sourceIdentities),
            Dependencies: dependencies,
            SemanticBindings: ExpectedSemanticBindings);
        byte[] manifestBytes = Serialize(manifest);
        Manifest serializedManifest = DeserializeManifest(manifestBytes);
        ValidateRoundTrippedManifest(manifest, serializedManifest);
        Write(manifestPath, manifestBytes);
        return new ExtractionResult(irPath, manifestPath, leanPath, sources.Length);
    }

    internal static void ValidateSerializedIr(byte[] sourceDerivedIr, byte[] candidateIr)
    {
        IrDocument sourceDerived = DeserializeIr(sourceDerivedIr);
        IrDocument candidate = DeserializeIr(candidateIr);
        ValidateRoundTrippedIr(sourceDerived, candidate);
    }

    internal static void ValidateSerializedManifest(byte[] sourceDerivedManifest, byte[] candidateManifest)
    {
        Manifest sourceDerived = DeserializeManifest(sourceDerivedManifest);
        Manifest candidate = DeserializeManifest(candidateManifest);
        ValidateRoundTrippedManifest(sourceDerived, candidate);
    }

    private static FoldShape ValidateFold(SourceFile source)
    {
        MethodDeclarationSyntax method = FindMethod(
            source,
            owner: "EthereumGasPolicy",
            name: "FoldTopFrameStateGas",
            returnType: "void",
            [
                new("gas", "EthereumGasPolicy", "ref"),
                new("baseline", "EthereumGasPolicy", "ref"),
                new("stateGasUsed", "long", string.Empty),
            ],
            expectedAccessibility: "public");
        RejectNestedFunctions(method);
        BlockSyntax body = method.Body ?? throw new ExtractionException("FoldTopFrameStateGas must have a block body.");
        if (body.Statements.Count != 5 || body.Statements[0] is not IfStatementSyntax noOp)
        {
            throw new ExtractionException("FoldTopFrameStateGas must contain the pinned guard and exactly four ordered field writes.");
        }

        string noOpPredicate = Canonical(noOp.Condition);
        if (noOpPredicate != "stateGasUsed<=0" || !IsBareReturn(noOp.Statement))
        {
            throw new ExtractionException("FoldTopFrameStateGas must guard all writes with stateGasUsed <= 0 and return without mutation.");
        }

        (string Owner, string Field, string Operator, string Value)[] expected =
        [
            ("baseline", "StateReservoir", "+=", "stateGasUsed"),
            ("baseline", "StateGasUsed", "+=", "stateGasUsed"),
            ("gas", "StateGasSpill", "=", "0"),
            ("gas", "StateGasSpillRefunded", "=", "0"),
        ];
        FoldWrite[] writes = new FoldWrite[expected.Length];
        SourceAnchor[] anchors = new SourceAnchor[expected.Length + 1];
        anchors[0] = Anchor(AdmissionStage.FoldTopFrameStateGas, method, noOp);
        for (int index = 0; index < expected.Length; index++)
        {
            if (body.Statements[index + 1] is not ExpressionStatementSyntax expression ||
                expression.Expression is not AssignmentExpressionSyntax assignment ||
                Canonical(assignment.Left) != $"{expected[index].Owner}.{expected[index].Field}" ||
                assignment.OperatorToken.Text != expected[index].Operator ||
                Canonical(assignment.Right) != expected[index].Value)
            {
                throw new ExtractionException(
                    $"FoldTopFrameStateGas field write {index} changed; expected {expected[index].Owner}.{expected[index].Field} {expected[index].Operator} {expected[index].Value}.");
            }

            GasOwner owner = expected[index].Owner == "gas" ? GasOwner.GasAvailable : GasOwner.Baseline;
            GasField field = ParseGasField(expected[index].Field);
            SourceAnchor anchor = Anchor(AdmissionStage.FoldTopFrameStateGas, method, expression);
            anchors[index + 1] = anchor;
            writes[index] = new FoldWrite(owner, field, expected[index].Operator, expected[index].Value, index, anchor);
        }

        StructDeclarationSyntax policy = source.Root.DescendantNodes()
            .OfType<StructDeclarationSyntax>()
            .SingleOrDefault(item => item.Identifier.ValueText == "EthereumGasPolicy")
            ?? throw new ExtractionException("EthereumGasPolicy must be declared exactly once.");
        string[] gasPolicyFields = policy.Members
            .OfType<FieldDeclarationSyntax>()
            .SelectMany(static field => field.Declaration.Variables.Select(variable => variable.Identifier.ValueText))
            .ToArray();
        string[] expectedGasPolicyFields =
        ["Value", "StateReservoir", "StateGasUsed", "StateGasSpill", "StateGasSpillRefunded"];
        if (!gasPolicyFields.SequenceEqual(expectedGasPolicyFields, StringComparer.Ordinal))
            throw new ExtractionException("EthereumGasPolicy must retain exactly the five admitted gas-policy fields in source order.");

        return new FoldShape(MethodShapeOf(method), noOpPredicate, gasPolicyFields, writes, anchors);
    }

    private static InitialIntrinsicShape ValidateInitialIntrinsic(
        SourceFile policy,
        SourceFile processor,
        SourceFile intrinsic)
    {
        MethodDeclarationSyntax authorizationCost = FindMethod(
            intrinsic,
            owner: "IntrinsicGasCalculator",
            name: "AuthorizationListCost",
            returnType: "(ulongExecutionCost,longStateCost)",
            [
                new("transaction", "Transaction", string.Empty),
                new("spec", "IReleaseSpec", string.Empty),
            ],
            expectedAccessibility: "internal");
        LocalFunctionStatementSyntax authorizationListErrorHelper = ValidateAuthorizationListErrorHelper(authorizationCost);
        RejectNestedFunctions(authorizationCost, authorizationListErrorHelper);
        string authorizationBody = Canonical(authorizationCost.Body ?? throw new ExtractionException(
            "AuthorizationListCost must have a block body."));
        ReturnStatementSyntax[] authorizationReturns = authorizationCost.DescendantNodes()
            .OfType<ReturnStatementSyntax>()
            .ToArray();
        ConditionalExpressionSyntax? authorizationConditional = authorizationReturns
            .Select(static statement => statement.Expression)
            .OfType<ConditionalExpressionSyntax>()
            .SingleOrDefault();
        if (authorizationReturns.Length != 2 ||
            !authorizationReturns.Any(static statement => Canonical(statement.Expression) == "(0,0)") ||
            authorizationConditional is null ||
            Canonical(authorizationConditional.Condition) != "spec.IsEip8037Enabled" ||
            Canonical(authorizationConditional.WhenTrue) != "(authCount*perAuthExecution,0)" ||
            Canonical(authorizationConditional.WhenFalse) != "(authCount*GasCostOf.NewAccount,0)")
        {
            throw new ExtractionException("AuthorizationListCost must return zero intrinsic state cost on every admitted authorization-list branch.");
        }

        MethodDeclarationSyntax calculate = FindMethod(
            policy,
            owner: "EthereumGasPolicy",
            name: "Calculate",
            returnType: "IntrinsicGas<EthereumGasPolicy>",
            [
                new("tx", "Transaction", string.Empty),
                new("spec", "IReleaseSpec", string.Empty),
                new("blockGasLimit", "ulong", string.Empty),
            ],
            expectedAccessibility: "private");
        RejectNestedFunctions(calculate);
        ValidateIntrinsicStateCostProjection(calculate);
        string calculateBody = Canonical(calculate.Body ?? throw new ExtractionException("EthereumGasPolicy.Calculate must have a block body."));
        RequireContains(calculateBody, "IntrinsicGasCalculator.AuthorizationListCost(tx,spec)",
            "EthereumGasPolicy.Calculate must derive auth state cost from AuthorizationListCost.");
        RequireContains(calculateBody, "longtotalStateCost=authStateCost;",
            "EthereumGasPolicy.Calculate must retain the named totalStateCost projection.");
        RequireContains(calculateBody, "StateReservoir=totalStateCost",
            "EthereumGasPolicy.Calculate must project totalStateCost into intrinsic StateReservoir.");
        RequireContains(calculateBody, "StateGasUsed=totalStateCost",
            "EthereumGasPolicy.Calculate must project totalStateCost into intrinsic StateGasUsed.");
        _ = calculate.DescendantNodes()
            .OfType<ConditionalExpressionSyntax>()
            .SingleOrDefault(conditional =>
                Canonical(conditional.Condition) == "spec.IsEip8037Enabled" &&
                Canonical(conditional.WhenTrue).Contains("StateReservoir=totalStateCost", StringComparison.Ordinal) &&
                Canonical(conditional.WhenTrue).Contains("StateGasUsed=totalStateCost", StringComparison.Ordinal) &&
                Canonical(conditional.WhenFalse).Contains("FromULong(executionGas)", StringComparison.Ordinal))
            ?? throw new ExtractionException(
                "EthereumGasPolicy.Calculate must retain the EIP-8037 state-bearing intrinsic branch and standard false branch polarity.");

        MethodDeclarationSyntax execute = FindMethod(
            processor,
            owner: "TransactionProcessorBase",
            name: "Execute",
            returnType: "TransactionResult",
            [
                new("tx", "Transaction", string.Empty),
                new("tracer", "ITxTracer", string.Empty),
                new("opts", "ExecutionOptions", string.Empty),
                new("header", "BlockHeader", string.Empty),
                new("spec", "IReleaseSpec", string.Empty),
                new("intrinsicGas", "IntrinsicGas<TGasPolicy>", "in"),
            ],
            expectedAccessibility: "private");
        RejectNestedFunctions(execute);
        InvocationExpressionSyntax calculateAvailableGasCall = FindInvocation(
            execute,
            "CalculateAvailableGas",
            ["tx", "spec", "inintrinsicGas", "outTGasPolicygasAvailable"]);
        IfStatementSyntax calculateAvailableGasGuard = FindContainingIf(
            calculateAvailableGasCall,
            "!(result=CalculateAvailableGas(tx,spec,inintrinsicGas,outTGasPolicygasAvailable))");
        if (calculateAvailableGasGuard.Statement is not ReturnStatementSyntax calculateFailureReturn ||
            Canonical(calculateFailureReturn.Expression) != "result")
        {
            throw new ExtractionException("TransactionProcessorBase.Execute must immediately return a failed CalculateAvailableGas result.");
        }

        InvocationExpressionSyntax simpleTransferCall = FindInvocationByCallee(execute, "ExecuteSimpleTransfer");
        InvocationExpressionSyntax evmTransactionCall = FindInvocationByCallee(execute, "ExecuteEvmTransaction");
        if (calculateAvailableGasCall.FullSpan.Start >= simpleTransferCall.FullSpan.Start ||
            calculateAvailableGasCall.FullSpan.Start >= evmTransactionCall.FullSpan.Start)
        {
            throw new ExtractionException("TransactionProcessorBase.Execute must calculate available gas before either transaction execution route.");
        }

        MethodDeclarationSyntax calculateAvailableGas = FindMethod(
            processor,
            owner: "TransactionProcessorBase",
            name: "CalculateAvailableGas",
            returnType: "TransactionResult",
            [
                new("tx", "Transaction", string.Empty),
                new("spec", "IReleaseSpec", string.Empty),
                new("intrinsicGas", "IntrinsicGas<TGasPolicy>", "in"),
                new("gasAvailable", "TGasPolicy", "out"),
            ],
            expectedAccessibility: "protected");
        RejectNestedFunctions(calculateAvailableGas);
        InvocationExpressionSyntax availableGasForwardCall = FindInvocation(
            calculateAvailableGas,
            "TGasPolicy.TryCreateAvailableFromIntrinsic",
            ["tx.GasLimit", "intrinsicGas.Standard", "spec", "outgasAvailable"]);
        if (calculateAvailableGas.ExpressionBody?.Expression is not ConditionalExpressionSyntax availableGasResult ||
            availableGasResult.Condition != availableGasForwardCall ||
            Canonical(availableGasResult.WhenTrue) != "TransactionResult.Ok" ||
            Canonical(availableGasResult.WhenFalse) != "TransactionResult.GasLimitBelowIntrinsicGas")
        {
            throw new ExtractionException("TransactionProcessorBase.CalculateAvailableGas must directly forward the production gas initializer outcome.");
        }

        MethodDeclarationSyntax available = FindMethod(
            policy,
            owner: "EthereumGasPolicy",
            name: "TryCreateAvailableFromIntrinsic",
            returnType: "bool",
            [
                new("gasLimit", "ulong", string.Empty),
                new("intrinsicGas", "EthereumGasPolicy", "in"),
                new("spec", "IReleaseSpec", string.Empty),
                new("available", "EthereumGasPolicy", "out"),
            ],
            expectedAccessibility: "public");
        RejectNestedFunctions(available);
        InvocationExpressionSyntax initializationCall = FindInvocation(
            available,
            "TransactionGasInitializationKernel.TryCreate",
            [
                "gasLimit",
                "intrinsicGas.Value",
                "intrinsicGas.StateReservoir",
                "spec.IsEip8037Enabled",
                "Eip7825Constants.DefaultTxGasLimitCap",
            ]);
        string[] projection = ValidateAvailableGasInitialization(available, initializationCall);

        SourceAnchor[] anchors =
        [
            Anchor(AdmissionStage.InitialIntrinsicStateGas, authorizationCost, authorizationCost.ReturnType),
            Anchor(AdmissionStage.InitialIntrinsicStateGas, calculate, calculate.Body!),
            Anchor(AdmissionStage.CalculateAvailableGas, execute, calculateAvailableGasCall),
            Anchor(AdmissionStage.CalculateAvailableGas, calculateAvailableGas, availableGasForwardCall),
            Anchor(AdmissionStage.CalculateAvailableGas, available, initializationCall),
        ];
        return new InitialIntrinsicShape(
            MethodShapeOf(authorizationCost),
            ["(0,0)", "(authCount * perAuthExecution,0)", "(authCount * GasCostOf.NewAccount,0)"],
            MethodShapeOf(calculate),
            "IntrinsicGasCalculator.AuthorizationListCost(tx, spec)",
            "long totalStateCost = authStateCost",
            "StateReservoir = totalStateCost; StateGasUsed = totalStateCost",
            MethodShapeOf(execute),
            CallShapeOf(calculateAvailableGasCall, execute, AdmissionStage.CalculateAvailableGas),
            MethodShapeOf(calculateAvailableGas),
            CallShapeOf(availableGasForwardCall, calculateAvailableGas, AdmissionStage.CalculateAvailableGas),
            MethodShapeOf(available),
            CallShapeOf(initializationCall, available, AdmissionStage.CalculateAvailableGas),
            projection,
            anchors);
    }

    private static LocalFunctionStatementSyntax ValidateAuthorizationListErrorHelper(MethodDeclarationSyntax authorizationCost)
    {
        BlockSyntax body = authorizationCost.Body ?? throw new ExtractionException(
            "AuthorizationListCost must have a block body.");
        LocalFunctionStatementSyntax[] helpers = authorizationCost.DescendantNodes()
            .OfType<LocalFunctionStatementSyntax>()
            .ToArray();
        if (helpers.Length != 1 || body.Statements.LastOrDefault() is not LocalFunctionStatementSyntax helper ||
            helper != helpers[0] ||
            helper.AttributeLists.Count != 1 || Canonical(helper.AttributeLists[0]) != "[DoesNotReturn,StackTraceHidden]" ||
            helper.Modifiers.Count != 1 || !helper.Modifiers[0].IsKind(SyntaxKind.StaticKeyword) ||
            Canonical(helper.ReturnType) != "void" ||
            helper.Identifier.ValueText != "ThrowAuthorizationListNotEnabled" ||
            helper.TypeParameterList is not null || helper.ConstraintClauses.Count != 0 ||
            helper.ParameterList.Parameters is not [ParameterSyntax parameter] ||
            Canonical(parameter.Type) != "IReleaseSpec" || parameter.Identifier.ValueText != "releaseSpec" ||
            parameter.Modifiers.Count != 0 || parameter.Default is not null ||
            helper.Body is not null || helper.ExpressionBody?.Expression is not ThrowExpressionSyntax throwExpression ||
            throwExpression.Expression is not ObjectCreationExpressionSyntax exception ||
            Canonical(exception.Type) != "InvalidDataException" || exception.Initializer is not null ||
            exception.ArgumentList?.Arguments is not [ArgumentSyntax argument] ||
            argument.NameColon is not null || argument.RefKindKeyword.RawKind != 0 ||
            argument.Expression is not InterpolatedStringExpressionSyntax message ||
            message.Contents.OfType<InterpolationSyntax>().ToArray() is not [InterpolationSyntax interpolation] ||
            Canonical(interpolation.Expression) != "releaseSpec.Name")
        {
            throw new ExtractionException(
                "AuthorizationListCost must retain only its exact no-return authorization-list error helper.");
        }

        InvocationExpressionSyntax[] calls = authorizationCost.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => Canonical(invocation.Expression) == "ThrowAuthorizationListNotEnabled")
            .ToArray();
        if (calls.Length != 1 || Canonical(calls[0].ArgumentList) != "(spec)" ||
            FindContainingIf(calls[0], "!spec.IsAuthorizationListEnabled").Statement is not BlockSyntax errorBranch ||
            errorBranch.Statements is not [ExpressionStatementSyntax callStatement] ||
            callStatement.Expression != calls[0])
        {
            throw new ExtractionException(
                "AuthorizationListCost must invoke its no-return error helper only from the authorization-list-disabled branch.");
        }

        return helper;
    }

    private static void ValidateIntrinsicStateCostProjection(MethodDeclarationSyntax calculate)
    {
        BlockSyntax body = calculate.Body ?? throw new ExtractionException("EthereumGasPolicy.Calculate must have a block body.");
        string[] statements =
        [
            "ulongtokensInCallData=IntrinsicGasCalculator.CalculateTokensInCallData(tx,spec);",
            "ulongfloorTokensInAccessList=IntrinsicGasCalculator.CalculateFloorTokensInAccessList(tx,spec);",
            "(ulongauthExecutionCost,longauthStateCost)=IntrinsicGasCalculator.AuthorizationListCost(tx,spec);",
            "ulongaccessListCost=IntrinsicGasCalculator.AccessListCost(tx,spec,floorTokensInAccessList);",
            "ulongbaseCost=spec.IsEip2780Enabled?GasCostOf.TransactionEip2780:GasCostOf.Transaction;",
            "ulongcreateCost=CreateCost(tx,spec);",
            "ulongeip2780ExtraGas=Eip2780ExtraGas(tx,spec);",
            "ulongexecutionGas=baseCost+DataCost(tx,spec,tokensInCallData)+createCost+accessListCost+authExecutionCost+eip2780ExtraGas;",
            "ulongfloorBase=spec.IsEip2780Enabled?baseCost+createCost+eip2780ExtraGas:baseCost;",
            "ulongfloorCost=IntrinsicGasCalculator.CalculateFloorCost(tx,spec,floorBase,tokensInCallData,floorTokensInAccessList);",
            "longtotalStateCost=authStateCost;",
            "returnspec.IsEip8037Enabled?newIntrinsicGas<EthereumGasPolicy>(newEthereumGasPolicy{Value=executionGas,StateReservoir=totalStateCost,StateGasUsed=totalStateCost,},FromULong(floorCost)):newIntrinsicGas<EthereumGasPolicy>(FromULong(executionGas),FromULong(floorCost));",
        ];
        if (!body.Statements.Select(Canonical).SequenceEqual(statements, StringComparer.Ordinal))
        {
            throw new ExtractionException(
                "EthereumGasPolicy.Calculate must preserve the complete ordered authorization-cost dataflow and intrinsic return projection.");
        }
    }

    private static string[] ValidateAvailableGasInitialization(
        MethodDeclarationSyntax available,
        InvocationExpressionSyntax initializationCall)
    {
        BlockSyntax body = available.Body ?? throw new ExtractionException(
            "TryCreateAvailableFromIntrinsic must have a block body.");
        if (body.Statements.Count != 4 ||
            body.Statements[0] is not LocalDeclarationStatementSyntax resultDeclaration ||
            resultDeclaration.Declaration.Type.ToString() != "TransactionGasInitializationResult" ||
            resultDeclaration.Declaration.Variables.Count != 1 ||
            resultDeclaration.Declaration.Variables[0].Identifier.ValueText != "result" ||
            resultDeclaration.Declaration.Variables[0].Initializer?.Value != initializationCall ||
            body.Statements[1] is not IfStatementSyntax failureGuard ||
            Canonical(failureGuard.Condition) != "result.OutcomeisTransactionGasInitializationOutcome.IntrinsicGasExceedsLimit" ||
            failureGuard.Statement is not BlockSyntax failureBody || failureBody.Statements.Count != 2 ||
            failureBody.Statements[0] is not ExpressionStatementSyntax failureAssignmentStatement ||
            failureAssignmentStatement.Expression is not AssignmentExpressionSyntax failureAssignment ||
            Canonical(failureAssignment.Left) != "available" || failureAssignment.OperatorToken.Text != "=" ||
            Canonical(failureAssignment.Right) != "default" ||
            failureBody.Statements[1] is not ReturnStatementSyntax failureReturn ||
            Canonical(failureReturn.Expression) != "false" ||
            body.Statements[2] is not ExpressionStatementSyntax availableAssignmentStatement ||
            availableAssignmentStatement.Expression is not AssignmentExpressionSyntax availableAssignment ||
            Canonical(availableAssignment.Left) != "available" || availableAssignment.OperatorToken.Text != "=" ||
            availableAssignment.Right is not ObjectCreationExpressionSyntax availableCreation ||
            Canonical(availableCreation.Type) != "EthereumGasPolicy" || availableCreation.ArgumentList is not null ||
            availableCreation.Initializer is not InitializerExpressionSyntax initializer ||
            body.Statements[3] is not ReturnStatementSyntax successReturn ||
            Canonical(successReturn.Expression) != "true")
        {
            throw new ExtractionException(
                "TryCreateAvailableFromIntrinsic must preserve the kernel failure return and exact successful policy projection.");
        }

        string[] fields = ["Value", "StateReservoir", "StateGasUsed", "StateGasSpill", "StateGasSpillRefunded"];
        if (initializer.Expressions.Count != fields.Length)
        {
            throw new ExtractionException("TryCreateAvailableFromIntrinsic must project exactly five policy fields.");
        }

        string[] projection = new string[fields.Length];
        for (int index = 0; index < fields.Length; index++)
        {
            if (initializer.Expressions[index] is not AssignmentExpressionSyntax assignment ||
                assignment.OperatorToken.Text != "=" ||
                Canonical(assignment.Left) != fields[index] ||
                Canonical(assignment.Right) != $"result.{fields[index]}")
            {
                throw new ExtractionException(
                    $"TryCreateAvailableFromIntrinsic must project {fields[index]} from the initializer result in source order.");
            }

            projection[index] = $"{fields[index]}=result.{fields[index]}";
        }

        return projection;
    }

    private static ExecutionChargeShape ValidateExecutionCharge(SourceFile policy)
    {
        MethodDeclarationSyntax method = FindMethod(
            policy,
            owner: "EthereumGasPolicy",
            name: "UpdateGas",
            returnType: "bool",
            [
                new("gas", "EthereumGasPolicy", "ref"),
                new("gasCost", "ulong", string.Empty),
            ],
            expectedAccessibility: "public");
        RejectNestedFunctions(method);

        MethodDeclarationSyntax remainingGas = FindMethod(
            policy,
            owner: "EthereumGasPolicy",
            name: "GetRemainingGas",
            returnType: "ulong",
            [new("gas", "EthereumGasPolicy", "in")],
            expectedAccessibility: "public");
        string remainingGasExpression = Canonical(remainingGas.ExpressionBody?.Expression);
        if (remainingGasExpression != "gas.Value")
        {
            throw new ExtractionException("EthereumGasPolicy.GetRemainingGas must return gas.Value for the UpdateGas affordability guard.");
        }

        MethodDeclarationSyntax consumeRaw = FindMethod(
            policy,
            owner: "EthereumGasPolicy",
            name: "ConsumeRaw",
            returnType: "void",
            [
                new("gas", "EthereumGasPolicy", "ref"),
                new("cost", "ulong", string.Empty),
            ],
            expectedAccessibility: "private");
        string successMutation = Canonical(consumeRaw.ExpressionBody?.Expression);
        if (consumeRaw.ExpressionBody is null ||
            consumeRaw.ExpressionBody.Expression is not AssignmentExpressionSyntax consumeAssignment ||
            Canonical(consumeAssignment.Left) != "gas.Value" ||
            consumeAssignment.OperatorToken.Text != "-=" ||
            Canonical(consumeAssignment.Right) != "cost")
        {
            throw new ExtractionException("EthereumGasPolicy.ConsumeRaw must subtract cost from gas.Value.");
        }

        BlockSyntax body = method.Body ?? throw new ExtractionException("EthereumGasPolicy.UpdateGas must have a block body.");
        if (body.Statements.Count != 3 || body.Statements[0] is not IfStatementSyntax failureBranch ||
            failureBranch.Statement is not BlockSyntax failureBody || failureBody.Statements.Count != 2 ||
            failureBody.Statements[0] is not ExpressionStatementSyntax failureWrite ||
            failureWrite.Expression is not AssignmentExpressionSyntax failureAssignment ||
            Canonical(failureAssignment.Left) != "gas.Value" || failureAssignment.OperatorToken.Text != "=" ||
            Canonical(failureAssignment.Right) != "0" ||
            failureBody.Statements[1] is not ReturnStatementSyntax failureReturn ||
            Canonical(failureReturn.Expression) != "false" ||
            body.Statements[1] is not ExpressionStatementSyntax successCallStatement ||
            successCallStatement.Expression is not InvocationExpressionSyntax successCall ||
            Canonical(successCall.Expression) != "ConsumeRaw" ||
            !successCall.ArgumentList.Arguments.Select(static argument => Canonical(argument))
                .SequenceEqual(["refgas", "gasCost"], StringComparer.Ordinal) ||
            body.Statements[2] is not ReturnStatementSyntax successReturn ||
            Canonical(successReturn.Expression) != "true")
        {
            throw new ExtractionException("EthereumGasPolicy.UpdateGas must retain zero-on-failure and ConsumeRaw-on-success semantics.");
        }

        string guard = Canonical(failureBranch.Condition);
        if (guard != "GetRemainingGas(ingas)<gasCost")
            throw new ExtractionException("EthereumGasPolicy.UpdateGas affordability guard changed.");

        SourceAnchor[] anchors =
        [
            Anchor(AdmissionStage.ChargeAccountWriteExecution, remainingGas, remainingGas.ExpressionBody!.Expression),
            Anchor(AdmissionStage.ChargeAccountWriteExecution, consumeRaw, consumeRaw.ExpressionBody!.Expression),
            Anchor(AdmissionStage.ChargeAccountWriteExecution, method, failureBranch),
            Anchor(AdmissionStage.ChargeAccountWriteExecution, method, successCall),
            Anchor(AdmissionStage.ChargeAccountWriteExecution, method, successReturn),
        ];
        EnsureAnchorOrder(anchors, "execution charge semantics");
        return new ExecutionChargeShape(
            MethodShapeOf(method),
            MethodShapeOf(remainingGas),
            remainingGasExpression,
            guard,
            Canonical(failureBranch.Statement),
            CallShapeOf(successCall, method, AdmissionStage.ChargeAccountWriteExecution),
            MethodShapeOf(consumeRaw),
            successMutation,
            Canonical(successReturn),
            anchors);
    }

    private static StateChargeAdapterShape ValidateStateChargeAdapter(SourceFile policy)
    {
        MethodDeclarationSyntax method = FindMethod(
            policy,
            owner: "EthereumGasPolicy",
            name: "TryConsumeStateGas",
            returnType: "bool",
            [
                new("gas", "EthereumGasPolicy", "ref"),
                new("stateGasCost", "long", string.Empty),
            ],
            expectedAccessibility: "public");
        RejectNestedFunctions(method);

        InvocationExpressionSyntax kernelCall = FindInvocation(
            method,
            "StateGasChargeKernel.TryCharge",
            [
                "gas.Value",
                "gas.StateReservoir",
                "gas.StateGasUsed",
                "gas.StateGasSpill",
                "gas.StateGasSpillRefunded",
                "stateGasCost",
            ]);
        BlockSyntax body = method.Body ?? throw new ExtractionException("EthereumGasPolicy.TryConsumeStateGas must have a block body.");
        if (body.Statements.Count != 8 ||
            body.Statements[0] is not LocalDeclarationStatementSyntax resultDeclaration ||
            resultDeclaration.Declaration.Type.ToString() != "StateGasChargeResult" ||
            resultDeclaration.Declaration.Variables.Count != 1 ||
            resultDeclaration.Declaration.Variables[0].Identifier.ValueText != "result" ||
            resultDeclaration.Declaration.Variables[0].Initializer?.Value != kernelCall ||
            body.Statements[1] is not IfStatementSyntax outOfGasGuard ||
            Canonical(outOfGasGuard.Condition) != "result.OutcomeisStateGasChargeOutcome.OutOfGas" ||
            outOfGasGuard.Statement is not ReturnStatementSyntax outOfGasReturn ||
            Canonical(outOfGasReturn.Expression) != "false" ||
            body.Statements[7] is not ReturnStatementSyntax successReturn ||
            Canonical(successReturn.Expression) != "true")
        {
            throw new ExtractionException("EthereumGasPolicy.TryConsumeStateGas must call the pinned state-charge kernel, return false before mutation on OOG, project five fields, and return true.");
        }

        string[] fields = ["Value", "StateReservoir", "StateGasUsed", "StateGasSpill", "StateGasSpillRefunded"];
        string[] projection = new string[fields.Length];
        SourceAnchor[] anchors = new SourceAnchor[fields.Length + 3];
        anchors[0] = Anchor(AdmissionStage.ChargeNewAccountState, method, kernelCall);
        anchors[1] = Anchor(AdmissionStage.ChargeNewAccountState, method, outOfGasGuard);
        for (int index = 0; index < fields.Length; index++)
        {
            if (body.Statements[index + 2] is not ExpressionStatementSyntax statement ||
                statement.Expression is not AssignmentExpressionSyntax assignment ||
                assignment.OperatorToken.Text != "=" ||
                Canonical(assignment.Left) != $"gas.{fields[index]}" ||
                Canonical(assignment.Right) != $"result.{fields[index]}")
            {
                throw new ExtractionException($"EthereumGasPolicy.TryConsumeStateGas must project gas.{fields[index]} from result.{fields[index]} in exact field order.");
            }

            projection[index] = $"{fields[index]}=result.{fields[index]}";
            anchors[index + 2] = Anchor(AdmissionStage.ChargeNewAccountState, method, statement);
        }

        anchors[^1] = Anchor(AdmissionStage.ChargeNewAccountState, method, successReturn);
        EnsureAnchorOrder(anchors, "state-gas charge adapter semantics");
        return new StateChargeAdapterShape(
            MethodShapeOf(method),
            CallShapeOf(kernelCall, method, AdmissionStage.ChargeNewAccountState),
            Canonical(outOfGasGuard.Condition),
            projection,
            Canonical(successReturn.Expression),
            anchors);
    }

    private static (DelegationShape Delegations, ReachabilityShape Reachability) ValidateDelegations(
        SourceFile policy,
        SourceFile processor,
        SourceFile mainnet,
        FoldShape fold)
    {
        MethodDeclarationSyntax executeEvmTransaction = FindMethodByName(
            processor,
            owner: "TransactionProcessorBase",
            name: "ExecuteEvmTransaction",
            returnType: "TransactionResult",
            expectedAccessibility: "private");
        MethodDeclarationSyntax process = FindMethod(
            processor,
            owner: "TransactionProcessorBase",
            name: "ProcessDelegations",
            returnType: "bool",
            [
                new("tx", "Transaction", string.Empty),
                new("spec", "IReleaseSpec", string.Empty),
                new("accessTracker", "StackAccessTracker", "in"),
                new("gasAvailable", "TGasPolicy", "ref"),
                new("executionIntrinsicGasStandard", "TGasPolicy", "ref"),
                new("codeInsertRefunds", "long", "out"),
            ],
            expectedAccessibility: "private");
        RejectNestedFunctions(process);
        StateChargeAdapterShape stateChargeAdapter = ValidateStateChargeAdapter(policy);
        ExecutionChargeShape executionCharge = ValidateExecutionCharge(policy);
        MethodDeclarationSyntax stateGasUsed = FindMethod(
            policy,
            owner: "EthereumGasPolicy",
            name: "GetStateGasUsed",
            returnType: "long",
            [new("gas", "EthereumGasPolicy", "in")],
            expectedAccessibility: "public");
        if (Canonical(stateGasUsed.ExpressionBody?.Expression) != "gas.StateGasUsed")
        {
            throw new ExtractionException("EthereumGasPolicy.GetStateGasUsed must read gas.StateGasUsed for the authorization delta.");
        }

        InvocationExpressionSyntax executeCall = FindInvocation(
            executeEvmTransaction,
            "ProcessDelegations",
            [
                "tx",
                "spec",
                "accessTracker",
                "refgasAvailable",
                "refexecutionIntrinsicGasStandard",
                "outdelegationRefunds",
            ]);
        IfStatementSyntax executeGuard = FindContainingIf(executeCall, "spec.IsEip7702Enabled&&tx.HasAuthorizationList");
        string executeGuardText = Canonical(executeGuard.Condition);
        VariableDeclaratorSyntax executionIntrinsicBaseline = FindVariableInitializer(
            executeEvmTransaction,
            "executionIntrinsicGasStandard",
            "intrinsicGas.Standard");
        VariableDeclaratorSyntax prePreparation = FindVariableInitializer(executeEvmTransaction, "prePreparationGas", "gasAvailable");
        InvocationExpressionSyntax buildEnvironment = FindInvocationByCallee(executeEvmTransaction, "BuildExecutionEnvironment");
        if (executionIntrinsicBaseline.FullSpan.Start >= prePreparation.FullSpan.Start ||
            prePreparation.FullSpan.Start >= executeCall.FullSpan.Start || executeCall.FullSpan.Start >= buildEnvironment.FullSpan.Start)
        {
            throw new ExtractionException(
                "ExecuteEvmTransaction must initialize the fold baseline and prePreparationGas before ProcessDelegations and build the environment afterward.");
        }

        AssignmentExpressionSyntax topFrameFailure = FindAssignment(executeEvmTransaction, "topFrameOutOfGas", "true", "=");
        IfStatementSyntax callerFailure = FindContainingIf(topFrameFailure, "spec.IsEip8037Enabled");
        IfStatementSyntax processFailure = FindContainingIf(
            executeCall,
            "!ProcessDelegations(tx,spec,accessTracker,refgasAvailable,refexecutionIntrinsicGasStandard,outdelegationRefunds)");
        string callerFailureBody = Canonical(callerFailure);
        RequireContains(callerFailureBody, "topFrameOutOfGas=true", "EIP-8037 ProcessDelegations failure must set topFrameOutOfGas.");
        RequireContains(callerFailureBody, "returnTransactionResult.GasLimitBelowIntrinsicGas;",
            "pre-EIP-8037 ProcessDelegations failure must retain the gas-limit return.");
        if (callerFailure.Statement is not BlockSyntax eip8037FailureBody ||
            eip8037FailureBody.Statements.Count != 1 ||
            topFrameFailure.Parent is not ExpressionStatementSyntax topFrameFailureStatement ||
            eip8037FailureBody.Statements[0] != topFrameFailureStatement ||
            callerFailure.Else?.Statement is not BlockSyntax preEip8037FailureBody ||
            preEip8037FailureBody.Statements.Count != 1 ||
            preEip8037FailureBody.Statements[0] is not ReturnStatementSyntax preEip8037Return ||
            Canonical(preEip8037Return.Expression) != "TransactionResult.GasLimitBelowIntrinsicGas")
        {
            throw new ExtractionException(
                "The EIP-8037 top-frame out-of-gas assignment must be the true arm and the pre-EIP-8037 gas-limit result the false arm.");
        }
        if (processFailure.Statement is not BlockSyntax processFailureBody ||
            processFailureBody.Statements.Count != 1 || processFailureBody.Statements[0] != callerFailure ||
            callerFailure.Parent != processFailureBody)
        {
            throw new ExtractionException(
                "The EIP-8037 top-frame out-of-gas branch must be the direct ProcessDelegations failure branch.");
        }

        VariableDeclaratorSyntax snapshotValue = FindVariableInitializer(executeEvmTransaction, "preExecutionSnapshot", "Snapshot.Empty");
        VariableDeclaratorSyntax snapshotFlag = FindVariableInitializer(executeEvmTransaction, "hasPreExecutionSnapshot", "false");
        InvocationExpressionSyntax takeSnapshot = FindInvocation(executeEvmTransaction, "WorldState.TakeSnapshot", []);
        AssignmentExpressionSyntax snapshotAssignment = FindAssignment(
            executeEvmTransaction,
            "preExecutionSnapshot",
            "WorldState.TakeSnapshot()",
            "=");
        AssignmentExpressionSyntax snapshotFlagAssignment = FindAssignment(
            executeEvmTransaction,
            "hasPreExecutionSnapshot",
            "true",
            "=");
        IfStatementSyntax snapshotGuard = FindContainingIf(takeSnapshot, "spec.IsEip8037Enabled");
        IfStatementSyntax authorizationGuard = FindContainingIf(
            snapshotGuard,
            "spec.IsEip7702Enabled&&tx.HasAuthorizationList",
            includeSelf: false);
        if (authorizationGuard.Statement is not BlockSyntax authorizationBody ||
            snapshotGuard.Parent != authorizationBody ||
            snapshotGuard.Statement is not BlockSyntax snapshotBody ||
            snapshotAssignment.Parent is not ExpressionStatementSyntax snapshotStatement ||
            snapshotFlagAssignment.Parent is not ExpressionStatementSyntax snapshotFlagStatement ||
            snapshotBody.Statements.Count != 2 ||
            snapshotBody.Statements[0] != snapshotStatement ||
            snapshotBody.Statements[1] != snapshotFlagStatement ||
            snapshotValue.FullSpan.Start >= snapshotFlag.FullSpan.Start ||
            snapshotFlag.FullSpan.Start >= snapshotGuard.FullSpan.Start ||
            takeSnapshot.FullSpan.Start >= snapshotFlagAssignment.FullSpan.Start ||
            snapshotFlagAssignment.FullSpan.Start >= executeCall.FullSpan.Start)
        {
            throw new ExtractionException("The EIP-8037 authorization snapshot and presence flag must be initialized and set before ProcessDelegations inside the EIP-7702 guard.");
        }

        InvocationExpressionSyntax restore = FindInvocation(executeEvmTransaction, "WorldState.Restore", ["preExecutionSnapshot"]);
        AssignmentExpressionSyntax gasReset = FindAssignment(executeEvmTransaction, "gasAvailable", "prePreparationGas", "=");
        AssignmentExpressionSyntax intrinsicReset = FindAssignment(executeEvmTransaction, "executionIntrinsicGasStandard", "intrinsicGas.Standard", "=");
        AssignmentExpressionSyntax[] directBaselineAssignments = executeEvmTransaction.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(assignment => Canonical(assignment.Left) == "executionIntrinsicGasStandard")
            .ToArray();
        if (directBaselineAssignments.Length != 1 || directBaselineAssignments[0] != intrinsicReset)
        {
            throw new ExtractionException(
                "executionIntrinsicGasStandard may only be directly reset after top-frame EIP-8037 out-of-gas.");
        }
        IfStatementSyntax resetGuard = FindContainingIf(restore, "topFrameOutOfGas&&spec.IsEip8037Enabled");
        IfStatementSyntax restoreGuard = FindContainingIf(restore, "hasPreExecutionSnapshot");
        if (resetGuard.Parent != executeEvmTransaction.Body ||
            resetGuard.Statement is not BlockSyntax resetBody ||
            restoreGuard.Parent != resetBody ||
            restoreGuard.Statement is not BlockSyntax restoreBody ||
            restore.Parent is not ExpressionStatementSyntax restoreStatement ||
            restoreBody.Statements.Count != 1 || restoreBody.Statements[0] != restoreStatement ||
            gasReset.Parent is not ExpressionStatementSyntax gasResetStatement ||
            intrinsicReset.Parent is not ExpressionStatementSyntax intrinsicResetStatement ||
            resetBody.Statements.IndexOf(restoreGuard) != 0 ||
            resetBody.Statements.IndexOf(gasResetStatement) != 1 ||
            resetBody.Statements.IndexOf(intrinsicResetStatement) != 2)
        {
            throw new ExtractionException("Snapshot restore must be the optional first block, followed by both direct gas-reset statements inside the top-frame EIP-8037 out-of-gas branch.");
        }
        if (restore.FullSpan.Start >= gasReset.FullSpan.Start || gasReset.FullSpan.Start >= intrinsicReset.FullSpan.Start)
        {
            throw new ExtractionException("EIP-8037 partial authorization failure must restore the snapshot, then reset gasAvailable and executionIntrinsicGasStandard.");
        }

        string processBody = Canonical(process.Body ?? throw new ExtractionException("ProcessDelegations must have a block body."));
        RequireContains(processBody, "Debug.Assert(spec.IsEip7702Enabled&&tx.HasAuthorizationList);",
            "ProcessDelegations must retain its EIP-7702 precondition.");
        RequireContains(processBody, "codeInsertRefunds=0;", "ProcessDelegations must initialize codeInsertRefunds.");
        VariableDeclaratorSyntax preAuthorization = FindVariableInitializer(
            process,
            "preAuthorizationStateGasUsed",
            "TGasPolicy.GetStateGasUsed(ingasAvailable)");
        ForEachStatementSyntax authorizationLoop = process.DescendantNodes().OfType<ForEachStatementSyntax>().SingleOrDefault()
            ?? throw new ExtractionException("ProcessDelegations must contain exactly one authorization-list loop.");
        if (Canonical(authorizationLoop.Expression) != "tx.AuthorizationList")
        {
            throw new ExtractionException("ProcessDelegations must iterate the transaction authorization list directly.");
        }

        InvocationExpressionSyntax validAuthorization = FindInvocation(
            process,
            "IsValidForExecution",
            [
                "authTuple",
                "accessTracker",
                "spec",
                "outboolhasDelegation",
                "outstring?error",
            ]);
        IfStatementSyntax validGuard = FindIf(process, "authorizationResult!=AuthorizationTupleResult.Valid");
        if (validGuard.Else is null || validGuard.FullSpan.Start >= authorizationLoop.FullSpan.End)
        {
            throw new ExtractionException("ProcessDelegations must branch on IsValidForExecution before any valid-authorization state charge.");
        }

        AssignmentExpressionSyntax logicalEip8037Existence = FindAssignment(
            process,
            "logicalAccountExists",
            "!WorldState.IsDeadAccount(authority)",
            "=");
        AssignmentExpressionSyntax physicalEip8037Existence = FindAssignment(
            process,
            "physicalAccountExists",
            "logicalAccountExists||WorldState.AccountExists(authority)",
            "=");
        AssignmentExpressionSyntax physicalLegacyExistence = FindAssignment(
            process,
            "physicalAccountExists",
            "WorldState.AccountExists(authority)",
            "=");
        AssignmentExpressionSyntax logicalLegacyExistence = FindAssignment(
            process,
            "logicalAccountExists",
            "physicalAccountExists",
            "=");
        IfStatementSyntax existenceGuard = FindContainingIf(logicalEip8037Existence, "spec.IsEip8037Enabled");
        if (existenceGuard.Else is null ||
            !existenceGuard.Statement.FullSpan.Contains(logicalEip8037Existence.FullSpan) ||
            !existenceGuard.Statement.FullSpan.Contains(physicalEip8037Existence.FullSpan) ||
            !existenceGuard.Else.Statement.FullSpan.Contains(physicalLegacyExistence.FullSpan) ||
            !existenceGuard.Else.Statement.FullSpan.Contains(logicalLegacyExistence.FullSpan) ||
            logicalEip8037Existence.FullSpan.Start >= physicalEip8037Existence.FullSpan.Start ||
            physicalLegacyExistence.FullSpan.Start >= logicalLegacyExistence.FullSpan.Start ||
            validGuard.Else is null || !validGuard.Else.Statement.FullSpan.Contains(existenceGuard.FullSpan))
        {
            throw new ExtractionException(
                "ProcessDelegations must classify EIP-8037 authority existence logically while preserving physical existence for mutation.");
        }

        InvocationExpressionSyntax newAccountCharge = FindInvocation(
            process,
            "TGasPolicy.TryConsumeStateGas",
            ["refgasAvailable", "TGasPolicy.GetNewAccountStateCost()"]);
        InvocationExpressionSyntax accountWriteCharge = FindInvocation(
            process,
            "TGasPolicy.UpdateGas",
            ["refgasAvailable", "accountWriteGas"]);
        InvocationExpressionSyntax perAuthorizationCharge = FindInvocation(
            process,
            "TGasPolicy.TryConsumeStateGas",
            ["refgasAvailable", "TGasPolicy.GetPerAuthBaseStateCost()"]);
        IfStatementSyntax newAccountGuard = FindContainingIf(
            newAccountCharge,
            "!logicalAccountExists&&!TGasPolicy.TryConsumeStateGas(refgasAvailable,TGasPolicy.GetNewAccountStateCost())");
        IfStatementSyntax accountWriteGuard = FindContainingIf(
            accountWriteCharge,
            "accountWriteGas!=0&&!TGasPolicy.UpdateGas(refgasAvailable,accountWriteGas)");
        IfStatementSyntax perAuthorizationGuard = FindContainingIf(
            perAuthorizationCharge,
            "!delegatedBefore&&delegationSetFor!.Add(authority)&&!TGasPolicy.TryConsumeStateGas(refgasAvailable,TGasPolicy.GetPerAuthBaseStateCost())");
        IfStatementSyntax eip8037ChargeGuard = FindContainingIf(newAccountCharge, "spec.IsEip8037Enabled");
        IfStatementSyntax firstWriteGuard = FindContainingIf(accountWriteCharge, "writtenAccounts!.Add(authority)");
        IfStatementSyntax nonClearGuard = FindContainingIf(perAuthorizationCharge, "!clearsDelegation");
        if (!eip8037ChargeGuard.Statement.FullSpan.Contains(accountWriteCharge.FullSpan) ||
            !eip8037ChargeGuard.Statement.FullSpan.Contains(perAuthorizationCharge.FullSpan) ||
            !firstWriteGuard.Statement.FullSpan.Contains(accountWriteGuard.FullSpan) ||
            !nonClearGuard.Statement.FullSpan.Contains(perAuthorizationGuard.FullSpan) ||
            validGuard.Else is null || !validGuard.Else.Statement.FullSpan.Contains(eip8037ChargeGuard.FullSpan) ||
            !authorizationLoop.Statement.FullSpan.Contains(validGuard.FullSpan))
        {
            throw new ExtractionException("Authorization charges must remain in the valid/EIP-8037 branch with the first-write and non-clear guards preserved.");
        }

        ChargeShape[] charges =
        [
            new(
                ChargeKind.NewAccountState,
                "TGasPolicy.TryConsumeStateGas",
                ["refgasAvailable", "TGasPolicy.GetNewAccountStateCost()"],
                Canonical(newAccountGuard.Condition),
                newAccountCharge.FullSpan.Start,
                Anchor(AdmissionStage.ChargeNewAccountState, process, newAccountCharge)),
            new(
                ChargeKind.AccountWriteExecution,
                "TGasPolicy.UpdateGas",
                ["refgasAvailable", "accountWriteGas"],
                Canonical(accountWriteGuard.Condition),
                accountWriteCharge.FullSpan.Start,
                Anchor(AdmissionStage.ChargeAccountWriteExecution, process, accountWriteCharge)),
            new(
                ChargeKind.PerAuthorizationState,
                "TGasPolicy.TryConsumeStateGas",
                ["refgasAvailable", "TGasPolicy.GetPerAuthBaseStateCost()"],
                Canonical(perAuthorizationGuard.Condition),
                perAuthorizationCharge.FullSpan.Start,
                Anchor(AdmissionStage.ChargePerAuthorizationState, process, perAuthorizationCharge)),
        ];
        if (charges[0].Order >= charges[1].Order || charges[1].Order >= charges[2].Order)
        {
            throw new ExtractionException("Authorization charges must remain ordered new-account state, account-write execution, then per-authorization state.");
        }

        VariableDeclaratorSyntax accountWriteGas = FindVariableInitializer(
            process,
            "accountWriteGas",
            "spec.IsEip8038Enabled?Eip8038Constants.AccountWrite:0");
        _ = accountWriteGas;

        InvocationExpressionSyntax authorityMutation = FindInvocation(process, "_codeInfoRepository.SetDelegation", ["authTuple.CodeAddress", "authority", "spec"]);
        InvocationExpressionSyntax createAccount = FindInvocation(process, "WorldState.CreateAccount", ["authority", "0", "1"]);
        InvocationExpressionSyntax incrementNonce = FindInvocation(process, "WorldState.IncrementNonce", ["authority"]);
        IfStatementSyntax authorityCreationGuard = FindContainingIf(createAccount, "!physicalAccountExists");
        if (authorityCreationGuard.Else is null ||
            !authorityCreationGuard.Else.Statement.FullSpan.Contains(incrementNonce.FullSpan) ||
            Math.Min(createAccount.FullSpan.Start, incrementNonce.FullSpan.Start) <= charges[2].Order ||
            authorityMutation.FullSpan.Start <= Math.Max(createAccount.FullSpan.Start, incrementNonce.FullSpan.Start))
        {
            throw new ExtractionException("Authority state mutation must occur only after all authorization charges and SetDelegation must remain last.");
        }

        VariableDeclaratorSyntax deltaVariable = FindVariableInitializer(
            process,
            "authorizationStateGasUsed",
            "TGasPolicy.GetStateGasUsed(ingasAvailable)-preAuthorizationStateGasUsed");
        InvocationExpressionSyntax foldCall = FindInvocation(
            process,
            "TGasPolicy.FoldTopFrameStateGas",
            ["refgasAvailable", "refexecutionIntrinsicGasStandard", "authorizationStateGasUsed"]);
        IfStatementSyntax foldGuard = FindContainingIf(foldCall, "spec.IsEip8037Enabled");
        if (deltaVariable.FullSpan.Start >= foldCall.FullSpan.Start || authorizationLoop.FullSpan.End >= deltaVariable.FullSpan.Start ||
            foldGuard.FullSpan.Start <= authorizationLoop.FullSpan.Start)
        {
            throw new ExtractionException("authorizationStateGasUsed and FoldTopFrameStateGas must follow the complete authorization loop and execute only on its success path.");
        }

        ReturnStatementSyntax[] falseReturns = process.DescendantNodes()
            .OfType<ReturnStatementSyntax>()
            .Where(static statement => statement.Expression is not null && Canonical(statement.Expression) == "false")
            .ToArray();
        if (falseReturns.Length != 3 || falseReturns.Any(statement =>
                !statement.Ancestors().OfType<IfStatementSyntax>().Any(ifStatement =>
                    ifStatement.FullSpan.Contains(statement.FullSpan) &&
                    ifStatement.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(invocation =>
                        invocation.Expression is MemberAccessExpressionSyntax member &&
                        (Canonical(member) == "TGasPolicy.TryConsumeStateGas" || Canonical(member) == "TGasPolicy.UpdateGas")))))
        {
            throw new ExtractionException("Every ProcessDelegations false return must be a charge-failure return, with partial prior state left explicit.");
        }

        IfStatementSyntax[] failureGuards = [newAccountGuard, accountWriteGuard, perAuthorizationGuard];
        if (failureGuards.Any(guard => guard.Statement is not BlockSyntax block ||
                block.Statements.Count != 1 || block.Statements[0] is not ReturnStatementSyntax statement ||
                Canonical(statement.Expression) != "false"))
        {
            throw new ExtractionException("Each admitted authorization charge failure guard must immediately return false without an intervening mutation.");
        }

        SourceAnchor[] anchors =
        [
            Anchor(AdmissionStage.ProcessDelegationsGuard, executeEvmTransaction, executeGuard),
            Anchor(AdmissionStage.CapturePreAuthorizationStateGasUsed, process, preAuthorization),
            Anchor(AdmissionStage.ValidateAuthorization, process, validAuthorization),
            charges[0].Anchor,
            charges[1].Anchor,
            charges[2].Anchor,
            Anchor(AdmissionStage.ApplyAuthorityMutation, process, authorityMutation),
            Anchor(AdmissionStage.ComputeAuthorizationStateGasDelta, process, deltaVariable),
            Anchor(AdmissionStage.FoldTopFrameStateGas, process, foldCall),
        ];
        EnsureAnchorOrder(anchors, "authorization/fold call order");

        MethodShape processorMethod = MethodShapeOf(process);
        string authorityMutationOrder =
            "classify logical existence for state gas and physical existence for mutation; charge state (new account), charge execution (first account write), charge state (first non-clear delegation), then CreateAccount/IncrementNonce and SetDelegation";
        FailureShape failure = new(
            "false",
            "spec.IsEip8037Enabled -> topFrameOutOfGas = true; otherwise GasLimitBelowIntrinsicGas",
            "false returns expose gasAvailable after all prior successful charges; account-write OOG clears Value, while per-authorization state OOG preserves earlier fields; no rollback occurs inside ProcessDelegations",
            "EIP-7702/EIP-8037 snapshot capture -> top-frame EIP-8037 OOG -> optional snapshot restore -> gasAvailable = prePreparationGas -> executionIntrinsicGasStandard = intrinsicGas.Standard",
            [
                Anchor(AdmissionStage.ProcessDelegationsGuard, executeEvmTransaction, takeSnapshot),
                Anchor(AdmissionStage.ProcessDelegationsGuard, executeEvmTransaction, callerFailure),
                Anchor(AdmissionStage.ProcessDelegationsGuard, executeEvmTransaction, restore),
                Anchor(AdmissionStage.ProcessDelegationsGuard, executeEvmTransaction, gasReset),
                Anchor(AdmissionStage.ProcessDelegationsGuard, executeEvmTransaction, intrinsicReset),
            ]);
        DelegationShape delegation = new(
            processorMethod,
            CallShapeOf(executeCall, executeEvmTransaction, AdmissionStage.ProcessDelegationsGuard),
            executeGuardText,
            "TGasPolicy.GetStateGasUsed(in gasAvailable)",
            charges,
            Canonical(validGuard.Condition),
            authorityMutationOrder,
            new DeltaShape(
                "authorizationStateGasUsed",
                "TGasPolicy.GetStateGasUsed(in gasAvailable)",
                "-",
                "preAuthorizationStateGasUsed",
                Anchor(AdmissionStage.ComputeAuthorizationStateGasDelta, process, deltaVariable)),
            CallShapeOf(foldCall, process, AdmissionStage.FoldTopFrameStateGas),
            stateChargeAdapter,
            executionCharge,
            failure,
            anchors);

        if (FindType(processor.Root, "TransactionProcessorBase", 1) is not ClassDeclarationSyntax genericProcessor ||
            FindType(processor.Root, "EthereumTransactionProcessor", 0) is not ClassDeclarationSyntax ethereumProcessor ||
            FindType(processor.Root, "EthereumTransactionProcessorBase", 0) is not ClassDeclarationSyntax ethereumBase ||
            !HasModifier(genericProcessor.Modifiers, SyntaxKind.PublicKeyword) ||
            !HasModifier(genericProcessor.Modifiers, SyntaxKind.AbstractKeyword) ||
            genericProcessor.BaseList?.Types is not [BaseTypeSyntax genericProcessorBase, BaseTypeSyntax genericProcessorInterface] ||
            Canonical(genericProcessorBase.Type) != "TransactionProcessorBase" ||
            Canonical(genericProcessorInterface.Type) != "ITransactionProcessor" ||
            !HasModifier(ethereumProcessor.Modifiers, SyntaxKind.PublicKeyword) ||
            !HasModifier(ethereumProcessor.Modifiers, SyntaxKind.SealedKeyword) ||
            ethereumProcessor.BaseList?.Types is not [BaseTypeSyntax ethereumProcessorBase] ||
            Canonical(ethereumProcessorBase.Type) != "EthereumTransactionProcessorBase" ||
            ethereumProcessor.Members.Count != 0 ||
            !HasModifier(ethereumBase.Modifiers, SyntaxKind.PublicKeyword) ||
            !HasModifier(ethereumBase.Modifiers, SyntaxKind.AbstractKeyword) ||
            ethereumBase.BaseList?.Types is not [BaseTypeSyntax ethereumBaseBase] ||
            Canonical(ethereumBaseBase.Type) != "TransactionProcessorBase<EthereumGasPolicy>" ||
            ethereumBase.Members.Count != 0)
        {
            throw new ExtractionException("The admitted processor inheritance path changed.");
        }

        MethodDeclarationSyntax[] standardAvailableGasOverrides = processor.Root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == "CalculateAvailableGas" &&
                method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault() is TypeDeclarationSyntax owner &&
                (owner.Identifier.ValueText == "EthereumTransactionProcessor" ||
                 owner.Identifier.ValueText == "EthereumTransactionProcessorBase"))
            .ToArray();
        if (standardAvailableGasOverrides.Length != 0)
        {
            throw new ExtractionException("The standard Ethereum transaction-processor path must not override CalculateAvailableGas.");
        }

        const string registration = ".AddScoped<ITransactionProcessor,EthereumTransactionProcessor>()";
        if (FindType(mainnet.Root, "BlockProcessingModule", 0) is not ClassDeclarationSyntax blockProcessingModule)
        {
            throw new ExtractionException("The standard mainnet BlockProcessingModule declaration changed.");
        }
        MethodDeclarationSyntax load = blockProcessingModule.Members
            .OfType<MethodDeclarationSyntax>()
            .SingleOrDefault(method => method.Identifier.ValueText == "Load" && method.ParameterList.Parameters.Count == 1)
            ?? throw new ExtractionException("The standard mainnet BlockProcessingModule.Load declaration changed.");

        InvocationExpressionSyntax[] registrationMatches = mainnet.Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => invocation.Ancestors().Contains(load) &&
                TargetsTransactionProcessor(invocation))
            .ToArray();
        if (registrationMatches.Length != 1)
            throw new ExtractionException("The standard mainnet registration anchor is not a unique invocation.");
        InvocationExpressionSyntax registrationInvocation = registrationMatches[0];
        if (load.Body is null || registrationInvocation.ArgumentList.Arguments.Count != 0 ||
            registrationInvocation.Expression is not MemberAccessExpressionSyntax registrationMember ||
            registrationMember.Name is not GenericNameSyntax registrationName ||
            registrationName.Identifier.ValueText != "AddScoped" ||
            !registrationName.TypeArgumentList.Arguments.Select(Canonical).SequenceEqual(
                ["ITransactionProcessor", "EthereumTransactionProcessor"], StringComparer.Ordinal) ||
            !IsBuilderFluentChain(registrationMember.Expression) ||
            registrationInvocation.Ancestors().Any(static ancestor =>
                ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax or IfStatementSyntax or
                ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax) ||
            registrationInvocation.Ancestors().OfType<ExpressionStatementSyntax>().FirstOrDefault() is not ExpressionStatementSyntax registrationStatement ||
            registrationStatement.Parent != load.Body ||
            load.Body.Statements.FirstOrDefault() != registrationStatement)
        {
            throw new ExtractionException(
                "The standard mainnet ITransactionProcessor registration must remain an unconditional direct Load-body chain.");
        }
        ReachabilityShape reachability = new(
            "Nethermind.Evm.TransactionProcessing.EthereumTransactionProcessor",
            "Nethermind.Evm.TransactionProcessing.EthereumTransactionProcessorBase",
            "Nethermind.Evm.TransactionProcessing.TransactionProcessorBase<EthereumGasPolicy>",
            registration,
            Anchor(AdmissionStage.ProcessDelegationsGuard, load, registrationInvocation));

        return (delegation, reachability);
    }

    private static DependencyIdentity[] ValidateDependencies(string root)
    {
        DependencyIdentity[] dependencies = new DependencyIdentity[DependencyExpectations.Length];
        for (int index = 0; index < DependencyExpectations.Length; index++)
        {
            DependencyExpectation expectation = DependencyExpectations[index];
            string manifestFullPath = Path.GetFullPath(Path.Combine(root, expectation.ManifestPath));
            string refinementFullPath = Path.GetFullPath(Path.Combine(root, expectation.RefinementPath));
            EnsureWithin(root, manifestFullPath);
            EnsureWithin(root, refinementFullPath);
            if (!File.Exists(manifestFullPath) || !File.Exists(refinementFullPath))
            {
                throw new ExtractionException($"Missing accepted dependency artifacts for {expectation.Name}.");
            }

            byte[] manifestBytes = File.ReadAllBytes(manifestFullPath);
            byte[] refinementBytes = File.ReadAllBytes(refinementFullPath);
            string manifestHash = Sha256(manifestBytes);
            if (manifestHash != expectation.ManifestSha256)
            {
                throw new ExtractionException($"Accepted {expectation.Name} manifest identity changed; found SHA-256 {manifestHash}.");
            }
            string refinementHash = Sha256(refinementBytes);
            if (refinementHash != expectation.RefinementSha256)
            {
                throw new ExtractionException($"Accepted {expectation.Name} refinement identity changed; found SHA-256 {refinementHash}.");
            }

            using JsonDocument manifest = ParseDependencyManifest(manifestBytes, expectation);
            string rootName = GetString(manifest.RootElement, "root", expectation.Name);
            string[] rootSignatures = GetStringArray(manifest.RootElement, "rootSignatures", expectation.Name);
            if (rootName != expectation.RootName || rootSignatures.Length == 0 || rootSignatures[0] != expectation.RootSignature)
            {
                throw new ExtractionException($"Accepted {expectation.Name} root signature changed.");
            }

            JsonElement sourceArtifact = manifest.RootElement.GetProperty("source");
            string sourcePath = GetString(sourceArtifact, "path", expectation.Name);
            string expectedSourceHash = GetString(sourceArtifact, "sha256", expectation.Name);
            if (Normalize(sourcePath) != Normalize(expectation.SourcePath) ||
                expectedSourceHash != expectation.SourceSha256)
            {
                throw new ExtractionException($"Accepted {expectation.Name} source identity changed.");
            }
            string sourceFullPath = Path.GetFullPath(Path.Combine(root, sourcePath));
            EnsureWithin(root, sourceFullPath);
            if (!File.Exists(sourceFullPath))
            {
                throw new ExtractionException($"Accepted {expectation.Name} source is missing: {sourcePath}.");
            }
            string actualSourceHash = Sha256(File.ReadAllBytes(sourceFullPath));
            if (actualSourceHash != expectedSourceHash)
            {
                throw new ExtractionException($"Accepted {expectation.Name} source hash does not match its source manifest; found SHA-256 {actualSourceHash}.");
            }

            JsonElement leanArtifact = manifest.RootElement.GetProperty("leanArtifact");
            string leanPath = GetString(leanArtifact, "path", expectation.Name);
            string expectedLeanHash = GetString(leanArtifact, "sha256", expectation.Name);
            string leanFullPath = Path.GetFullPath(Path.Combine(root, leanPath));
            EnsureWithin(root, leanFullPath);
            if (!File.Exists(leanFullPath))
            {
                throw new ExtractionException($"Accepted {expectation.Name} Lean artifact is missing: {leanPath}.");
            }
            string actualLeanHash = Sha256(File.ReadAllBytes(leanFullPath));
            if (actualLeanHash != expectedLeanHash)
            {
                throw new ExtractionException($"Accepted {expectation.Name} Lean artifact hash does not match its source manifest.");
            }
            if (actualLeanHash != expectation.LeanSha256)
            {
                throw new ExtractionException($"Accepted {expectation.Name} Lean artifact identity changed; found SHA-256 {actualLeanHash}.");
            }

            string theoremSignature = ExtractTheoremSignature(
                Encoding.UTF8.GetString(refinementBytes),
                expectation.TheoremName,
                expectation.RequiredFragments);
            string theoremSignatureHash = Sha256(Encoding.UTF8.GetBytes(theoremSignature));
            if (theoremSignatureHash != expectation.TheoremSignatureSha256)
            {
                throw new ExtractionException($"Accepted {expectation.Name} theorem signature identity changed; found SHA-256 {theoremSignatureHash}.");
            }
            dependencies[index] = new DependencyIdentity(
                expectation.Name,
                Normalize(expectation.ManifestPath),
                manifestHash,
                Normalize(expectation.RefinementPath),
                refinementHash,
                Normalize(leanPath),
                actualLeanHash,
                expectation.RootSignature,
                expectation.TheoremName,
                theoremSignature);
        }

        return dependencies;
    }

    private static void ValidateIrShape(IrDocument document)
    {
        if (document is null)
            throw new ExtractionException("The authorization state-gas IR was null.");

        if (document.SchemaVersion != 2 || document.ExtractorVersion != ExtractorVersion ||
            document.AncestorBaselineCommit != AncestorBaselineCommit ||
            document.SourceIdentityAuthority != SourceIdentityAuthority ||
            document.Kernel != KernelName)
        {
            throw new ExtractionException("The authorization state-gas IR header changed.");
        }

        RequireTextArray(document.ExternalObligations, "ExternalObligations");
        if (document.ExternalObligations.Length < 8)
            throw new ExtractionException("The authorization state-gas IR must retain its external-obligation boundary.");

        ValidateInitialIntrinsicShape(document.InitialIntrinsic);
        ValidateReachabilityShape(document.Reachability);
        ValidateDelegationShape(document.Delegations);
        ValidateFoldShape(document.Fold);
        ValidateDependenciesShape(document.Dependencies);
    }

    private static void ValidateInitialIntrinsicShape(InitialIntrinsicShape shape)
    {
        if (shape is null)
            throw new ExtractionException("The authorization state-gas IR initial-intrinsic shape was null.");

        ValidateMethodShape(shape.AuthorizationCostMethod, "InitialIntrinsic.AuthorizationCostMethod");
        RequireTextArray(shape.ZeroStateCostReturns, "InitialIntrinsic.ZeroStateCostReturns");
        if (shape.ZeroStateCostReturns.Length != 3)
            throw new ExtractionException("InitialIntrinsic must retain all three zero-state-cost return forms.");
        ValidateMethodShape(shape.CalculateMethod, "InitialIntrinsic.CalculateMethod");
        RequireText(shape.CostCall, "InitialIntrinsic.CostCall");
        RequireText(shape.TotalStateCostAssignment, "InitialIntrinsic.TotalStateCostAssignment");
        RequireText(shape.StandardStateProjection, "InitialIntrinsic.StandardStateProjection");
        ValidateMethodShape(shape.ExecuteMethod, "InitialIntrinsic.ExecuteMethod");
        ValidateCallShape(shape.CalculateAvailableGasCall, "InitialIntrinsic.CalculateAvailableGasCall");
        ValidateMethodShape(shape.CalculateAvailableGasMethod, "InitialIntrinsic.CalculateAvailableGasMethod");
        ValidateCallShape(shape.AvailableGasForwardCall, "InitialIntrinsic.AvailableGasForwardCall");
        ValidateMethodShape(shape.AvailableGasMethod, "InitialIntrinsic.AvailableGasMethod");
        ValidateCallShape(shape.InitializationKernelCall, "InitialIntrinsic.InitializationKernelCall");
        RequireTextArray(shape.AvailableFieldProjection, "InitialIntrinsic.AvailableFieldProjection");
        if (shape.AvailableFieldProjection.Length != 5 ||
            !shape.AvailableFieldProjection.SequenceEqual(
                ["Value=result.Value", "StateReservoir=result.StateReservoir", "StateGasUsed=result.StateGasUsed",
                 "StateGasSpill=result.StateGasSpill", "StateGasSpillRefunded=result.StateGasSpillRefunded"],
                StringComparer.Ordinal))
        {
            throw new ExtractionException("InitialIntrinsic must project exactly the five gas-policy fields.");
        }
        RequireAnchors(shape.Anchors, "InitialIntrinsic.Anchors");
    }

    private static void ValidateReachabilityShape(ReachabilityShape shape)
    {
        if (shape is null)
            throw new ExtractionException("The authorization state-gas IR reachability shape was null.");
        RequireText(shape.ConcreteProcessor, "Reachability.ConcreteProcessor");
        RequireText(shape.EthereumBase, "Reachability.EthereumBase");
        RequireText(shape.ClosedGenericBase, "Reachability.ClosedGenericBase");
        RequireText(shape.Registration, "Reachability.Registration");
        ValidateAnchor(shape.RegistrationAnchor, "Reachability.RegistrationAnchor");
    }

    private static void ValidateDelegationShape(DelegationShape shape)
    {
        if (shape is null)
            throw new ExtractionException("The authorization state-gas IR delegation shape was null.");

        ValidateMethodShape(shape.Method, "Delegations.Method");
        ValidateCallShape(shape.ExecuteCall, "Delegations.ExecuteCall");
        RequireText(shape.ExecuteGuard, "Delegations.ExecuteGuard");
        RequireText(shape.InitialStateUsedExpression, "Delegations.InitialStateUsedExpression");
        if (shape.Charges is null || shape.Charges.Length != 3)
            throw new ExtractionException("Delegations must contain exactly three ordered charge shapes.");
        foreach (ChargeShape charge in shape.Charges)
            ValidateChargeShape(charge);
        RequireText(shape.ValidAuthorizationGuard, "Delegations.ValidAuthorizationGuard");
        RequireText(shape.AuthorityMutationOrder, "Delegations.AuthorityMutationOrder");
        ValidateDeltaShape(shape.Delta, "Delegations.Delta");
        ValidateCallShape(shape.FoldCall, "Delegations.FoldCall");
        ValidateStateChargeAdapterShape(shape.StateChargeAdapter);
        ValidateExecutionChargeShape(shape.ExecutionCharge);
        ValidateFailureShape(shape.Failure);
        RequireAnchors(shape.Anchors, "Delegations.Anchors");
    }

    private static void ValidateFoldShape(FoldShape shape)
    {
        if (shape is null)
            throw new ExtractionException("The authorization state-gas IR fold shape was null.");
        ValidateMethodShape(shape.Method, "Fold.Method");
        RequireText(shape.NoOpPredicate, "Fold.NoOpPredicate");
        RequireTextArray(shape.GasPolicyFields, "Fold.GasPolicyFields");
        if (!shape.GasPolicyFields.SequenceEqual(
                ["Value", "StateReservoir", "StateGasUsed", "StateGasSpill", "StateGasSpillRefunded"],
                StringComparer.Ordinal))
            throw new ExtractionException("Fold.GasPolicyFields must contain exactly the five production gas-policy fields.");
        if (shape.NoOpPredicate != "stateGasUsed<=0" || shape.Writes is null || shape.Writes.Length != 4)
            throw new ExtractionException("Fold must retain the nonpositive guard and exactly four writes.");

        (GasOwner Owner, GasField Field, string Operator, string Value)[] expected =
        [
            (GasOwner.Baseline, GasField.StateReservoir, "+=", "stateGasUsed"),
            (GasOwner.Baseline, GasField.StateGasUsed, "+=", "stateGasUsed"),
            (GasOwner.GasAvailable, GasField.StateGasSpill, "=", "0"),
            (GasOwner.GasAvailable, GasField.StateGasSpillRefunded, "=", "0"),
        ];
        for (int index = 0; index < expected.Length; index++)
        {
            FoldWrite write = shape.Writes[index] ?? throw new ExtractionException($"Fold.Writes[{index}] was null.");
            if (write.Order != index || write.Owner != expected[index].Owner || write.Field != expected[index].Field ||
                write.Operator != expected[index].Operator || write.Value != expected[index].Value)
            {
                throw new ExtractionException($"Fold.Writes[{index}] changed from the exact production field order.");
            }
            ValidateAnchor(write.Anchor, $"Fold.Writes[{index}].Anchor");
        }
        RequireAnchors(shape.Anchors, "Fold.Anchors");
    }

    private static void ValidateDependenciesShape(DependencyIdentity[] dependencies)
    {
        if (dependencies is null || dependencies.Length != DependencyExpectations.Length)
            throw new ExtractionException("The authorization state-gas IR dependency set changed.");
        for (int index = 0; index < dependencies.Length; index++)
        {
            DependencyIdentity dependency = dependencies[index] ??
                throw new ExtractionException($"Dependencies[{index}] was null.");
            DependencyExpectation expected = DependencyExpectations[index];
            RequireText(dependency.Name, $"Dependencies[{index}].Name");
            if (dependency.Name != expected.Name || dependency.RootSignature != expected.RootSignature ||
                dependency.TheoremName != expected.TheoremName ||
                dependency.ManifestSha256 != expected.ManifestSha256 ||
                dependency.RefinementSha256 != expected.RefinementSha256 ||
                dependency.LeanSha256 != expected.LeanSha256 ||
                dependency.TheoremSignature is null ||
                Sha256(Encoding.UTF8.GetBytes(dependency.TheoremSignature)) != expected.TheoremSignatureSha256)
            {
                throw new ExtractionException($"Dependencies[{index}] is not bound to the expected accepted kernel identity.");
            }
            RequirePathAndSha(dependency.ManifestPath, dependency.ManifestSha256, $"Dependencies[{index}].Manifest");
            RequirePathAndSha(dependency.RefinementPath, dependency.RefinementSha256, $"Dependencies[{index}].Refinement");
            RequirePathAndSha(dependency.LeanPath, dependency.LeanSha256, $"Dependencies[{index}].Lean");
            RequireText(dependency.TheoremSignature, $"Dependencies[{index}].TheoremSignature");
        }
    }

    private static void ValidateMethodShape(MethodShape shape, string field)
    {
        if (shape is null)
            throw new ExtractionException($"{field} was null.");
        RequireText(shape.Namespace, $"{field}.Namespace");
        RequireText(shape.Owner, $"{field}.Owner");
        RequireText(shape.Name, $"{field}.Name");
        RequireText(shape.ReturnType, $"{field}.ReturnType");
        RequireText(shape.Accessibility, $"{field}.Accessibility");
        RequireTextArray(shape.Modifiers, $"{field}.Modifiers");
        if (shape.Parameters is null)
            throw new ExtractionException($"{field}.Parameters was null.");
        foreach (ParameterShape parameter in shape.Parameters)
        {
            if (parameter is null)
                throw new ExtractionException($"{field}.Parameters contained null.");
            RequireText(parameter.Name, $"{field}.Parameters.Name");
            RequireText(parameter.Type, $"{field}.Parameters.Type");
            if (parameter.RefKind is null)
                throw new ExtractionException($"{field}.Parameters.RefKind was null.");
        }
    }

    private static void ValidateCallShape(CallShape shape, string field)
    {
        if (shape is null)
            throw new ExtractionException($"{field} was null.");
        RequireText(shape.Callee, $"{field}.Callee");
        RequireTextArray(shape.Arguments, $"{field}.Arguments");
        RequireText(shape.Method, $"{field}.Method");
        ValidateAnchor(shape.Anchor, $"{field}.Anchor");
    }

    private static void ValidateChargeShape(ChargeShape shape)
    {
        if (shape is null)
            throw new ExtractionException("A delegation charge shape was null.");
        RequireText(shape.Callee, "Delegations.Charges.Callee");
        RequireTextArray(shape.Arguments, "Delegations.Charges.Arguments");
        RequireText(shape.Guard, "Delegations.Charges.Guard");
        if (shape.Order < 0)
            throw new ExtractionException("Delegations charge order was negative.");
        ValidateAnchor(shape.Anchor, "Delegations.Charges.Anchor");
    }

    private static void ValidateDeltaShape(DeltaShape shape, string field)
    {
        if (shape is null)
            throw new ExtractionException($"{field} was null.");
        RequireText(shape.Variable, $"{field}.Variable");
        RequireText(shape.Left, $"{field}.Left");
        RequireText(shape.Operator, $"{field}.Operator");
        RequireText(shape.Right, $"{field}.Right");
        if (shape.Operator != "-")
            throw new ExtractionException($"{field}.Operator must remain subtraction of the pre-authorization value.");
        ValidateAnchor(shape.Anchor, $"{field}.Anchor");
    }

    private static void ValidateFailureShape(FailureShape shape)
    {
        if (shape is null)
            throw new ExtractionException("Delegations.Failure was null.");
        RequireText(shape.MethodReturn, "Delegations.Failure.MethodReturn");
        RequireText(shape.CallerFailureBranch, "Delegations.Failure.CallerFailureBranch");
        RequireText(shape.PartialStateContract, "Delegations.Failure.PartialStateContract");
        RequireText(shape.ResetOrder, "Delegations.Failure.ResetOrder");
        RequireAnchors(shape.Anchors, "Delegations.Failure.Anchors");
    }

    private static void ValidateExecutionChargeShape(ExecutionChargeShape shape)
    {
        if (shape is null)
            throw new ExtractionException("Delegations.ExecutionCharge was null.");
        ValidateMethodShape(shape.Method, "Delegations.ExecutionCharge.Method");
        ValidateMethodShape(shape.RemainingGasMethod, "Delegations.ExecutionCharge.RemainingGasMethod");
        RequireText(shape.RemainingGasExpression, "Delegations.ExecutionCharge.RemainingGasExpression");
        RequireText(shape.Guard, "Delegations.ExecutionCharge.Guard");
        RequireText(shape.FailureMutation, "Delegations.ExecutionCharge.FailureMutation");
        ValidateCallShape(shape.SuccessCall, "Delegations.ExecutionCharge.SuccessCall");
        ValidateMethodShape(shape.ConsumeRawMethod, "Delegations.ExecutionCharge.ConsumeRawMethod");
        RequireText(shape.SuccessMutation, "Delegations.ExecutionCharge.SuccessMutation");
        RequireText(shape.SuccessReturn, "Delegations.ExecutionCharge.SuccessReturn");
        RequireAnchors(shape.Anchors, "Delegations.ExecutionCharge.Anchors");
    }

    private static void ValidateStateChargeAdapterShape(StateChargeAdapterShape shape)
    {
        if (shape is null)
            throw new ExtractionException("Delegations.StateChargeAdapter was null.");
        ValidateMethodShape(shape.Method, "Delegations.StateChargeAdapter.Method");
        ValidateCallShape(shape.KernelCall, "Delegations.StateChargeAdapter.KernelCall");
        RequireText(shape.OutOfGasGuard, "Delegations.StateChargeAdapter.OutOfGasGuard");
        RequireTextArray(shape.SuccessFieldProjection, "Delegations.StateChargeAdapter.SuccessFieldProjection");
        if (shape.SuccessFieldProjection.Length != 5 ||
            !shape.SuccessFieldProjection.SequenceEqual(
                ["Value=result.Value", "StateReservoir=result.StateReservoir", "StateGasUsed=result.StateGasUsed",
                 "StateGasSpill=result.StateGasSpill", "StateGasSpillRefunded=result.StateGasSpillRefunded"],
                StringComparer.Ordinal))
        {
            throw new ExtractionException("Delegations.StateChargeAdapter must project exactly all five gas-policy fields in source order.");
        }
        RequireText(shape.SuccessReturn, "Delegations.StateChargeAdapter.SuccessReturn");
        RequireAnchors(shape.Anchors, "Delegations.StateChargeAdapter.Anchors");
    }

    private static void RequireAnchors(SourceAnchor[] anchors, string field)
    {
        if (anchors is null)
            throw new ExtractionException($"{field} was null.");
        foreach (SourceAnchor anchor in anchors)
            ValidateAnchor(anchor, field);
    }

    private static void ValidateAnchor(SourceAnchor anchor, string field)
    {
        if (anchor is null)
            throw new ExtractionException($"{field} contained null.");
        RequireText(anchor.Method, $"{field}.Method");
        RequireText(anchor.CanonicalSyntax, $"{field}.CanonicalSyntax");
        if (anchor.StartLine <= 0 || anchor.StartColumn <= 0 || anchor.EndLine <= 0 || anchor.EndColumn <= 0)
            throw new ExtractionException($"{field} had an invalid source location.");
    }

    private static void RequirePathAndSha(string path, string sha, string field)
    {
        RequireText(path, $"{field}.Path");
        RequireSha256(sha, $"{field}.Sha256");
    }

    private static void RequireTextArray(string[]? values, string field)
    {
        if (values is null)
            throw new ExtractionException($"{field} was null.");
        for (int index = 0; index < values.Length; index++)
            RequireText(values[index], $"{field}[{index}]");
    }

    private static void RequireText(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ExtractionException($"{field} was empty or null.");
    }

    private static void RequireSha256(string? value, string field)
    {
        if (value is null || value.Length != 64 || value.Any(static character => !Uri.IsHexDigit(character)))
            throw new ExtractionException($"{field} is not a SHA-256 identity.");
    }

    private static void ValidateRoundTrippedIr(IrDocument sourceDerived, IrDocument roundTripped)
    {
        ValidateIrShape(sourceDerived);
        ValidateIrShape(roundTripped);
        if (!Serialize(sourceDerived).AsSpan().SequenceEqual(Serialize(roundTripped)))
            throw new ExtractionException("The serialized authorization state-gas IR does not exactly match source-derived semantics.");
    }

    private static void ValidateRoundTrippedManifest(Manifest sourceDerived, Manifest roundTripped)
    {
        ValidateManifest(sourceDerived);
        ValidateManifest(roundTripped);
        if (!Serialize(sourceDerived).AsSpan().SequenceEqual(Serialize(roundTripped)))
            throw new ExtractionException("The serialized authorization state-gas manifest does not exactly match source-derived lineage.");
    }

    private static void ValidateManifest(Manifest manifest)
    {
        if (manifest is null || manifest.SchemaVersion != 2 || manifest.ExtractorVersion != ExtractorVersion ||
            manifest.LanguageVersion != LanguageVersion.CSharp14.ToDisplayString() ||
            manifest.AncestorBaselineCommit != AncestorBaselineCommit ||
            manifest.SourceIdentityAuthority != SourceIdentityAuthority || manifest.Kernel != KernelName)
        {
            throw new ExtractionException("The authorization state-gas manifest header changed.");
        }
        RequireText(manifest.CompilerVersion, "Manifest.CompilerVersion");
        if (manifest.Sources is null || manifest.Sources.Length != 4 || manifest.Admissions is null ||
            manifest.Ir is null || manifest.Lean is null || manifest.Dependencies is null ||
            manifest.SemanticBindings is null)
        {
            throw new ExtractionException("The authorization state-gas manifest has missing collections or artifacts.");
        }

        HashSet<string> paths = new(StringComparer.Ordinal);
        foreach (SourceIdentity source in manifest.Sources)
        {
            if (source is null)
                throw new ExtractionException("Manifest.Sources contained null.");
            RequireText(source.Path, "Manifest.Sources.Path");
            RequireSha256(source.Sha256, "Manifest.Sources.Sha256");
            if (!paths.Add(source.Path))
                throw new ExtractionException($"Duplicate authorization state-gas source identity {source.Path}.");
        }
        ValidateAdmissions(manifest.Admissions, manifest.Sources);
        ValidateArtifact(manifest.Ir, IrFileName, "Manifest.Ir");
        ValidateArtifact(manifest.Lean, Normalize(DefaultLeanPath), "Manifest.Lean");
        RequireSha256(manifest.CombinedSha256, "Manifest.CombinedSha256");
        if (manifest.CombinedSha256 != CombinedHash(manifest.Sources))
            throw new ExtractionException("Manifest.CombinedSha256 does not match its sources.");
        if (!manifest.SemanticBindings.SequenceEqual(ExpectedSemanticBindings, StringComparer.Ordinal))
            throw new ExtractionException("The manifest semantic binding list changed.");
        ValidateDependenciesShape(manifest.Dependencies);
    }

    private static void ValidateArtifact(ArtifactIdentity artifact, string expectedPath, string field)
    {
        if (artifact is null || artifact.Path != expectedPath)
            throw new ExtractionException($"{field}.Path changed.");
        RequireSha256(artifact.Sha256, $"{field}.Sha256");
    }

    private static void ValidateAdmissions(IReadOnlyList<AdmissionIdentity> admissions, IReadOnlyList<SourceIdentity> sources)
    {
        if (admissions is null || admissions.Count != sources.Count)
            throw new ExtractionException("Manifest admissions do not cover exactly all admitted sources.");
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (AdmissionIdentity admission in admissions)
        {
            if (admission is null)
                throw new ExtractionException("Manifest.Admissions contained null.");
            RequireText(admission.Key, "Manifest.Admissions.Key");
            RequireSha256(admission.SourceSha256, "Manifest.Admissions.SourceSha256");
            if (!keys.Add(admission.Key) || !admission.Key.EndsWith(":compilation-unit/0:complete-source", StringComparison.Ordinal))
                throw new ExtractionException("Manifest admission identities must be unique complete-source identities.");
        }
        if (!admissions.SequenceEqual(BuildAdmissions(sources)))
            throw new ExtractionException("Manifest admissions do not match source identities.");
    }

    private static AdmissionIdentity[] BuildAdmissions(IEnumerable<SourceIdentity> sources) => sources
        .Select(static source => new AdmissionIdentity(
            $"{source.Path}:compilation-unit/0:complete-source", source.Sha256))
        .ToArray();

    private static IrDocument DeserializeIr(byte[] bytes)
    {
        if (bytes is null)
            throw new ExtractionException("The serialized authorization state-gas IR input was null.");
        try
        {
            RejectDuplicateProperties(bytes, "authorization state-gas IR");
            IrDocument document = JsonSerializer.Deserialize<IrDocument>(bytes, JsonOptions)
                ?? throw new ExtractionException("The serialized authorization state-gas IR was empty.");
            ValidateIrShape(document);
            return document;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"The serialized authorization state-gas IR is not strict JSON: {exception.Message}");
        }
    }

    private static Manifest DeserializeManifest(byte[] bytes)
    {
        if (bytes is null)
            throw new ExtractionException("The serialized authorization state-gas manifest input was null.");
        try
        {
            RejectDuplicateProperties(bytes, "authorization state-gas manifest");
            Manifest manifest = JsonSerializer.Deserialize<Manifest>(bytes, JsonOptions)
                ?? throw new ExtractionException("The serialized authorization state-gas manifest was empty.");
            ValidateManifest(manifest);
            return manifest;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"The serialized authorization state-gas manifest is not strict JSON: {exception.Message}");
        }
    }

    private static byte[] Serialize<T>(T value)
    {
        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        string normalized = Encoding.UTF8.GetString(serialized)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .TrimEnd('\n') + "\n";
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(normalized);
    }

    private static void RejectDuplicateProperties(byte[] bytes, string context)
    {
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
        });
        ValidateUniqueProperties(document.RootElement, context);
    }

    private static void ValidateUniqueProperties(JsonElement element, string context)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new ExtractionException($"The serialized {context} contains duplicate property '{property.Name}'.");
                ValidateUniqueProperties(property.Value, context);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
                ValidateUniqueProperties(item, context);
        }
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string CombinedHash(IEnumerable<SourceIdentity> sources)
    {
        StringBuilder builder = new();
        foreach (SourceIdentity source in sources)
            builder.Append(source.Path).Append('\n').Append(source.Sha256).Append('\n');
        return Sha256(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static SourceFile Read(string root, string relativePath, string label)
    {
        string path = Path.GetFullPath(Path.Combine(root, relativePath));
        EnsureWithin(root, path);
        if (!File.Exists(path))
            throw new ExtractionException($"Missing {label} source: {relativePath}");
        byte[] bytes = File.ReadAllBytes(path);
        SourceText text = SourceText.From(bytes, bytes.Length, Encoding.UTF8, canBeEmbedded: true);
        SyntaxTree tree = CSharpSyntaxTree.ParseText(text, ParseOptions, relativePath);
        return new SourceFile(relativePath, Sha256(bytes), text, tree, tree.GetCompilationUnitRoot());
    }

    private static MethodDeclarationSyntax FindMethod(
        SourceFile source,
        string owner,
        string name,
        string? returnType,
        IReadOnlyList<ParameterShape> parameters,
        string expectedAccessibility)
    {
        MethodDeclarationSyntax[] matches = source.Root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(method =>
            {
                TypeDeclarationSyntax? containingType = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                return containingType is not null && containingType.Identifier.ValueText == owner &&
                    method.Identifier.ValueText == name &&
                    (returnType is null || Canonical(method.ReturnType) == returnType) &&
                    AccessibilityOf(method) == expectedAccessibility &&
                    ParametersMatch(method, parameters);
            })
            .ToArray();
        if (matches.Length != 1)
            throw new ExtractionException($"{source.RelativePath}:{owner}.{name} must have exactly one admitted declaration; found {matches.Length}.");
        return matches[0];
    }

    private static MethodDeclarationSyntax FindMethodByName(
        SourceFile source,
        string owner,
        string name,
        string? returnType,
        string expectedAccessibility)
    {
        MethodDeclarationSyntax[] matches = source.Root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(method =>
            {
                TypeDeclarationSyntax? containingType = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                return containingType is not null && containingType.Identifier.ValueText == owner &&
                    method.Identifier.ValueText == name &&
                    (returnType is null || Canonical(method.ReturnType) == returnType) &&
                    AccessibilityOf(method) == expectedAccessibility;
            })
            .ToArray();
        if (matches.Length != 1)
            throw new ExtractionException($"{source.RelativePath}:{owner}.{name} must have exactly one admitted declaration; found {matches.Length}.");
        return matches[0];
    }

    private static bool ParametersMatch(MethodDeclarationSyntax method, IReadOnlyList<ParameterShape> expected)
    {
        if (method.ParameterList.Parameters.Count != expected.Count)
            return false;
        for (int index = 0; index < expected.Count; index++)
        {
            ParameterSyntax actual = method.ParameterList.Parameters[index];
            ParameterShape wanted = expected[index];
            if (actual.Identifier.ValueText != wanted.Name ||
                Canonical(actual.Type) != wanted.Type ||
                RefKindOf(actual) != wanted.RefKind)
                return false;
        }
        return true;
    }

    private static string AccessibilityOf(MethodDeclarationSyntax method)
    {
        foreach (SyntaxToken modifier in method.Modifiers)
        {
            if (modifier.IsKind(SyntaxKind.PublicKeyword) || modifier.IsKind(SyntaxKind.PrivateKeyword) ||
                modifier.IsKind(SyntaxKind.ProtectedKeyword) || modifier.IsKind(SyntaxKind.InternalKeyword))
                return modifier.Text;
        }
        return string.Empty;
    }

    private static bool HasModifier(SyntaxTokenList modifiers, SyntaxKind kind) =>
        modifiers.Any(modifier => modifier.IsKind(kind));

    private static string RefKindOf(ParameterSyntax parameter)
    {
        foreach (SyntaxToken modifier in parameter.Modifiers)
        {
            if (modifier.IsKind(SyntaxKind.RefKeyword) || modifier.IsKind(SyntaxKind.InKeyword) ||
                modifier.IsKind(SyntaxKind.OutKeyword))
                return modifier.Text;
        }
        return string.Empty;
    }

    private static TypeDeclarationSyntax? FindType(CompilationUnitSyntax root, string name, int? typeParameterCount = null) => root.DescendantNodes()
        .OfType<TypeDeclarationSyntax>()
        .SingleOrDefault(type => type.Identifier.ValueText == name &&
            (!typeParameterCount.HasValue || (type.TypeParameterList?.Parameters.Count ?? 0) == typeParameterCount.Value));

    private static InvocationExpressionSyntax FindInvocation(
        MethodDeclarationSyntax method,
        string callee,
        IReadOnlyList<string> arguments)
    {
        InvocationExpressionSyntax[] matches = method.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => Canonical(invocation.Expression) == callee &&
                invocation.ArgumentList.Arguments.Select(static argument => Canonical(argument)).SequenceEqual(arguments, StringComparer.Ordinal))
            .ToArray();
        if (matches.Length != 1)
            throw new ExtractionException($"{method.Identifier.ValueText} must contain exactly one admitted {callee} call with the pinned arguments; found {matches.Length}.");
        return matches[0];
    }

    private static InvocationExpressionSyntax FindInvocationByCallee(MethodDeclarationSyntax method, string callee)
    {
        InvocationExpressionSyntax[] matches = method.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => Canonical(invocation.Expression) == callee || InvocationName(invocation) == callee)
            .ToArray();
        if (matches.Length != 1)
            throw new ExtractionException($"{method.Identifier.ValueText} must contain exactly one admitted {callee} call; found {matches.Length}.");
        return matches[0];
    }

    private static IfStatementSyntax FindIf(SyntaxNode root, string condition)
    {
        IfStatementSyntax[] matches = root.DescendantNodesAndSelf()
            .OfType<IfStatementSyntax>()
            .Where(item => Canonical(item.Condition) == condition)
            .ToArray();
        if (matches.Length != 1)
            throw new ExtractionException($"Expected exactly one admitted if condition '{condition}', found {matches.Length}.");
        return matches[0];
    }

    private static IfStatementSyntax FindContainingIf(SyntaxNode node, string condition, bool includeSelf = true)
    {
        SyntaxNode? current = includeSelf ? node : node.Parent;
        while (current is not null)
        {
            if (current is IfStatementSyntax branch && Canonical(branch.Condition) == condition)
                return branch;
            current = current.Parent;
        }
        throw new ExtractionException($"No admitted containing if condition '{condition}' was found.");
    }

    private static VariableDeclaratorSyntax FindVariableInitializer(
        SyntaxNode root,
        string variable,
        string initializer)
    {
        VariableDeclaratorSyntax[] matches = root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(declarator => declarator.Identifier.ValueText == variable &&
                declarator.Initializer is not null && Canonical(declarator.Initializer.Value) == initializer)
            .ToArray();
        if (matches.Length != 1)
            throw new ExtractionException($"Expected exactly one admitted initializer '{variable} = {initializer}', found {matches.Length}.");
        return matches[0];
    }

    private static AssignmentExpressionSyntax FindAssignment(
        SyntaxNode root,
        string left,
        string right,
        string @operator)
    {
        AssignmentExpressionSyntax[] matches = root.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(assignment => Canonical(assignment.Left) == left &&
                Canonical(assignment.Right) == right && assignment.OperatorToken.Text == @operator)
            .ToArray();
        if (matches.Length != 1)
            throw new ExtractionException($"Expected exactly one admitted assignment '{left} {@operator} {right}', found {matches.Length}.");
        return matches[0];
    }

    private static string InvocationName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        _ => string.Empty,
    };

    private static bool IsBuilderFluentChain(ExpressionSyntax expression)
    {
        while (true)
        {
            switch (expression)
            {
                case InvocationExpressionSyntax invocation:
                    expression = invocation.Expression;
                    break;
                case MemberAccessExpressionSyntax member:
                    expression = member.Expression;
                    break;
                case IdentifierNameSyntax identifier:
                    return identifier.Identifier.ValueText == "builder";
                default:
                    return false;
            }
        }
    }

    private static bool IsTransactionProcessorService(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText == "ITransactionProcessor",
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText == "ITransactionProcessor",
        AliasQualifiedNameSyntax aliasQualified => aliasQualified.Name.Identifier.ValueText == "ITransactionProcessor",
        _ => false,
    };

    private static bool TargetsTransactionProcessor(InvocationExpressionSyntax invocation)
    {
        GenericNameSyntax? genericName = invocation.Expression switch
        {
            MemberAccessExpressionSyntax { Name: GenericNameSyntax name } => name,
            GenericNameSyntax name => name,
            _ => null,
        };
        return genericName?.TypeArgumentList.Arguments.FirstOrDefault() is TypeSyntax serviceType &&
            IsTransactionProcessorService(serviceType) ||
            invocation.ArgumentList.Arguments.Any(static argument =>
                argument.Expression is TypeOfExpressionSyntax typeOf && IsTransactionProcessorService(typeOf.Type));
    }

    private static bool IsBareReturn(StatementSyntax statement) => statement switch
    {
        ReturnStatementSyntax returnStatement => returnStatement.Expression is null,
        BlockSyntax block when block.Statements.Count == 1 && block.Statements[0] is ReturnStatementSyntax returnStatement =>
            returnStatement.Expression is null,
        _ => false,
    };

    private static MethodShape MethodShapeOf(MethodDeclarationSyntax method)
    {
        TypeDeclarationSyntax owner = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()
            ?? throw new ExtractionException($"Method {method.Identifier.ValueText} has no containing type.");
        return new MethodShape(
            NamespaceOf(method),
            owner.Identifier.ValueText,
            method.Identifier.ValueText,
            Canonical(method.ReturnType),
            method.ParameterList.Parameters.Select(static parameter => new ParameterShape(
                parameter.Identifier.ValueText,
                Canonical(parameter.Type),
                RefKindOf(parameter))).ToArray(),
            AccessibilityOf(method),
            method.Modifiers.Select(static modifier => modifier.Text).ToArray());
    }

    private static CallShape CallShapeOf(InvocationExpressionSyntax invocation, MethodDeclarationSyntax method, AdmissionStage stage) =>
        new(
            Canonical(invocation.Expression),
            invocation.ArgumentList.Arguments.Select(static argument => Canonical(argument)).ToArray(),
            method.Identifier.ValueText,
            Anchor(stage, method, invocation));

    private static SourceAnchor Anchor(AdmissionStage stage, MethodDeclarationSyntax method, SyntaxNode node)
    {
        SourceText text = method.SyntaxTree.GetText();
        LinePositionSpan span = text.Lines.GetLinePositionSpan(node.Span);
        return new SourceAnchor(
            stage,
            method.Identifier.ValueText,
            Canonical(node),
            span.Start.Line + 1,
            span.Start.Character + 1,
            span.End.Line + 1,
            span.End.Character + 1);
    }

    private static void EnsureAnchorOrder(IReadOnlyList<SourceAnchor> anchors, string scope)
    {
        for (int index = 1; index < anchors.Count; index++)
        {
            SourceAnchor previous = anchors[index - 1];
            SourceAnchor current = anchors[index];
            if (previous.StartLine > current.StartLine ||
                (previous.StartLine == current.StartLine && previous.StartColumn >= current.StartColumn))
                throw new ExtractionException($"The admitted {scope} order changed.");
        }
    }

    private static GasField ParseGasField(string field) => field switch
    {
        nameof(GasField.Value) => GasField.Value,
        nameof(GasField.StateReservoir) => GasField.StateReservoir,
        nameof(GasField.StateGasUsed) => GasField.StateGasUsed,
        nameof(GasField.StateGasSpill) => GasField.StateGasSpill,
        nameof(GasField.StateGasSpillRefunded) => GasField.StateGasSpillRefunded,
        _ => throw new ExtractionException($"Unknown admitted gas-policy field {field}.")
    };

    private static string NamespaceOf(SyntaxNode node)
    {
        SyntaxNode? current = node.Parent;
        while (current is not null)
        {
            if (current is BaseNamespaceDeclarationSyntax declaration)
                return declaration.Name.ToString();
            current = current.Parent;
        }
        return string.Empty;
    }

    private static string Canonical(SyntaxNode? node)
    {
        if (node is null)
            return string.Empty;
        string text = node.WithoutTrivia().ToFullString();
        StringBuilder builder = new(text.Length);
        foreach (char character in text)
        {
            if (!char.IsWhiteSpace(character))
                builder.Append(character);
        }
        return builder.ToString();
    }

    private static void RequireContains(string actual, string expected, string message)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
            throw new ExtractionException(message);
    }

    private static int CountOccurrences(string actual, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = actual.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    private static void RejectNestedFunctions(MethodDeclarationSyntax method, LocalFunctionStatementSyntax? allowedLocalFunction = null)
    {
        if (method.DescendantNodes().Any(node =>
                node is AnonymousFunctionExpressionSyntax ||
                node is LocalFunctionStatementSyntax localFunction && localFunction != allowedLocalFunction))
            throw new ExtractionException($"{method.Identifier.ValueText} may not hide admitted effects in nested functions.");
    }

    private static void RejectErrors(SourceFile source)
    {
        foreach (Diagnostic diagnostic in source.Tree.GetDiagnostics())
        {
            if (diagnostic.Severity == DiagnosticSeverity.Error)
                throw new ExtractionException($"{source.RelativePath} does not parse as C# 14: {diagnostic.GetMessage()}");
        }
    }

    private static void EnsureWithin(string root, string path)
    {
        string canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string canonicalPath = Path.GetFullPath(path);
        if (!canonicalPath.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
            throw new ExtractionException($"Source or output path escapes the admitted root: {canonicalPath}");
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static JsonDocument ParseDependencyManifest(byte[] bytes, DependencyExpectation expectation)
    {
        try
        {
            JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.GetProperty("schemaVersion").GetInt32() != 3 ||
                root.GetProperty("root").GetString() != expectation.RootName)
            {
                document.Dispose();
                throw new ExtractionException($"Accepted {expectation.Name} manifest schema/root changed.");
            }
            JsonElement roots = root.GetProperty("rootSignatures");
            if (roots.ValueKind != JsonValueKind.Array || roots.GetArrayLength() == 0)
            {
                document.Dispose();
                throw new ExtractionException($"Accepted {expectation.Name} manifest root signatures are missing.");
            }
            return document;
        }
        catch (KeyNotFoundException exception)
        {
            throw new ExtractionException($"Accepted {expectation.Name} manifest is missing a required field: {exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            throw new ExtractionException($"Accepted {expectation.Name} manifest has an invalid field: {exception.Message}");
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"Accepted {expectation.Name} manifest is not strict JSON: {exception.Message}");
        }
    }

    private static string GetString(JsonElement objectElement, string property, string context)
    {
        if (!objectElement.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            throw new ExtractionException($"Accepted {context} manifest field {property} must be a string.");
        return value.GetString() ?? throw new ExtractionException($"Accepted {context} manifest field {property} was null.");
    }

    private static string[] GetStringArray(JsonElement objectElement, string property, string context)
    {
        if (!objectElement.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
            throw new ExtractionException($"Accepted {context} manifest field {property} must be an array.");
        List<string> values = [];
        foreach (JsonElement entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String || entry.GetString() is not string text)
                throw new ExtractionException($"Accepted {context} manifest field {property} must contain strings.");
            values.Add(text);
        }
        return values.ToArray();
    }

    private static string ExtractTheoremSignature(string source, string theoremName, IReadOnlyList<string> requiredFragments)
    {
        int start = source.IndexOf($"theorem {theoremName}", StringComparison.Ordinal);
        if (start < 0)
            throw new ExtractionException($"Accepted refinement theorem {theoremName} is missing.");
        int body = source.IndexOf(":= by", start, StringComparison.Ordinal);
        if (body < 0)
            throw new ExtractionException($"Accepted refinement theorem {theoremName} has no proof boundary.");
        string declarationText = source[start..body];
        string declaration = CanonicalLean(declarationText);
        foreach (string fragment in requiredFragments)
        {
            if (!declarationText.Contains(fragment, StringComparison.Ordinal) &&
                !declaration.Contains(CanonicalLean(fragment), StringComparison.Ordinal))
                throw new ExtractionException($"Accepted refinement theorem {theoremName} lost required binding fragment '{fragment}'.");
        }
        return declaration;
    }

    private static string CanonicalLean(string text)
    {
        StringBuilder builder = new(text.Length);
        foreach (char character in text)
        {
            if (!char.IsWhiteSpace(character))
                builder.Append(character);
        }
        return builder.ToString();
    }

    private static byte[] EmitLean(IrDocument document, string irHash)
    {
        StringBuilder builder = new();
        builder.Append("-- Generated by AuthorizationStateGasFoldExtractor ").Append(ExtractorVersion).AppendLine(".");
        builder.AppendLine($"-- Ancestor baseline Nethermind commit: {document.AncestorBaselineCommit}");
        builder.AppendLine($"-- IR SHA-256: {irHash}");
        builder.AppendLine("-- Definitions only. Proofs are handwritten in Refinement/AuthorizationStateGasFold.lean.");
        builder.AppendLine("-- This generated file deliberately contains definitions only.");
        builder.AppendLine();
        builder.AppendLine("namespace Eip803x.Generated.AuthorizationStateGasFold");
        builder.AppendLine();
        builder.AppendLine("inductive Stage where");
        builder.AppendLine("  | initialIntrinsicStateGas | calculateAvailableGas | processDelegationsGuard");
        builder.AppendLine("  | capturePreAuthorizationStateGasUsed | validateAuthorization");
        builder.AppendLine("  | chargeNewAccountState | chargeAccountWriteExecution | chargePerAuthorizationState");
        builder.AppendLine("  | applyAuthorityMutation | computeAuthorizationStateGasDelta | foldTopFrameStateGas");
        builder.AppendLine("  deriving DecidableEq, Repr");
        builder.AppendLine();
        builder.AppendLine("inductive GasOwner where | available | baseline deriving DecidableEq, Repr");
        builder.AppendLine("inductive GasField where");
        builder.AppendLine("  | value | stateReservoir | stateGasUsed | stateGasSpill | stateGasSpillRefunded");
        builder.AppendLine("  deriving DecidableEq, Repr");
        builder.AppendLine();
        builder.AppendLine("structure GasPolicyState where");
        builder.AppendLine("  value : Nat");
        builder.AppendLine("  stateReservoir : Int");
        builder.AppendLine("  stateGasUsed : Int");
        builder.AppendLine("  stateGasSpill : Int");
        builder.AppendLine("  stateGasSpillRefunded : Int");
        builder.AppendLine("  deriving DecidableEq, Repr");
        builder.AppendLine();
        builder.AppendLine("structure AuthorizationEffect where");
        builder.AppendLine("  valid : Bool");
        builder.AppendLine("  logicalAccountExists : Bool");
        builder.AppendLine("  delegatedBefore : Bool");
        builder.AppendLine("  clearsDelegation : Bool");
        builder.AppendLine("  firstWrite : Bool");
        builder.AppendLine("  firstDelegation : Bool");
        builder.AppendLine("  newAccountStateCost : Int");
        builder.AppendLine("  perAuthorizationStateCost : Int");
        builder.AppendLine("  accountWriteExecutionCost : Nat");
        builder.AppendLine("  deriving DecidableEq, Repr");
        builder.AppendLine();
        builder.AppendLine("inductive AuthorizationOutcome where");
        builder.AppendLine("  | success (state : GasPolicyState) | failure (state : GasPolicyState)");
        builder.AppendLine("  deriving DecidableEq, Repr");
        builder.AppendLine();
        builder.AppendLine("structure ProcessInput where");
        builder.AppendLine("  eip8037 : Bool");
        builder.AppendLine("  available : GasPolicyState");
        builder.AppendLine("  baseline : GasPolicyState");
        builder.AppendLine("  authorizations : List AuthorizationEffect");
        builder.AppendLine("  deriving DecidableEq, Repr");
        builder.AppendLine();
        builder.AppendLine("inductive ProcessOutcome where");
        builder.AppendLine("  | completed (available : GasPolicyState) (baseline : GasPolicyState) (stateGasUsed : Int)");
        builder.AppendLine("  | partialFailure (available : GasPolicyState) (baseline : GasPolicyState)");
        builder.AppendLine("  deriving DecidableEq, Repr");
        builder.AppendLine();
        builder.AppendLine("def stateGasUsedDelta (before after : GasPolicyState) : Int := after.stateGasUsed - before.stateGasUsed");
        builder.AppendLine();
        builder.AppendLine("def stateChargeSpill (available : GasPolicyState) (amount : Int) : Int :=");
        builder.AppendLine("  if amount <= 0 then 0 else if available.stateReservoir <= 0 then amount else");
        builder.AppendLine("    if amount > available.stateReservoir then amount - available.stateReservoir else 0");
        builder.AppendLine();
        builder.AppendLine("def chargeState (available : GasPolicyState) (amount : Int) : Option GasPolicyState :=");
        builder.AppendLine("  if available.stateReservoir >= amount then");
        builder.AppendLine("    some { available with stateReservoir := available.stateReservoir - amount, stateGasUsed := available.stateGasUsed + amount }");
        builder.AppendLine("  else");
        builder.AppendLine("    let spill := stateChargeSpill available amount");
        builder.AppendLine("    if available.value < Int.toNat spill then none else");
        builder.AppendLine("      some { available with value := available.value - Int.toNat spill, stateReservoir := min 0 available.stateReservoir, stateGasUsed := available.stateGasUsed + amount, stateGasSpill := available.stateGasSpill + spill }");
        builder.AppendLine();
        builder.AppendLine("def chargeExecution (available : GasPolicyState) (amount : Nat) : Option GasPolicyState :=");
        builder.AppendLine("  if available.value < amount then none else some { available with value := available.value - amount }");
        builder.AppendLine();
        builder.AppendLine("def applyAuthorization (e : AuthorizationEffect) (available : GasPolicyState) : AuthorizationOutcome :=");
        builder.AppendLine("  if !e.valid then .success available else");
        builder.AppendLine("  let afterNew := if e.logicalAccountExists then some available else chargeState available e.newAccountStateCost");
        builder.AppendLine("  match afterNew with");
        builder.AppendLine("  | none => .failure available");
        builder.AppendLine("  | some state =>");
        builder.AppendLine("    let afterWrite := if e.firstWrite then chargeExecution state e.accountWriteExecutionCost else some state");
        builder.AppendLine("    match afterWrite with");
        builder.AppendLine("    | none => .failure { state with value := 0 }");
        builder.AppendLine("    | some state => if !e.clearsDelegation && !e.delegatedBefore && e.firstDelegation then");
        builder.AppendLine("        match chargeState state e.perAuthorizationStateCost with");
        builder.AppendLine("        | none => .failure state");
        builder.AppendLine("        | some after => .success after");
        builder.AppendLine("      else .success state");
        builder.AppendLine();
        builder.AppendLine("def processLoop (eip8037 : Bool) (authorizations : List AuthorizationEffect)");
        builder.AppendLine("    (available : GasPolicyState) (baseline : GasPolicyState) (beforeUsed : Int) : ProcessOutcome :=");
        builder.AppendLine("  match authorizations with");
        builder.AppendLine("  | [] => .completed available baseline (available.stateGasUsed - beforeUsed)");
        builder.AppendLine("  | effect :: rest =>");
        builder.AppendLine("    if !eip8037 then processLoop eip8037 rest available baseline beforeUsed else");
        builder.AppendLine("    match applyAuthorization effect available with");
        builder.AppendLine("    | .failure after => .partialFailure after baseline");
        builder.AppendLine("    | .success after => processLoop eip8037 rest after baseline beforeUsed");
        builder.AppendLine();
        builder.AppendLine("def foldTopFrameStateGas (gas baseline : GasPolicyState) (stateGasUsed : Int) : GasPolicyState × GasPolicyState :=");
        builder.AppendLine("  if stateGasUsed <= 0 then (gas, baseline) else");
        builder.AppendLine("    let baselineReservoir := { baseline with stateReservoir := baseline.stateReservoir + stateGasUsed }");
        builder.AppendLine("    let baselineUsed := { baselineReservoir with stateGasUsed := baselineReservoir.stateGasUsed + stateGasUsed }");
        builder.AppendLine("    let gasSpill := { gas with stateGasSpill := 0 }");
        builder.AppendLine("    let gasRefunded := { gasSpill with stateGasSpillRefunded := 0 }");
        builder.AppendLine("    (gasRefunded, baselineUsed)");
        builder.AppendLine();
        builder.AppendLine("def processDelegations (input : ProcessInput) : ProcessOutcome :=");
        builder.AppendLine("  let beforeUsed := input.available.stateGasUsed");
        builder.AppendLine("  match processLoop input.eip8037 input.authorizations input.available input.baseline beforeUsed with");
        builder.AppendLine("  | .partialFailure available baseline => .partialFailure available baseline");
        builder.AppendLine("  | .completed available baseline delta =>");
        builder.AppendLine("    if !input.eip8037 then .completed available baseline delta else");
        builder.AppendLine("      let folded := foldTopFrameStateGas available baseline delta");
        builder.AppendLine("      .completed folded.1 folded.2 delta");
        builder.AppendLine();
        builder.AppendLine("def callerVisible (input : ProcessInput) (outcome : ProcessOutcome) : ProcessOutcome :=");
        builder.AppendLine("  match outcome with");
        builder.AppendLine("  | .completed available baseline delta => .completed available baseline delta");
        builder.AppendLine("  | .partialFailure _ _ => .partialFailure input.available input.baseline");
        builder.AppendLine();
        builder.AppendLine("def exactFoldWriteOrder : List (GasOwner × GasField) :=");
        builder.Append("  [");
        for (int index = 0; index < document.Fold.Writes.Length; index++)
        {
            if (index != 0) builder.Append(", ");
            FoldWrite write = document.Fold.Writes[index];
            string owner = write.Owner == GasOwner.GasAvailable ? "available" : "baseline";
            string field = write.Field switch
            {
                GasField.Value => "value",
                GasField.StateReservoir => "stateReservoir",
                GasField.StateGasUsed => "stateGasUsed",
                GasField.StateGasSpill => "stateGasSpill",
                GasField.StateGasSpillRefunded => "stateGasSpillRefunded",
                _ => throw new ExtractionException("Unexpected fold field."),
            };
            builder.Append("(.").Append(owner).Append(", .").Append(field).Append(')');
        }
        builder.AppendLine("]");
        builder.AppendLine();
        builder.AppendLine("end Eip803x.Generated.AuthorizationStateGasFold");
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            .GetBytes(builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private sealed record SourceFile(
        string RelativePath,
        string Hash,
        SourceText Text,
        SyntaxTree Tree,
        CompilationUnitSyntax Root);

    private sealed record DependencyExpectation(
        string Name,
        string ManifestPath,
        string RefinementPath,
        string SourcePath,
        string RootName,
        string RootSignature,
        string TheoremName,
        string ManifestSha256,
        string RefinementSha256,
        string LeanSha256,
        string TheoremSignatureSha256,
        string[] RequiredFragments,
        string SourceSha256);
}
