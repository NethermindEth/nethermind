// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Nethermind.Evm.Lean.OrdinaryPostNonceDispatchExtractor;

internal static class Extractor
{
    internal const string TransactionProcessorPath =
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs";
    internal const string ExecutionOptionsPath =
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/ExecutionOptions.cs";
    internal const string RoutingKernelPath =
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/SystemTransactionRoutingKernel.cs";
    internal const string MainnetDiPath =
        "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs";
    internal const string CodeRepositoryInterfacePath =
        "src/Nethermind/Nethermind.Evm/ICodeInfoRepository.cs";
    internal const string CodeInfoPath =
        "src/Nethermind/Nethermind.Evm/CodeAnalysis/CodeInfo.cs";
    internal const string JumpDestinationAnalyzerPath =
        "src/Nethermind/Nethermind.Evm/CodeAnalysis/JumpDestinationAnalyzer.cs";
    internal const string JumpDestinationAnalyzerStandardPath =
        "src/Nethermind/Nethermind.Evm/CodeAnalysis/JumpDestinationAnalyzer.std.cs";
    internal const string IntrinsicGasCalculatorPath =
        "src/Nethermind/Nethermind.Evm/IntrinsicGasCalculator.cs";
    internal const string AccountAccessPricingKernelPath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/AccountAccessPricingKernel.cs";
    internal const string PrecompileGasPricingKernelPath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/PrecompileGasPricingKernel.cs";
    internal const string SStorePricingKernelPath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/SStorePricingKernel.cs";
    internal const string StateGasChargeKernelPath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/StateGasChargeKernel.cs";
    internal const string StateGasTransitionAdapterKernelPath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionAdapterKernel.cs";
    internal const string StateGasTransitionKernelPath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionKernel.cs";
    internal const string TransactionGasInitializationKernelPath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/TransactionGasInitializationKernel.cs";
    internal const string CodeRepositoryPath =
        "src/Nethermind/Nethermind.Evm/CodeInfoRepository.cs";
    internal const string CacheCodeRepositoryPath =
        "src/Nethermind/Nethermind.Evm/CacheCodeInfoRepository.cs";
    internal const string WorldStateInterfacePath =
        "src/Nethermind/Nethermind.Evm/State/IWorldState.cs";
    internal const string WorldStatePath =
        "src/Nethermind/Nethermind.State/WorldState.cs";
    internal const string StateProviderPath =
        "src/Nethermind/Nethermind.State/StateProvider.cs";
    internal const string GasPolicyInterfacePath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs";
    internal const string EthereumGasPolicyPath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs";
    internal const string TransactionPath =
        "src/Nethermind/Nethermind.Core/Transaction.cs";
    internal const string TransactionStdPath =
        "src/Nethermind/Nethermind.Core/Transaction.std.cs";
    internal const string TransactionProcessorInterfacePath =
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/ITransactionProcessor.cs";
    internal const string SystemTransactionProcessorPath =
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/SystemTransactionProcessor.cs";
    internal const string DispatchFlagsStandardPath =
        "src/Nethermind/Nethermind.Evm/DispatchFlags.std.cs";
    internal const string MetricsPath =
        "src/Nethermind/Nethermind.Evm/Metrics.cs";
    internal const string MetricsStandardPath =
        "src/Nethermind/Nethermind.Evm/Metrics.std.cs";
    internal const string TransactionSubstatePath =
        "src/Nethermind/Nethermind.Evm/TransactionSubstate.cs";
    internal const string TransactionSettlementPath =
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionSettlementKernel.cs";
    internal const string VirtualMachineInterfacePath =
        "src/Nethermind/Nethermind.Evm/IVirtualMachine.cs";
    internal const string VirtualMachineSignaturePath =
        "src/Nethermind/Nethermind.Evm/VirtualMachine.cs";
    internal const string VirtualMachineAdapterPath =
        "tools/Evm/Lean/OrdinaryPostNonceDispatchExtractor/Admission/VirtualMachineStaticsAdapter.cs";
    internal const string PartialStorageProviderPath =
        "src/Nethermind/Nethermind.State/PartialStorageProviderBase.cs";
    internal const string PersistentStorageProviderPath =
        "src/Nethermind/Nethermind.State/PersistentStorageProvider.cs";
    internal const string PersistentStorageProviderStandardPath =
        "src/Nethermind/Nethermind.State/PersistentStorageProvider.std.cs";
    internal const string TransientStorageProviderPath =
        "src/Nethermind/Nethermind.State/TransientStorageProvider.cs";
    internal const string LocalMetricsFlushStandardPath =
        "src/Nethermind/Nethermind.State/LocalMetricsFlush.std.cs";
    internal const string StateChangeTypePath =
        "src/Nethermind/Nethermind.State/ChangeType.cs";
    internal const string CompilerReferenceInventoryPath =
        "tools/Evm/Lean/OrdinaryPostNonceDispatchExtractor/COMPILER_REFERENCE_PINS.json";

    internal const string OrdinaryStatefulIrPath =
        "tools/Evm/Lean/OrdinaryStatefulAdmissionPrefixExtractor/Generated/OrdinaryStatefulAdmissionPrefix.ir.json";
    internal const string OrdinaryStatefulManifestPath =
        "tools/Evm/Lean/OrdinaryStatefulAdmissionPrefixExtractor/Generated/OrdinaryStatefulAdmissionPrefix.source-manifest.json";
    internal const string OrdinaryStatefulLeanPath =
        "tools/Evm/Lean/OrdinaryStatefulAdmissionPrefixExtractor/Generated/OrdinaryStatefulAdmissionPrefix.lean";
    internal const string LifecycleLeanPath =
        "tools/Evm/Lean/TransactionProcessorExtractor/Generated/TransactionProcessorLifecycle.lean";

    private const int SchemaVersion = 1;
    private const string ExtractorVersion = "1.0.0";
    private const string IrFileName = "OrdinaryPostNonceDispatch.ir.json";
    private const string ManifestFileName = "OrdinaryPostNonceDispatch.source-manifest.json";
    private const string DefaultLeanPath =
        "tools/Evm/Lean/OrdinaryPostNonceDispatchExtractor/Generated/OrdinaryPostNonceDispatch.lean";
    private const string AdmissionClosurePath =
        "tools/Evm/Lean/OrdinaryPostNonceDispatchExtractor/Admission/ProductionClosure.txt";
    private const string ExpectedPostNonceSuccessPredicate =
        "!(result=ValidateSender(tx,header,spec,tracer,opts))||!(result=BuyGas(tx,spec,tracer,opts,effectiveGasPrice,outUInt256premiumPerGas,outUInt256senderReservedGasPayment,outUInt256blobBaseFee))||!(result=IncrementNonce(tx,header,spec,tracer,opts))";
    private const string ExpectedGasFailurePredicate =
        "!(result=CalculateAvailableGas(tx,spec,inintrinsicGas,outTGasPolicygasAvailable))";
    private const string Kernel =
        "Nethermind ordinary standard-mainnet post-nonce dispatch";
    private const string AcceptanceState =
        "bounded-source-extraction-and-refinement-only";
    private const int CompilerReferenceInventorySchemaVersion = 1;
    private const string CompilerReferenceInventorySha256 =
        "268ba44f24f3a45ba314ff09377212496c0e8dca45a738e13c72378398d59cb4";
    private static readonly SourceSpec[] SourceSpecs =
    [
        new(TransactionProcessorPath, "ordinary transaction processor"),
        new(ExecutionOptionsPath, "execution options"),
        new(RoutingKernelPath, "ordinary/system routing predicate"),
        new(MainnetDiPath, "standard mainnet DI route"),
        new(CodeRepositoryInterfacePath, "code repository interface"),
        new(CodeInfoPath, "CodeInfo semantic predicate"),
        new(JumpDestinationAnalyzerPath, "jump-destination semantic closure"),
        new(JumpDestinationAnalyzerStandardPath, "standard jump-destination semantic closure"),
        new(IntrinsicGasCalculatorPath, "intrinsic gas semantic closure"),
        new(AccountAccessPricingKernelPath, "account-access gas semantic closure"),
        new(PrecompileGasPricingKernelPath, "precompile gas semantic closure"),
        new(SStorePricingKernelPath, "SSTORE gas semantic closure"),
        new(StateGasChargeKernelPath, "state-gas charge semantic closure"),
        new(StateGasTransitionAdapterKernelPath, "state-gas transition adapter semantic closure"),
        new(StateGasTransitionKernelPath, "state-gas transition semantic closure"),
        new(TransactionGasInitializationKernelPath, "transaction-gas initialization semantic closure"),
        new(CodeRepositoryPath, "normal code repository implementation"),
        new(CacheCodeRepositoryPath, "standard cached code repository"),
        new(WorldStateInterfacePath, "IWorldState commit interface"),
        new(WorldStatePath, "standard WorldState route"),
        new(StateProviderPath, "standard StateProvider route"),
        new(GasPolicyInterfacePath, "intrinsic/available gas policy interface"),
        new(EthereumGasPolicyPath, "Ethereum available gas implementation"),
        new(TransactionPath, "transaction To/authorization projection"),
        new(TransactionStdPath, "transaction standard partial projection"),
        new(TransactionProcessorInterfacePath, "processor semantic interface closure"),
        new(SystemTransactionProcessorPath, "ordinary processor semantic closure"),
        new(DispatchFlagsStandardPath, "standard tracing dispatch semantic closure"),
        new(MetricsPath, "execution metrics semantic closure"),
        new(MetricsStandardPath, "standard execution metrics semantic closure"),
        new(TransactionSubstatePath, "transaction-substate semantic closure"),
        new(TransactionSettlementPath, "transaction settlement semantic closure"),
        new(VirtualMachineInterfacePath, "virtual-machine semantic interface closure"),
        new(VirtualMachineSignaturePath, "virtual-machine static signature closure"),
        new(PartialStorageProviderPath, "partial storage semantic closure"),
        new(PersistentStorageProviderPath, "persistent storage semantic closure"),
        new(PersistentStorageProviderStandardPath, "standard persistent storage semantic closure"),
        new(TransientStorageProviderPath, "transient storage semantic closure"),
        new(LocalMetricsFlushStandardPath, "standard local metrics semantic closure"),
        new(StateChangeTypePath, "state change semantic closure"),
    ];

    private static readonly DependencySpec[] DependencySpecs =
    [
        new(OrdinaryStatefulIrPath, "OrdinaryStatefulAdmissionPrefix semantic dependency"),
        new(OrdinaryStatefulManifestPath, "OrdinaryStatefulAdmissionPrefix source manifest dependency"),
        new(OrdinaryStatefulLeanPath, "OrdinaryStatefulAdmissionPrefix generated Lean dependency"),
        new(LifecycleLeanPath, "TransactionProcessor lifecycle erasure dependency"),
    ];

    private static readonly string[] CompilerReferenceClosurePaths =
    [
        "src/Nethermind/artifacts/bin/Nethermind.Init/release",
        "tools/artifacts/bin/Evm/release",
        "src/Nethermind/artifacts/bin/Nethermind.Network.Enr.Test/release",
    ];

    private static readonly string[] RequiredCompilerAssemblyNames =
    [
        "Autofac",
        "Microsoft.Extensions.DependencyInjection",
        "Microsoft.Extensions.ObjectPool",
        "Nethermind.Api",
        "Nethermind.Blockchain",
        "Nethermind.Config",
        "Nethermind.Consensus",
        "Nethermind.Core",
        "Nethermind.Crypto",
        "Nethermind.Db",
        "Nethermind.Evm",
        "Nethermind.Evm.Precompiles",
        "Nethermind.Facade",
        "Nethermind.Init",
        "Nethermind.Int256",
        "Nethermind.Logging",
        "Nethermind.Network",
        "Nethermind.Network.Contract",
        "Nethermind.Network.Enr",
        "Nethermind.Serialization.Json",
        "Nethermind.Serialization.Rlp",
        "Nethermind.Serialization.Ssz",
        "Nethermind.Specs",
        "Nethermind.State",
        "Nethermind.Trie",
        "Nethermind.TxPool",
    ];

    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default
        .WithLanguageVersion(LanguageVersion.CSharp14)
        .WithDocumentationMode(DocumentationMode.Parse)
        .WithKind(SourceCodeKind.Regular);

    private static readonly HashSet<string> ProcessorSemanticSourcePaths =
    [
        TransactionProcessorPath,
        ExecutionOptionsPath,
        RoutingKernelPath,
        TransactionProcessorInterfacePath,
        SystemTransactionProcessorPath,
        DispatchFlagsStandardPath,
        MetricsPath,
        MetricsStandardPath,
        TransactionSubstatePath,
        TransactionSettlementPath,
        VirtualMachineInterfacePath,
    ];

    private static readonly HashSet<string> CodeInfoSemanticSourcePaths =
    [
        CodeInfoPath,
        JumpDestinationAnalyzerPath,
        JumpDestinationAnalyzerStandardPath,
    ];

    private static readonly HashSet<string> CodeRepositorySemanticSourcePaths =
    [
        CodeRepositoryPath,
        CacheCodeRepositoryPath,
        MetricsPath,
        MetricsStandardPath,
    ];

    private static readonly HashSet<string> GasPolicySemanticSourcePaths =
    [
        GasPolicyInterfacePath,
        EthereumGasPolicyPath,
        IntrinsicGasCalculatorPath,
        AccountAccessPricingKernelPath,
        PrecompileGasPricingKernelPath,
        SStorePricingKernelPath,
        StateGasChargeKernelPath,
        StateGasTransitionAdapterKernelPath,
        StateGasTransitionKernelPath,
        TransactionGasInitializationKernelPath,
    ];

    private static readonly HashSet<string> WorldStateSemanticSourcePaths =
    [
        WorldStatePath,
        StateProviderPath,
        PartialStorageProviderPath,
        PersistentStorageProviderPath,
        PersistentStorageProviderStandardPath,
        TransientStorageProviderPath,
        LocalMetricsFlushStandardPath,
        StateChangeTypePath,
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private static readonly string[] ExpectedStageIds =
    [
        "prepareSimpleTransferFastPath",
        "commitBeforeExecution",
        "calculateAvailableGas",
        "dispatchSimpleTransfer",
        "dispatchEvm",
    ];

    private static readonly string[] ExpectedBranchIds =
    [
        "prepareInitializesPreloadedOutputs",
        "prepareNoRecipient",
        "prepareNotCandidate",
        "lookupEscapes",
        "lookupReturns",
        "commitEscapes",
        "gasRejected",
        "simpleHandoff",
        "evmHandoff",
    ];

    private static readonly TerminalKind[] ExpectedTerminalKinds =
    [
        TerminalKind.NoTerminal,
        TerminalKind.NoTerminal,
        TerminalKind.NoTerminal,
        TerminalKind.EscapedLookup,
        TerminalKind.NoTerminal,
        TerminalKind.EscapedPrecommit,
        TerminalKind.GasRejected,
        TerminalKind.SimpleHandoff,
        TerminalKind.EvmHandoff,
    ];

    private static readonly string[] ExpectedOperationIds =
    [
        "restoreOption",
        "commitOption",
        "prepareFastPath",
        "candidateGuard",
        "codeLookup",
        "simpleDecision",
        "commitBeforeExecution",
        "precommitRequest",
        "calculateAvailableGas",
        "gasFailurePolicy",
        "simpleHandoff",
        "evmHandoff",
    ];

    private const string ExpectedRestoreFormula = "opts.HasFlag(ExecutionOptions.Restore)";
    private const string ExpectedCommitFormula =
        "opts.HasFlag(ExecutionOptions.Commit)||(!opts.HasFlag(ExecutionOptions.SkipValidation)&&!spec.IsEip658Enabled)";
    private const string ExpectedGasCondition =
        "TGasPolicy.TryCreateAvailableFromIntrinsic(tx.GasLimit,intrinsicGas.Standard,spec,outgasAvailable)";
    private const string ExpectedGasSuccessResult = "TransactionResult.Ok";
    private const string ExpectedGasFailureResult = "TransactionResult.GasLimitBelowIntrinsicGas";

    internal static ExtractionResult Extract(string repoRoot, string outputDirectory, string? leanOutputPath = null) =>
        ExtractCore(repoRoot, outputDirectory, leanOutputPath, requireReviewedAdmission: true);

    /// <summary>Runs source lowering without the reviewed source closure for mutation tests only.</summary>
    /// <remarks>
    /// The source root may be an isolated fixture. In that case <paramref name="referenceRoot"/>
    /// supplies the deterministic production metadata/reference closure used by Roslyn.
    /// </remarks>
    internal static ExtractionResult ExtractWithoutReviewedAdmissionForTest(
        string repoRoot,
        string outputDirectory,
        string? leanOutputPath = null,
        string? referenceRoot = null) =>
        ExtractCore(repoRoot, outputDirectory, leanOutputPath, requireReviewedAdmission: false, referenceRoot: referenceRoot);

    private static ExtractionResult ExtractCore(
        string repoRoot,
        string outputDirectory,
        string? leanOutputPath,
        bool requireReviewedAdmission,
        string? referenceRoot = null)
    {
        string root = Path.GetFullPath(repoRoot);
        AdmissionClosure closure = ReadAdmissionClosure(root);
        SourceFile[] sources = ReadSources(root);
        if (requireReviewedAdmission)
        {
            ValidateSourceClosure(sources, closure);
        }

        DependencyIdentity[] dependencies = ReadDependencies(root);
        SourceFile virtualMachineAdapter = ReadSource(root, VirtualMachineAdapterPath, "explicit virtual-machine compiler adapter");
        string compilerRoot = referenceRoot is null ? root : Path.GetFullPath(referenceRoot);
        CompilerReferenceClosure compilerReferences = BuildMetadataReferences(compilerRoot);
        SemanticContext context = BuildSemanticContext(
            root,
            sources,
            virtualMachineAdapter,
            compilerReferences);
        List<SourceBinding> bindings = [];
        RouteShape route = ValidateRoute(sources, context, bindings);
        OptionShape options = ValidateOptions(FindSource(sources, ExecutionOptionsPath), context, bindings);
        DispatchValidation validation = ValidateDispatch(
            FindSource(sources, TransactionProcessorPath),
            FindSource(sources, CodeRepositoryInterfacePath),
            FindSource(sources, CodeInfoPath),
            FindSource(sources, CodeRepositoryPath),
            FindSource(sources, CacheCodeRepositoryPath),
            FindSource(sources, WorldStateInterfacePath),
            FindSource(sources, WorldStatePath),
            FindSource(sources, GasPolicyInterfacePath),
            FindSource(sources, EthereumGasPolicyPath),
            FindSource(sources, TransactionPath),
            options,
            context,
            bindings);
        options = options with
        {
            RestoreFormula = validation.RestoreFormula,
            CommitFormula = validation.CommitFormula,
            CommitBeforeFormula = validation.CommitBeforeFormula,
        };
        validation = validation with { Shape = validation.Shape with { RouteBindings = route.Bindings } };

        if (requireReviewedAdmission)
        {
            ValidateDependencyClosure(root, dependencies, closure);
        }

        SourceIdentity[] identities = sources
            .Select(static source => new SourceIdentity(source.RelativePath, source.Role, source.Sha256))
            .ToArray();
        DispatchShape dispatch = validation.Shape;
        IrDocument document = new(
            SchemaVersion,
            ExtractorVersion,
            Kernel,
            AcceptanceState,
            closure.Sha256,
            identities,
            dependencies,
            compilerReferences.Identities,
            options,
            route,
            dispatch,
            [
                "ExecutionOptions retains the source default signed-Int32 backing and HasFlag bit semantics.",
                "The preload is an algebraic none/loaded value; loaded carries the exact code handle and returned delegation designator.",
                "CalculateAvailableGas failure preserves the source default TGasPolicy output, whose five fields are all zero for EthereumGasPolicy.",
            ],
            [
                "The source-to-model correspondence is structural: Roslyn binds methods, invocations, receivers, properties, CFG blocks, dominance and order. CLR, JIT, overload resolution outside the admitted compilation, and exception mechanics remain external.",
                $"The processor and code-repository semantic units compile the admitted source trees against the explicit, hash-pinned compiler/reference inventory. Every inventory member carries its logical path, SHA-256, MVID, selection status, and metadata assembly-reference names; the actual Nethermind.Evm.dll is included and checked before any source lowering. The source project's assembly identity is used for friend-access checks. Metrics.cs and Metrics.std.cs are compiled as their actual production sources. The out-of-scope VM body is not compiled here: the admitted VirtualMachine.cs declaration and the checked-in compiler-only adapter {VirtualMachineAdapterPath} (SHA-256 {virtualMachineAdapter.Sha256}) provide the declared source boundary, while the exact Nethermind.Evm metadata symbol is independently validated for return type, static/accessibility modifiers, parameter names/types/ref-kinds, and target assembly. TransactionProcessor calls bind only to that adapter declaration; its empty body is not a semantic oracle. All compilation errors and candidate/error bindings are rejected; this is a source-closure semantic check, not a claim about the full repository build or CLR/JIT behavior.",
                "Lean Options.raw is a nonnegative projection of the source signed-Int32 ExecutionOptions value; signed representation and bit arithmetic correspondence remain an adapter obligation.",
                "Lean Nat/Int transaction and gas fields are unbounded projections of source UInt64/UInt256/Int64 values; fixed-width range and conversion obligations are outside this bounded refinement.",
                "CodeInfoRepository lookup and IWorldState.Commit are effectful external requests. TryCreateAvailableFromIntrinsic is source-bound through its exact condition and TransactionResult arms; its implementation result remains the input availableGas observation.",
                "The generated gas classifier maps that observed condition result to the exact admitted success/failure result names. This is a finite lowering of the helper conditional, not a proof that the external gas policy computes the observation.",
                "The ordinary-stateful prefix is imported only as a source-order/erasure dependency. This package starts at successful IncrementNonce and does not re-use its semantic values.",
                "SimpleTransfer and EvmTransaction bodies are typed handoff targets only; execution, code target loading, VM, settlement, receipts, and block accounting are outside scope.",
            ]);

        ValidateIr(document);
        byte[] irBytes = Serialize(document);
        string irSha256 = Sha256(irBytes);
        IrDocument roundTrippedDocument = DeserializeIr(irBytes);
        ValidateRoundTrip(document, roundTrippedDocument);
        byte[] leanBytes = LeanEmitter.Emit(roundTrippedDocument, irSha256);

        string output = Path.GetFullPath(outputDirectory);
        string leanPath = Path.GetFullPath(leanOutputPath ?? Path.Combine(root, DefaultLeanPath));
        EnsureWithin(leanOutputPath is null ? root : output, leanPath);
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(Path.GetDirectoryName(leanPath)!);
        string irPath = Path.Combine(output, IrFileName);
        string manifestPath = Path.Combine(output, ManifestFileName);

        Manifest manifest = new(
            SchemaVersion,
            ExtractorVersion,
            typeof(CSharpCompilation).Assembly.GetName().Version?.ToString() ?? "unknown",
            LanguageVersion.CSharp14.ToDisplayString(),
            Kernel,
            AcceptanceState,
            closure.Sha256,
            identities,
            dependencies,
            compilerReferences.Identities,
            bindings.ToArray(),
            new ArtifactIdentity(IrFileName, irSha256),
            new ArtifactIdentity(Normalize(DefaultLeanPath), Sha256(leanBytes)),
            CombinedSourceHash(identities),
            irSha256);
        ValidateManifest(manifest);
        byte[] manifestBytes = Serialize(manifest);
        Manifest roundTrippedManifest = DeserializeManifest(manifestBytes);
        ValidateRoundTrip(manifest, roundTrippedManifest);
        Write(irPath, irBytes);
        Write(leanPath, leanBytes);
        Write(manifestPath, manifestBytes);

        return new ExtractionResult(
            irPath,
            manifestPath,
            leanPath,
            sources.Length,
            dispatch.Branches.Length,
            dispatch.Effects.Length,
            irSha256,
            Sha256(manifestBytes),
            Sha256(leanBytes));
    }

    internal static void ValidateExistingArtifacts(string repoRoot, string outputDirectory, string? leanOutputPath)
    {
        string root = Path.GetFullPath(repoRoot);
        string tempBase = Path.GetFullPath(Path.GetTempPath());
        string temp = Path.Combine(tempBase, "ordinary-post-nonce-dispatch-check-" + Guid.NewGuid().ToString("N"));
        string freshOutput = Path.Combine(temp, "Generated");
        string freshLean = Path.Combine(freshOutput, "OrdinaryPostNonceDispatch.lean");
        EnsureWithin(tempBase, temp);
        try
        {
            ExtractionResult fresh = Extract(root, freshOutput, freshLean);
            CompareFiles(Path.Combine(Path.GetFullPath(outputDirectory), IrFileName), fresh.IrPath);
            CompareFiles(Path.Combine(Path.GetFullPath(outputDirectory), ManifestFileName), fresh.ManifestPath);
            CompareFiles(Path.GetFullPath(leanOutputPath ?? Path.Combine(root, DefaultLeanPath)), fresh.LeanPath);
        }
        finally
        {
            if (Directory.Exists(temp) && IsChildOf(tempBase, temp))
            {
                Directory.Delete(temp, recursive: true);
            }
        }
    }

    internal static void ValidateSerializedIr(byte[] sourceDerivedIr, byte[] candidateIr)
    {
        IrDocument source = DeserializeIr(sourceDerivedIr);
        IrDocument candidate = DeserializeIr(candidateIr);
        ValidateRoundTrip(source, candidate);
    }

    internal static void ValidateSerializedManifest(byte[] sourceDerivedManifest, byte[] candidateManifest)
    {
        Manifest source = DeserializeManifest(sourceDerivedManifest);
        Manifest candidate = DeserializeManifest(candidateManifest);
        ValidateRoundTrip(source, candidate);
    }

    /// <summary>Invokes the emitter's independent structural validation for mutation tests.</summary>
    internal static byte[] EmitUnvalidatedIrForTest(byte[] serializedIr)
    {
        try
        {
            IrDocument document = JsonSerializer.Deserialize<IrDocument>(serializedIr, JsonOptions)
                ?? throw new ExtractionException("The test IR was empty.");
            return LeanEmitter.Emit(document, Sha256(serializedIr));
        }
        catch (JsonException exception)
        {
            throw new ExtractionException("The test IR is not valid JSON: " + exception.Message);
        }
    }

    private static SourceFile[] ReadSources(string root) =>
        SourceSpecs.Select(spec => ReadSource(root, spec.Path, spec.Role)).ToArray();

    private static DependencyIdentity[] ReadDependencies(string root)
    {
        List<DependencyIdentity> dependencies = [];
        foreach (DependencySpec spec in DependencySpecs)
        {
            string path = Path.GetFullPath(Path.Combine(root, spec.Path));
            EnsureWithin(root, path);
            if (!File.Exists(path))
            {
                throw new ExtractionException($"Missing {spec.Role}: {spec.Path}.");
            }

            dependencies.Add(new DependencyIdentity(spec.Path, spec.Role, Sha256(File.ReadAllBytes(path))));
        }

        return dependencies.ToArray();
    }

    private static SemanticContext BuildSemanticContext(
        string root,
        SourceFile[] sources,
        SourceFile virtualMachineAdapter,
        CompilerReferenceClosure compilerReferences)
    {
        MetadataReference[] references = compilerReferences.References;
        Dictionary<SyntaxTree, SemanticModel> models = [];
        SourceFile virtualMachine = FindSource(sources, VirtualMachineSignaturePath);
        ValidateVirtualMachineStaticsSignature(virtualMachine);
        ValidateVirtualMachineStaticsAdapter(virtualMachineAdapter);
        foreach (SourceFile[] group in SemanticGroups(sources))
        {
            IEnumerable<SyntaxTree> trees = group.Any(static source => source.RelativePath == TransactionProcessorPath)
                ? group.Select(static source => source.Tree).Append(virtualMachineAdapter.Tree)
                : group.Select(static source => source.Tree);

            CSharpCompilation compilation = CSharpCompilation.Create(
                CompilationAssemblyName(group[0].RelativePath),
                trees,
                references,
                new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    nullableContextOptions: NullableContextOptions.Enable,
                    allowUnsafe: true,
                    optimizationLevel: OptimizationLevel.Debug,
                    metadataImportOptions: MetadataImportOptions.All));

            Diagnostic[] errors = compilation.GetDiagnostics()
                .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .ToArray();
            if (errors.Length != 0)
            {
                throw new ExtractionException($"The production source closure has compilation errors in {string.Join(", ", group.Select(static source => source.RelativePath))}: " +
                    string.Join("; ", errors.Take(200).Select(static diagnostic => diagnostic.ToString())));
            }

            if (group.Any(static source => source.RelativePath == TransactionProcessorPath))
            {
                ValidateVirtualMachineStaticsBindings(
                    FindSource(group, TransactionProcessorPath),
                    virtualMachineAdapter,
                    compilation.GetSemanticModel(FindSource(group, TransactionProcessorPath).Tree, ignoreAccessibility: true));
                ValidateVirtualMachineStaticsMetadata(compilation, compilerReferences);
            }

            foreach (SourceFile source in group)
            {
                // Metrics is intentionally compiled in both source units so each closure rejects
                // a changed production signature, but its source tree has one shared semantic model.
                models.TryAdd(source.Tree, compilation.GetSemanticModel(source.Tree, ignoreAccessibility: true));
            }
        }

        return new SemanticContext(models);
    }

    private static IEnumerable<SourceFile[]> SemanticGroups(SourceFile[] sources)
    {
        SourceFile[] processorUnit = sources
            .Where(source => ProcessorSemanticSourcePaths.Contains(source.RelativePath))
            .ToArray();
        bool processorUnitEmitted = false;
        SourceFile[] codeInfoUnit = sources
            .Where(source => CodeInfoSemanticSourcePaths.Contains(source.RelativePath))
            .ToArray();
        bool codeInfoUnitEmitted = false;
        SourceFile[] codeRepositoryUnit = sources
            .Where(source => CodeRepositorySemanticSourcePaths.Contains(source.RelativePath))
            .ToArray();
        bool codeRepositoryUnitEmitted = false;
        SourceFile[] gasPolicyUnit = sources
            .Where(source => GasPolicySemanticSourcePaths.Contains(source.RelativePath))
            .ToArray();
        bool gasPolicyUnitEmitted = false;
        SourceFile[] worldStateUnit = sources
            .Where(source => WorldStateSemanticSourcePaths.Contains(source.RelativePath))
            .ToArray();
        bool worldStateUnitEmitted = false;
        SourceFile[] transactionParts = sources
            .Where(source => source.RelativePath == TransactionPath || source.RelativePath == TransactionStdPath)
            .ToArray();
        bool transactionGroupEmitted = false;
        foreach (SourceFile source in sources)
        {
            if (source.RelativePath == VirtualMachineSignaturePath)
            {
                continue;
            }

            if (CodeInfoSemanticSourcePaths.Contains(source.RelativePath))
            {
                if (!codeInfoUnitEmitted)
                {
                    codeInfoUnitEmitted = true;
                    yield return codeInfoUnit;
                }

                continue;
            }

            if (CodeRepositorySemanticSourcePaths.Contains(source.RelativePath))
            {
                if (!codeRepositoryUnitEmitted)
                {
                    codeRepositoryUnitEmitted = true;
                    yield return codeRepositoryUnit;
                }

                continue;
            }

            if (GasPolicySemanticSourcePaths.Contains(source.RelativePath))
            {
                if (!gasPolicyUnitEmitted)
                {
                    gasPolicyUnitEmitted = true;
                    yield return gasPolicyUnit;
                }

                continue;
            }

            if (WorldStateSemanticSourcePaths.Contains(source.RelativePath))
            {
                if (!worldStateUnitEmitted)
                {
                    worldStateUnitEmitted = true;
                    yield return worldStateUnit;
                }

                continue;
            }

            if (ProcessorSemanticSourcePaths.Contains(source.RelativePath))
            {
                if (!processorUnitEmitted)
                {
                    processorUnitEmitted = true;
                    yield return processorUnit;
                }

                continue;
            }

            if (source.RelativePath == TransactionPath || source.RelativePath == TransactionStdPath)
            {
                if (!transactionGroupEmitted)
                {
                    transactionGroupEmitted = true;
                    yield return transactionParts;
                }

                continue;
            }

            yield return [source];
        }
    }

    private static void ValidateVirtualMachineStaticsSignature(SourceFile source)
    {
        MethodDeclarationSyntax[] methods = source.Root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == "RestoreRipemdTouch" &&
                TypeIdentity(method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()) ==
                "Nethermind.Evm.VirtualMachineStatics")
            .ToArray();
        if (methods.Length != 1)
        {
            throw new ExtractionException("VirtualMachineStatics.RestoreRipemdTouch source must contain exactly one Nethermind.Evm.VirtualMachineStatics declaration.");
        }

        MethodDeclarationSyntax method = methods[0];
        if (method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault() is not ClassDeclarationSyntax type ||
            type.TypeParameterList is not null || method.TypeParameterList is not null ||
            type.Members.Count(static member => member is MethodDeclarationSyntax method && method.Identifier.ValueText == "RestoreRipemdTouch") != 1)
        {
            throw new ExtractionException("VirtualMachineStatics.RestoreRipemdTouch source type must be the non-generic Nethermind.Evm.VirtualMachineStatics class.");
        }

        if (Canonical(method.ReturnType) != "void" ||
            Canonical(method.ParameterList) != "(IWorldStateworldState,IReleaseSpecspec,boolshouldRestore)" ||
            !method.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.InternalKeyword)) ||
            !method.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.StaticKeyword)))
        {
            throw new ExtractionException("VirtualMachineStatics.RestoreRipemdTouch source declaration must remain internal static void with (IWorldState, IReleaseSpec, bool).");
        }

        SyntaxToken[] visibilityModifiers = method.Modifiers
            .Where(static modifier => modifier.IsKind(SyntaxKind.PublicKeyword) || modifier.IsKind(SyntaxKind.PrivateKeyword) ||
                modifier.IsKind(SyntaxKind.ProtectedKeyword) || modifier.IsKind(SyntaxKind.FileKeyword))
            .ToArray();
        if (visibilityModifiers.Length != 0)
        {
            throw new ExtractionException("VirtualMachineStatics.RestoreRipemdTouch source declaration has an unexpected accessibility modifier.");
        }
    }

    private static void ValidateVirtualMachineStaticsAdapter(SourceFile source)
    {
        ClassDeclarationSyntax[] classes = source.Root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Where(declaration => TypeIdentity(declaration) == "Nethermind.Evm.VirtualMachineStatics")
            .ToArray();
        MethodDeclarationSyntax[] methods = source.Root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == "RestoreRipemdTouch" &&
                TypeIdentity(method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()) ==
                "Nethermind.Evm.VirtualMachineStatics")
            .ToArray();
        BaseNamespaceDeclarationSyntax[] namespaces = source.Root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().ToArray();
        if (namespaces.Length != 1 || Canonical(namespaces[0].Name) != "Nethermind.Evm" || classes.Length != 1 || methods.Length != 1)
        {
            throw new ExtractionException("VirtualMachineStatics compiler adapter must contain exactly one Nethermind.Evm class and method.");
        }

        ClassDeclarationSyntax type = classes[0];
        MethodDeclarationSyntax method = methods[0];
        if (type.Members.Count != 1 || !ReferenceEquals(type.Members[0], method) ||
            !type.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.PublicKeyword)) ||
            !type.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.StaticKeyword)) ||
            type.TypeParameterList is not null || method.TypeParameterList is not null ||
            type.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.AbstractKeyword) || modifier.IsKind(SyntaxKind.SealedKeyword) ||
                modifier.IsKind(SyntaxKind.PartialKeyword) || modifier.IsKind(SyntaxKind.FileKeyword)) ||
            Canonical(method.ReturnType) != "void" ||
            Canonical(method.ParameterList) != "(IWorldStateworldState,IReleaseSpecspec,boolshouldRestore)" ||
            !method.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.InternalKeyword)) ||
            !method.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.StaticKeyword)) ||
            method.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.PublicKeyword) || modifier.IsKind(SyntaxKind.PrivateKeyword) ||
                modifier.IsKind(SyntaxKind.ProtectedKeyword) || modifier.IsKind(SyntaxKind.FileKeyword)) ||
            method.ExpressionBody is not null || method.Body is not { Statements.Count: 0 })
        {
            throw new ExtractionException("VirtualMachineStatics compiler adapter must remain public static with an empty internal static void RestoreRipemdTouch(IWorldState, IReleaseSpec, bool) body.");
        }
    }

    private static void ValidateVirtualMachineStaticsBindings(SourceFile processor, SourceFile adapter, SemanticModel model)
    {
        InvocationExpressionSyntax[] invocations = processor.Root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(static invocation => InvocationName(invocation) == "RestoreRipemdTouch" &&
                Canonical(invocation.Expression) == "VirtualMachineStatics.RestoreRipemdTouch")
            .ToArray();
        if (invocations.Length != 2)
        {
            throw new ExtractionException("TransactionProcessor must retain exactly two direct VirtualMachineStatics.RestoreRipemdTouch calls for the VM adapter boundary.");
        }

        foreach (InvocationExpressionSyntax invocation in invocations)
        {
            SymbolInfo symbolInfo = model.GetSymbolInfo(invocation);
            if (symbolInfo.CandidateReason != CandidateReason.None || symbolInfo.CandidateSymbols.Length != 0)
            {
                throw new ExtractionException("VirtualMachineStatics.RestoreRipemdTouch resolved through a candidate or ambiguous symbol.");
            }

            if (model.GetOperation(invocation) is not IInvocationOperation operation)
            {
                throw new ExtractionException("VirtualMachineStatics.RestoreRipemdTouch did not resolve to an invocation operation.");
            }

            IMethodSymbol method = operation.TargetMethod;
            if (HasErrorSymbol(method) || method.ContainingType is null || HasErrorType(method.ContainingType))
            {
                throw new ExtractionException("VirtualMachineStatics.RestoreRipemdTouch resolved through an error symbol.");
            }

            if (method.Name != "RestoreRipemdTouch" || method.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) != "Nethermind.Evm.VirtualMachineStatics" ||
                method.ContainingAssembly.Identity.Name != "Nethermind.Evm" || method.Arity != 0 || !method.IsStatic ||
                method.DeclaredAccessibility != Accessibility.Internal || !method.ReturnsVoid ||
                method.Parameters.Length != 3 ||
                method.Parameters[0].Name != "worldState" || method.Parameters[1].Name != "spec" || method.Parameters[2].Name != "shouldRestore" ||
                method.Parameters[0].RefKind != RefKind.None || method.Parameters[1].RefKind != RefKind.None || method.Parameters[2].RefKind != RefKind.None ||
                method.Parameters[0].Type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) != "Nethermind.Evm.State.IWorldState" ||
                method.Parameters[1].Type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) != "Nethermind.Core.Specs.IReleaseSpec" ||
                method.Parameters[2].Type.SpecialType != SpecialType.System_Boolean ||
                method.Locations.Length != 1 || !method.Locations[0].IsInSource ||
                !ReferenceEquals(method.Locations[0].SourceTree, adapter.Tree))
            {
                throw new ExtractionException("VirtualMachineStatics.RestoreRipemdTouch did not bind to the declared compiler adapter with the expected internal static void signature.");
            }
        }
    }

    private static void ValidateVirtualMachineStaticsMetadata(
        CSharpCompilation compilation,
        CompilerReferenceClosure compilerReferences)
    {
        CompilerReferenceIdentity expectedIdentity = compilerReferences.Identities.SingleOrDefault(identity =>
            identity.Selected && identity.AssemblyName == "Nethermind.Evm" &&
            identity.Path == "src/Nethermind/artifacts/bin/Nethermind.Init/release/Nethermind.Evm.dll")
            ?? throw new ExtractionException("The VM adapter boundary requires the selected hash-pinned Nethermind.Evm metadata reference.");
        string expectedPath = compilerReferences.ResolvedPaths[expectedIdentity.Path];
        MetadataReference[] matches = compilerReferences.References
            .Where(reference => string.Equals(MetadataReferencePath(reference), expectedPath, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new ExtractionException("The VM adapter boundary requires exactly one admitted Nethermind.Evm metadata reference.");
        }

        if (compilation.GetAssemblyOrModuleSymbol(matches[0]) is not IAssemblySymbol assembly ||
            assembly.Identity.Name != "Nethermind.Evm" || HasErrorSymbol(assembly))
        {
            throw new ExtractionException("The VM adapter boundary could not resolve the admitted Nethermind.Evm metadata assembly.");
        }

        INamedTypeSymbol? type = assembly.GetTypeByMetadataName("Nethermind.Evm.VirtualMachineStatics");
        if (type is null || HasErrorSymbol(type) || type.TypeKind != TypeKind.Class || !type.IsStatic ||
            type.Arity != 0 || type.ContainingType is not null ||
            type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) != "Nethermind.Evm.VirtualMachineStatics" ||
            type.DeclaredAccessibility != Accessibility.Public || type.ContainingAssembly.Identity.Name != "Nethermind.Evm")
        {
            throw new ExtractionException("The actual Nethermind.Evm.VirtualMachineStatics metadata type is missing, ambiguous, or changed.");
        }

        IMethodSymbol[] methods = type.GetMembers("RestoreRipemdTouch").OfType<IMethodSymbol>().ToArray();
        if (methods.Length != 1)
        {
            throw new ExtractionException("The actual Nethermind.Evm.VirtualMachineStatics.RestoreRipemdTouch metadata symbol is missing or ambiguous.");
        }

        IMethodSymbol method = methods[0];
        if (HasErrorSymbol(method) || method.MethodKind != MethodKind.Ordinary || method.Arity != 0 || !method.IsStatic ||
            method.DeclaredAccessibility != Accessibility.Internal || !method.ReturnsVoid ||
            method.Parameters.Length != 3 ||
            method.Parameters[0].Name != "worldState" || method.Parameters[1].Name != "spec" || method.Parameters[2].Name != "shouldRestore" ||
            method.Parameters[0].RefKind != RefKind.None || method.Parameters[1].RefKind != RefKind.None || method.Parameters[2].RefKind != RefKind.None ||
            method.Parameters[0].Type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) != "Nethermind.Evm.State.IWorldState" ||
            method.Parameters[1].Type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) != "Nethermind.Core.Specs.IReleaseSpec" ||
            method.Parameters[2].Type.SpecialType != SpecialType.System_Boolean ||
            method.Locations.Length == 0 || method.Locations.Any(static location => location.Kind != LocationKind.MetadataFile))
        {
            throw new ExtractionException("The actual Nethermind.Evm.VirtualMachineStatics.RestoreRipemdTouch metadata symbol changed: expected internal static void with (IWorldState, IReleaseSpec, bool).");
        }
    }

    private static string? MetadataReferencePath(MetadataReference reference)
    {
        if (string.IsNullOrWhiteSpace(reference.Display))
        {
            return null;
        }

        return Path.GetFullPath(reference.Display);
    }

    private static CompilerReferenceClosure BuildMetadataReferences(string referenceRoot)
    {
        string root = Path.GetFullPath(referenceRoot);
        CompilerReferenceInventory inventory = LoadCompilerReferenceInventory(root);
        string platformDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)
            ?? throw new ExtractionException("The runtime platform assembly directory is unavailable.");
        string canonicalPlatformDirectory = Path.GetFullPath(platformDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string[] platformPaths = EnumerateManagedPlatformPaths(canonicalPlatformDirectory);
        if (platformPaths.Length == 0)
        {
            throw new ExtractionException("The compiler reference inventory has no runtime-platform entries; the compiler closure is incomplete.");
        }
        string[] closureDirectories = CompilerReferenceClosurePaths
            .Select(relativePath =>
            {
                string directory = Path.GetFullPath(Path.Combine(root, relativePath));
                EnsureWithin(root, directory);
                if (!Directory.Exists(directory))
                {
                    throw new ExtractionException($"The compiler/reference closure is missing '{relativePath}'.");
                }

                return directory;
            })
            .ToArray();

        ValidateReferenceInventoryPaths(inventory, root, platformPaths, closureDirectories);

        List<MetadataReference> references = [];
        List<CompilerReferenceIdentity> identities = [];
        Dictionary<string, string> resolvedPaths = new(StringComparer.Ordinal);
        HashSet<string> selectedNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (CompilerReferencePin pin in inventory.References)
        {
            string fullPath = ResolveCompilerReferencePath(root, canonicalPlatformDirectory, pin.Path);
            if (!File.Exists(fullPath))
            {
                throw new ExtractionException($"The compiler reference inventory member does not exist: {pin.Path}.");
            }

            string actualSha;
            try
            {
                actualSha = Sha256(File.ReadAllBytes(fullPath));
            }
            catch (Exception exception)
            {
                throw new ExtractionException($"The compiler reference inventory member '{pin.Path}' could not be hashed: {exception.Message}");
            }

            if (actualSha != pin.Sha256)
            {
                throw new ExtractionException($"The compiler reference inventory detected byte drift in '{pin.Path}': expected {pin.Sha256}, got {actualSha}.");
            }

            (string assemblyName, string mvid, string[] dependencyNames) = ReadCompilerReferenceMetadata(pin.Path, fullPath);
            if (assemblyName != pin.AssemblyName)
            {
                throw new ExtractionException($"The compiler reference inventory detected assembly identity drift in '{pin.Path}': expected {pin.AssemblyName}, got {assemblyName}.");
            }

            if (!string.Equals(mvid, pin.Mvid, StringComparison.Ordinal))
            {
                throw new ExtractionException($"The compiler reference inventory detected MVID drift in '{pin.Path}': expected {pin.Mvid}, got {mvid}.");
            }

            if (pin.Selected)
            {
                if (!selectedNames.Add(pin.AssemblyName))
                {
                    throw new ExtractionException($"The compiler reference inventory selects assembly '{pin.AssemblyName}' more than once.");
                }

                try
                {
                    references.Add(MetadataReference.CreateFromFile(fullPath));
                }
                catch (Exception exception)
                {
                    throw new ExtractionException($"The compiler/reference inventory member '{pin.Path}' is not valid metadata: {exception.Message}");
                }
            }

            identities.Add(new CompilerReferenceIdentity(pin.Path, pin.AssemblyName, pin.Sha256, pin.Mvid, pin.Selected, dependencyNames));
            resolvedPaths.Add(pin.Path, fullPath);
        }

        string[] missingRequired = RequiredCompilerAssemblyNames
            .Where(required => !selectedNames.Contains(required))
            .ToArray();
        if (missingRequired.Length != 0)
        {
            throw new ExtractionException("The compiler/reference inventory is missing required selected assemblies: " +
                string.Join(", ", missingRequired));
        }

        return new(references.ToArray(), identities.ToArray(), resolvedPaths);
    }

    private static string[] EnumerateManagedPlatformPaths(string platformDirectory)
    {
        List<string> paths = [];
        foreach (string path in Directory.EnumerateFiles(platformDirectory, "*.dll", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                     .ThenBy(path => path, StringComparer.Ordinal))
        {
            try
            {
                using FileStream stream = File.OpenRead(path);
                using PEReader peReader = new(stream);
                if (peReader.HasMetadata)
                {
                    paths.Add($"platform/{Path.GetFileName(path)}");
                }
            }
            catch (Exception exception)
            {
                throw new ExtractionException($"The runtime platform binary '{Path.GetFileName(path)}' could not be inspected: {exception.Message}");
            }
        }

        return paths.ToArray();
    }

    private static CompilerReferenceInventory LoadCompilerReferenceInventory(string root)
    {
        string path = Path.GetFullPath(Path.Combine(root, CompilerReferenceInventoryPath));
        EnsureWithin(root, path);
        if (!File.Exists(path))
        {
            throw new ExtractionException($"The checked-in compiler reference inventory does not exist: {CompilerReferenceInventoryPath}.");
        }

        byte[] bytes = File.ReadAllBytes(path);
        if (Sha256(bytes) != CompilerReferenceInventorySha256)
        {
            throw new ExtractionException("The checked-in compiler reference inventory bytes changed from the reviewed pin.");
        }

        CompilerReferenceInventory inventory;
        try
        {
            inventory = JsonSerializer.Deserialize<CompilerReferenceInventory>(bytes, JsonOptions)
                ?? throw new ExtractionException("The compiler reference inventory was empty.");
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"The compiler reference inventory is invalid: {exception.Message}");
        }

        if (inventory.SchemaVersion != CompilerReferenceInventorySchemaVersion || inventory.References is null ||
            inventory.References.Any(static reference => reference is null))
        {
            throw new ExtractionException("The checked-in compiler reference inventory header changed.");
        }

        ValidateCompilerReferencePins(inventory.References);
        if (inventory.Count != inventory.References.Length ||
            inventory.AggregateSha256 != CompilerReferenceAggregateSha256(inventory.References))
        {
            throw new ExtractionException("The checked-in compiler reference inventory count or aggregate changed.");
        }

        RequireSha256(inventory.AggregateSha256, "compiler reference inventory aggregate hash");
        if (!Serialize(inventory).AsSpan().SequenceEqual(bytes))
        {
            throw new ExtractionException("The checked-in compiler reference inventory is not canonical.");
        }

        return inventory;
    }

    private static void ValidateCompilerReferencePins(CompilerReferencePin[] references)
    {
        if (references.Length == 0)
        {
            throw new ExtractionException("The compiler/reference inventory has no pinned references.");
        }

        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        string[] orderedPaths = references
            .Select(static reference => reference.Path)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (!references.Select(static reference => reference.Path).SequenceEqual(orderedPaths, StringComparer.Ordinal))
        {
            throw new ExtractionException("The compiler/reference inventory is not in canonical path order.");
        }

        foreach (CompilerReferencePin reference in references)
        {
            if (string.IsNullOrWhiteSpace(reference.Path) || !paths.Add(reference.Path) ||
                string.IsNullOrWhiteSpace(reference.AssemblyName) ||
                !string.Equals(reference.AssemblyName, Path.GetFileNameWithoutExtension(reference.Path), StringComparison.OrdinalIgnoreCase))
            {
                throw new ExtractionException("The compiler/reference inventory contains duplicate or malformed identities.");
            }

            string normalized = Normalize(reference.Path);
            if (!string.Equals(normalized, reference.Path, StringComparison.Ordinal) ||
                normalized.Contains("../", StringComparison.Ordinal) || normalized.Contains("/..", StringComparison.Ordinal) ||
                !normalized.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                throw new ExtractionException($"The compiler reference path is not canonical: {reference.Path}.");
            }

            bool isPlatform = normalized.StartsWith("platform/", StringComparison.Ordinal);
            if (isPlatform && Path.GetFileName(normalized["platform/".Length..]) != normalized["platform/".Length..])
            {
                throw new ExtractionException($"The platform compiler reference path is not a file name: {reference.Path}.");
            }

            bool isClosure = CompilerReferenceClosurePaths.Any(relativePath =>
                normalized.StartsWith(Normalize(relativePath) + "/", StringComparison.Ordinal));
            if (!isPlatform && !isClosure)
            {
                throw new ExtractionException($"The compiler reference escaped the admitted closure: {reference.Path}.");
            }

            RequireSha256(reference.Sha256, $"compiler reference {reference.Path}");
            if (!Guid.TryParseExact(reference.Mvid, "D", out _))
            {
                throw new ExtractionException($"Compiler reference {reference.Path} has an invalid MVID.");
            }
        }
    }

    private static void ValidateReferenceInventoryPaths(
        CompilerReferenceInventory inventory,
        string root,
        string[] platformPaths,
        string[] closureDirectories)
    {
        HashSet<string> expected = inventory.References
            .Select(static reference => reference.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> actual = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in platformPaths)
        {
            string logicalPath = $"platform/{Path.GetFileName(path)}";
            if (!actual.Add(logicalPath))
            {
                throw new ExtractionException($"The compiler reference inventory contains a duplicate runtime entry: {logicalPath}.");
            }
        }

        foreach (string directory in closureDirectories)
        {
            foreach (string path in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                         .ThenBy(path => path, StringComparer.Ordinal))
            {
                string logicalPath = Normalize(Path.GetRelativePath(root, path));
                if (!actual.Add(logicalPath))
                {
                    throw new ExtractionException($"The compiler reference closure contains a duplicate inventory path: {logicalPath}.");
                }
            }
        }

        string[] additions = actual.Except(expected, StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        string[] removals = expected.Except(actual, StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (additions.Length != 0 || removals.Length != 0)
        {
            throw new ExtractionException(
                "The compiler reference inventory path set changed. " +
                $"Additions: [{string.Join(", ", additions)}]. Removals: [{string.Join(", ", removals)}].");
        }
    }

    private static string ResolveCompilerReferencePath(string root, string platformDirectory, string logicalPath)
    {
        if (logicalPath.StartsWith("platform/", StringComparison.Ordinal))
        {
            string fileName = logicalPath["platform/".Length..];
            if (fileName.Length == 0 || Path.GetFileName(fileName) != fileName)
            {
                throw new ExtractionException($"The platform compiler reference path is not a file name: {logicalPath}.");
            }

            return Path.Combine(platformDirectory, fileName);
        }

        string fullPath = Path.GetFullPath(Path.Combine(root, logicalPath));
        EnsureWithin(root, fullPath);
        return fullPath;
    }

    private static (string AssemblyName, string Mvid, string[] Dependencies) ReadCompilerReferenceMetadata(string logicalPath, string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            using PEReader peReader = new(stream);
            if (!peReader.HasMetadata)
            {
                throw new ExtractionException($"The compiler reference '{logicalPath}' has no managed metadata.");
            }

            using MetadataReaderProvider provider = MetadataReaderProvider.FromMetadataImage(peReader.GetMetadata().GetContent());
            MetadataReader reader = provider.GetMetadataReader();
            AssemblyDefinition assembly = reader.GetAssemblyDefinition();
            string assemblyName = reader.GetString(assembly.Name);
            string mvid = reader.GetGuid(reader.GetModuleDefinition().Mvid).ToString("D");
            string[] dependencies = reader.AssemblyReferences
                .Select(handle => reader.GetString(reader.GetAssemblyReference(handle).Name))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            return (assemblyName, mvid, dependencies);
        }
        catch (ExtractionException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ExtractionException($"The compiler reference '{logicalPath}' is not valid managed metadata: {exception.Message}");
        }
    }

    private static string CompilerReferenceAggregateSha256(IEnumerable<CompilerReferencePin> references) =>
        Sha256(Encoding.UTF8.GetBytes(string.Join('\n', references.Select(reference =>
            $"{reference.Path}\0{reference.AssemblyName}\0{reference.Sha256}\0{reference.Mvid}\0{reference.Selected}")) + "\n"));

    private static string CompilationAssemblyName(string sourcePath) => sourcePath switch
    {
        MainnetDiPath => "Nethermind.Init",
        WorldStatePath or StateProviderPath => "Nethermind.State",
        TransactionPath or TransactionStdPath => "Nethermind.Core",
        _ => "Nethermind.Evm",
    };

    private static SourceFile ReadSource(string root, string relativePath, string role)
    {
        string path = Path.GetFullPath(Path.Combine(root, relativePath));
        EnsureWithin(root, path);
        if (!File.Exists(path))
        {
            throw new ExtractionException($"Missing {role}: {relativePath}.");
        }

        byte[] bytes = File.ReadAllBytes(path);
        SourceText text;
        try
        {
            text = SourceText.From(bytes, bytes.Length, Encoding.UTF8, canBeEmbedded: true);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ExtractionException($"Source {relativePath} is not strict UTF-8: {exception.Message}.");
        }

        SyntaxTree tree = CSharpSyntaxTree.ParseText(text, ParseOptions, relativePath);
        CompilationUnitSyntax syntax = tree.GetCompilationUnitRoot();
        if (syntax.ContainsDiagnostics && syntax.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            throw new ExtractionException($"Cannot parse {relativePath}: " +
                string.Join("; ", syntax.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                    .Select(static diagnostic => diagnostic.GetMessage())));
        }

        return new SourceFile(relativePath, role, Sha256(bytes), bytes, tree, syntax);
    }

    private static AdmissionClosure ReadAdmissionClosure(string root)
    {
        string path = Path.GetFullPath(Path.Combine(root, AdmissionClosurePath));
        EnsureWithin(root, path);
        if (!File.Exists(path))
        {
            throw new ExtractionException("Missing reviewed source-admission closure: " + AdmissionClosurePath + ".");
        }

        byte[] bytes = File.ReadAllBytes(path);
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ExtractionException("The reviewed source-admission closure is not strict UTF-8: " + exception.Message);
        }

        List<AdmissionSource> sources = [];
        List<AdmissionDependency> dependencies = [];
        foreach (string raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            string[] fields = line.Split('|');
            switch (fields)
            {
                case ["source", string role, string sourcePath, string sha256]:
                    RequireSha256(sha256, "Reviewed source closure SHA-256");
                    sources.Add(new AdmissionSource(role, sourcePath, sha256));
                    break;
                case ["dependency", string role, string dependencyPath, string sha256]:
                    RequireSha256(sha256, "Reviewed dependency closure SHA-256");
                    dependencies.Add(new AdmissionDependency(role, dependencyPath, sha256));
                    break;
                default:
                    throw new ExtractionException("The reviewed source-admission closure contains an unrecognized line: " + line);
            }
        }

        if (sources.Count != SourceSpecs.Length || dependencies.Count != DependencySpecs.Length)
        {
            throw new ExtractionException($"The reviewed source-admission closure must list exactly {SourceSpecs.Length} sources and {DependencySpecs.Length} dependencies.");
        }

        return new AdmissionClosure(Sha256(bytes), sources.ToArray(), dependencies.ToArray());
    }

    private static void ValidateSourceClosure(SourceFile[] sources, AdmissionClosure closure)
    {
        if (closure.Sources.Length != sources.Length)
        {
            throw new ExtractionException("The reviewed source-admission closure source count changed.");
        }

        for (int index = 0; index < sources.Length; index++)
        {
            SourceFile source = sources[index];
            AdmissionSource admitted = closure.Sources[index];
            if (admitted.Path != source.RelativePath || admitted.Role != source.Role || admitted.Sha256 != source.Sha256)
            {
                throw new ExtractionException("A reviewed source file changed; deliberately review and update " +
                    AdmissionClosurePath + " before regeneration: " + source.RelativePath + ".");
            }
        }
    }

    private static void ValidateDependencyClosure(string root, DependencyIdentity[] dependencies, AdmissionClosure closure)
    {
        // Generated dependencies are identity inputs, but not semantic oracles. The explicit
        // dependency identities are retained in the IR so a changed upstream lowering cannot be
        // mistaken for a stable bridge.
        if (dependencies.Length != DependencySpecs.Length || dependencies.Any(static dependency => !IsSha256(dependency.Sha256)) ||
            closure.Dependencies.Length != dependencies.Length)
        {
            throw new ExtractionException("The generated dependency identity set is incomplete.");
        }

        for (int index = 0; index < dependencies.Length; index++)
        {
            DependencyIdentity dependency = dependencies[index];
            AdmissionDependency admitted = closure.Dependencies[index];
            if (admitted.Path != dependency.Path || admitted.Role != dependency.Role || admitted.Sha256 != dependency.Sha256)
            {
                throw new ExtractionException("A generated semantic dependency changed; review " + AdmissionClosurePath + " before regeneration: " + dependency.Path + ".");
            }

            string path = Path.GetFullPath(Path.Combine(root, dependency.Path));
            EnsureWithin(root, path);
            if (!File.Exists(path))
            {
                throw new ExtractionException("A generated dependency disappeared: " + dependency.Path + ".");
            }
        }
    }

    private static SourceFile FindSource(SourceFile[] sources, string path) =>
        sources.SingleOrDefault(source => source.RelativePath == path)
        ?? throw new ExtractionException("Expected source was not loaded: " + path + ".");

    private static RouteShape ValidateRoute(SourceFile[] sources, SemanticContext context, List<SourceBinding> bindings)
    {
        SourceFile processor = FindSource(sources, TransactionProcessorPath);
        SourceFile routing = FindSource(sources, RoutingKernelPath);
        SourceFile registration = FindSource(sources, MainnetDiPath);
        SourceFile codeRepositoryInterface = FindSource(sources, CodeRepositoryInterfacePath);
        SourceFile codeRepository = FindSource(sources, CodeRepositoryPath);
        SourceFile cacheRepository = FindSource(sources, CacheCodeRepositoryPath);
        SourceFile worldStateInterface = FindSource(sources, WorldStateInterfacePath);
        SourceFile worldState = FindSource(sources, WorldStatePath);
        SourceFile stateProvider = FindSource(sources, StateProviderPath);
        SourceFile gasPolicy = FindSource(sources, GasPolicyInterfacePath);
        SourceFile ethereumGasPolicy = FindSource(sources, EthereumGasPolicyPath);

        MethodDeclarationSyntax useSystem = RequireMethod(routing, "SystemTransactionRoutingKernel", "UseSystemProcessor", 2, "boolisSystemTransaction,ExecutionOptionsoptions");
        string routingBody = Canonical(BodyOrExpression(useSystem));
        RequireCanonicalContains(routingBody, "isSystemTransaction||options==ExecutionOptions.SkipValidation",
            "The ordinary/system processor routing predicate changed.");

        MethodDeclarationSyntax load = RequireMethod(registration, "BlockProcessingModule", "Load", 1, "ContainerBuilderbuilder");
        InvocationExpressionSyntax blobRegistration = RequireRegistrationInvocation(registration, load,
            "ITransactionProcessor.IBlobBaseFeeCalculator,BlobBaseFeeCalculator");
        InvocationExpressionSyntax processorRegistration = RequireRegistrationInvocation(registration, load,
            "ITransactionProcessor,EthereumTransactionProcessor");
        InvocationExpressionSyntax codeRegistration = RequireRegistrationInvocation(registration, load,
            "ICodeInfoRepository,CacheCodeInfoRepository");
        InvocationExpressionSyntax worldRegistration = RequireRegistrationInvocation(registration, load,
            "IWorldState,WorldState");

        ClassDeclarationSyntax ethereumProcessor = RequireClass(processor, "EthereumTransactionProcessor");
        RequireCanonicalContains(Canonical(ethereumProcessor.BaseList!), "EthereumTransactionProcessorBase(",
            "EthereumTransactionProcessor no longer reaches the Ethereum transaction processor base.");
        ClassDeclarationSyntax ethereumBase = RequireClass(processor, "EthereumTransactionProcessorBase");
        RequireCanonicalContains(Canonical(ethereumBase.BaseList!), "TransactionProcessorBase<EthereumGasPolicy>",
            "EthereumTransactionProcessorBase no longer closes TransactionProcessorBase<EthereumGasPolicy>.");

        MethodDeclarationSyntax repositoryLookup = RequireMethod(codeRepositoryInterface, "ICodeInfoRepository", "GetCachedCodeInfo", 4,
            "CodeInfoGetCachedCodeInfo(AddresscodeSource,boolfollowDelegation,IReleaseSpecvmSpec,outAddress?delegationAddress)");
        MethodDeclarationSyntax repositoryImplementationLookup = RequireMethod(codeRepository, "CodeInfoRepository", "GetCachedCodeInfo", 4,
            "CodeInfoGetCachedCodeInfo(AddresscodeSource,boolfollowDelegation,IReleaseSpecvmSpec,outAddress?delegationAddress)");
        MethodDeclarationSyntax cachedRepositoryLookup = RequireMethod(cacheRepository, "CacheCodeInfoRepository", "GetCachedCodeInfo", 4,
            "CodeInfoGetCachedCodeInfo(AddresscodeSource,boolfollowDelegation,IReleaseSpecvmSpec,outAddress?delegationAddress)");
        PropertyDeclarationSyntax codeIsEmpty = RequireProperty(FindSource(sources, CodeInfoPath), "CodeInfo", "IsEmpty");
        if (codeIsEmpty.ExpressionBody is not { Expression: var codeIsEmptyExpression } ||
            Canonical(codeIsEmptyExpression) != "ReferenceEquals(_analyzer,_emptyAnalyzer)")
        {
            throw new ExtractionException("CodeInfo.IsEmpty must retain the analyzer-sentinel predicate.");
        }
        MethodDeclarationSyntax commitInterface = RequireMethod(worldStateInterface, "IWorldState", "Commit", 4,
            "voidCommit(IReleaseSpecreleaseSpec,IWorldStateTracertracer,boolisGenesis=false,boolcommitRoots=true)");
        MethodDeclarationSyntax commitImplementation = RequireMethod(worldState, "WorldState", "Commit", 4,
            "voidCommit(IReleaseSpecreleaseSpec,IWorldStateTracertracer,boolisGenesis=false,boolcommitRoots=true)");
        MethodDeclarationSyntax stateProviderCommit = RequireMethod(stateProvider, "StateProvider", "Commit", 4,
            "voidCommit(IReleaseSpecreleaseSpec,IWorldStateTracerstateTracer,boolcommitRoots,boolisGenesis)");
        InvocationExpressionSyntax worldToProviderCommit = RequireInvocation(commitImplementation, "Commit", "_stateProvider",
            "releaseSpec,tracer,commitRoots,isGenesis");
        MethodDeclarationSyntax gasPolicyLookup = RequireMethod(gasPolicy, "IGasPolicy", "TryCreateAvailableFromIntrinsic", 4,
            "staticabstractboolTryCreateAvailableFromIntrinsic(ulonggasLimit,inTSelfintrinsicGas,IReleaseSpecspec,outTSelfavailable)");
        MethodDeclarationSyntax ethereumGasLookup = RequireMethod(ethereumGasPolicy, "EthereumGasPolicy", "TryCreateAvailableFromIntrinsic", 4,
            "boolTryCreateAvailableFromIntrinsic(ulonggasLimit,inEthereumGasPolicyintrinsicGas,IReleaseSpecspec,outEthereumGasPolicyavailable)");
        string repositoryBody = Canonical(repositoryImplementationLookup);
        RequireCanonicalContains(repositoryBody, "delegationAddress=null", "CodeInfoRepository must initialize the delegation designator to null.");
        RequireCanonicalContains(repositoryBody, "if(followDelegation)", "CodeInfoRepository must preserve the followDelegation gate.");
        RequireCanonicalContains(repositoryBody, "ICodeInfoRepository.TryGetDelegatedAddress", "CodeInfoRepository delegation extraction changed.");
        string cacheBody = Canonical(cachedRepositoryLookup);
        RequireCanonicalContains(cacheBody, "_inner.GetCachedCodeInfo(codeSource,followDelegation,vmSpec,outdelegationAddress)", "CacheCodeInfoRepository must forward the exact lookup.");
        string worldCommitBody = Canonical(commitImplementation);
        RequireCanonicalContains(worldCommitBody, "_stateProvider.Commit(releaseSpec,tracer,commitRoots,isGenesis)", "WorldState must route commit to StateProvider with source argument order.");
        string gasBody = Canonical(ethereumGasLookup);
        RequireCanonicalContains(gasBody, "available=default", "EthereumGasPolicy must preserve the default available-gas out value on rejection.");

        SourceBinding[] routeBindings =
        [
            Bind(routing, "SystemTransactionRoutingKernel", "UseSystemProcessor", useSystem, context),
            Bind(registration, "BlockProcessingModule", "Load", load, context),
            Bind(registration, "BlockProcessingModule", "AddScoped<ITransactionProcessor.IBlobBaseFeeCalculator, BlobBaseFeeCalculator>", blobRegistration, context),
            Bind(registration, "BlockProcessingModule", "AddScoped<ITransactionProcessor, EthereumTransactionProcessor>", processorRegistration, context),
            Bind(registration, "BlockProcessingModule", "AddScoped<ICodeInfoRepository, CacheCodeInfoRepository>", codeRegistration, context),
            Bind(registration, "BlockProcessingModule", "AddScoped<IWorldState, WorldState>", worldRegistration, context),
            Bind(processor, "EthereumTransactionProcessor", "class", ethereumProcessor, context),
            Bind(processor, "EthereumTransactionProcessorBase", "class", ethereumBase, context),
            Bind(codeRepositoryInterface, "ICodeInfoRepository", "GetCachedCodeInfo", repositoryLookup, context),
            Bind(codeRepository, "CodeInfoRepository", "GetCachedCodeInfo", repositoryImplementationLookup, context),
            Bind(cacheRepository, "CacheCodeInfoRepository", "GetCachedCodeInfo", cachedRepositoryLookup, context),
            Bind(FindSource(sources, CodeInfoPath), "CodeInfo", "IsEmpty", codeIsEmpty, context),
            Bind(worldStateInterface, "IWorldState", "Commit", commitInterface, context),
            Bind(worldState, "WorldState", "Commit", commitImplementation, context),
            Bind(stateProvider, "StateProvider", "Commit", stateProviderCommit, context),
            Bind(worldState, "WorldState", "Commit -> StateProvider.Commit", worldToProviderCommit, context),
            Bind(gasPolicy, "IGasPolicy<TSelf>", "TryCreateAvailableFromIntrinsic", gasPolicyLookup, context),
            Bind(ethereumGasPolicy, "EthereumGasPolicy", "TryCreateAvailableFromIntrinsic", ethereumGasLookup, context),
        ];
        bindings.AddRange(routeBindings);

        return new RouteShape(
            "BlockProcessingModule.AddScoped<ITransactionProcessor, EthereumTransactionProcessor>",
            "EthereumTransactionProcessorBase : TransactionProcessorBase<EthereumGasPolicy>",
            "BlockProcessingModule.AddScoped<IWorldState, WorldState>",
            "BlockProcessingModule.AddScoped<ICodeInfoRepository, CacheCodeInfoRepository>",
            "BlockProcessingModule.AddScoped<ITransactionProcessor.IBlobBaseFeeCalculator, BlobBaseFeeCalculator>",
            "IWorldState.Commit(IReleaseSpec, IWorldStateTracer, bool, bool)",
            "WorldState.Commit -> StateProvider.Commit",
            [
                "SystemTransactionProcessor selected by UseSystemProcessor is outside the ordinary route.",
                "XDC and Taiko processor overrides are outside the standard-mainnet registration.",
                "Parallel processor construction and BAL-specific orchestration are outside this package.",
            ],
            routeBindings);
    }

    private static OptionShape ValidateOptions(SourceFile source, SemanticContext context, List<SourceBinding> bindings)
    {
        EnumDeclarationSyntax declaration = RequireSingleEnum(source, "ExecutionOptions");
        if (declaration.BaseList is not null)
        {
            throw new ExtractionException("ExecutionOptions must retain its default signed Int32 backing type.");
        }

        int none = RequireEnumValue(declaration, "None", "0");
        int commit = RequireEnumValue(declaration, "Commit", "1");
        int restore = RequireEnumValue(declaration, "Restore", "2");
        int skipValidation = RequireEnumValue(declaration, "SkipValidation", "4");
        int warmup = RequireEnumValue(declaration, "Warmup", "8");
        int buildUp = RequireEnumValue(declaration, "BuildUp", "16");
        SourceBinding binding = Bind(source, "ExecutionOptions", "enum", declaration, context);
        bindings.Add(binding);
        return new OptionShape(none, commit, restore, skipValidation, warmup, buildUp, string.Empty, string.Empty, string.Empty, binding);
    }

    private static DispatchValidation ValidateDispatch(
        SourceFile processor,
        SourceFile codeRepositoryInterface,
        SourceFile codeInfo,
        SourceFile codeRepository,
        SourceFile cacheRepository,
        SourceFile worldStateInterface,
        SourceFile worldState,
        SourceFile gasPolicy,
        SourceFile ethereumGasPolicy,
        SourceFile transaction,
        OptionShape optionShape,
        SemanticContext context,
        List<SourceBinding> bindings)
    {
        MethodDeclarationSyntax process = RequireMethod(processor, "TransactionProcessorBase", "Process", 3,
            "TransactionResultProcess(Transactiontransaction,ITxTracertxTracer,ExecutionOptionsoptions)");
        MethodDeclarationSyntax executeCore = RequireMethod(processor, "TransactionProcessorBase", "ExecuteCore", 3,
            "TransactionResultExecuteCore(Transactiontx,ITxTracertracer,ExecutionOptionsopts)");
        MethodDeclarationSyntax executeThree = RequireMethod(processor, "TransactionProcessorBase", "Execute", 3,
            "TransactionResultExecute(Transactiontx,ITxTracertracer,ExecutionOptionsopts)");
        MethodDeclarationSyntax recoverBeforeIntrinsic = RequireMethod(processor, "TransactionProcessorBase", "RecoverSenderBeforeIntrinsicGas", 2,
            "voidRecoverSenderBeforeIntrinsicGas(Transactiontx,IReleaseSpecspec)");
        MethodDeclarationSyntax calculateIntrinsic = RequireMethod(processor, "TransactionProcessorBase", "CalculateIntrinsicGas", 3,
            "IntrinsicGas<TGasPolicy>CalculateIntrinsicGas(Transactiontx,IReleaseSpecspec,ulongblockGasLimit)");
        MethodDeclarationSyntax execute = RequireExecuteOverload(processor);
        InvocationExpressionSyntax processToCore = RequireDirectInvocation(process, "ExecuteCore", string.Empty,
            "transaction,txTracer,options");
        InvocationExpressionSyntax systemRoute = RequireDirectInvocation(executeCore, "UseSystemProcessor", "SystemTransactionRoutingKernel",
            "tx.IsSystem(),opts");
        InvocationExpressionSyntax coreToExecute = RequireDirectInvocation(executeCore, "Execute", string.Empty,
            "tx,tracer,opts");
        InvocationExpressionSyntax recoverCall = RequireDirectInvocation(executeThree, "RecoverSenderBeforeIntrinsicGas", string.Empty,
            "tx,spec");
        InvocationExpressionSyntax intrinsicCall = RequireDirectInvocation(executeThree, "CalculateIntrinsicGas", string.Empty,
            "tx,spec,header.GasLimit");
        InvocationExpressionSyntax enterSix = RequireDirectInvocation(executeThree, "Execute", string.Empty,
            "tx,tracer,opts,header,spec,inintrinsicGas");
        RequireOrder("ordinary call path", systemRoute, coreToExecute);
        RequireOrder("intrinsic-to-dispatch path", recoverCall, intrinsicCall, enterSix);
        string recoverBeforeBody = Canonical(recoverBeforeIntrinsic);
        RequireCanonicalContains(recoverBeforeBody, "spec.IsEip2780Enabled&&tx.IsMessageCall&&tx.Signatureisnotnull",
            "The EIP-2780 sender recovery boundary changed.");
        RequireCanonicalContains(recoverBeforeBody, "tx.SenderAddress=Ecdsa.RecoverAddress(tx,!spec.ValidateChainId)",
            "The EIP-2780 sender recovery assignment changed.");
        MethodDeclarationSyntax prepare = RequireMethod(processor, "TransactionProcessorBase", "PrepareSimpleTransferFastPath", 4,
            "Address?PrepareSimpleTransferFastPath(Transactiontx,IReleaseSpecspec,outCodeInfo?preloadedCodeInfo,outAddress?preloadedDelegationAddress)");
        MethodDeclarationSyntax simpleHelper = RequireMethod(processor, "TransactionProcessorBase", "IsSimpleTransferFastPathCandidate", 2,
            "boolIsSimpleTransferFastPathCandidate(Transactiontx,boolisCodeOverridable)");
        MethodDeclarationSyntax emptyHelper = RequireMethod(processor, "TransactionProcessorBase", "HasNoExecutableCode", 2,
            "boolHasNoExecutableCode(CodeInfocodeInfo,Address?delegationAddress)");
        RequireCanonicalContains(Canonical(BodyOrExpression(simpleHelper)),
            "!isCodeOverridable&&tx.AuthorizationListisnull&&!ForceSimpleTransferDisabled",
            "The simple-transfer candidate guard changed.");
        MethodDeclarationSyntax calculateAvailable = RequireMethod(processor, "TransactionProcessorBase", "CalculateAvailableGas", 4,
            "TGasPolicyCalculateAvailableGas(Transactiontx,IReleaseSpecspec,inIntrinsicGas<TGasPolicy>intrinsicGas,outTGasPolicygasAvailable)");
        MethodDeclarationSyntax simpleHandoff = RequireMethod(processor, "TransactionProcessorBase", "ExecuteSimpleTransfer", 15,
            "TransactionResultExecuteSimpleTransfer(Transactiontx,BlockHeaderheader,IReleaseSpecspec,ITxTracertracer,ExecutionOptionsopts,boolrestore,boolcommit,booldeleteCallerAccount,Addressrecipient,inIntrinsicGas<TGasPolicy>intrinsicGas,TGasPolicygasAvailable,inUInt256opcodeGasPrice,inUInt256premiumPerGas,inUInt256senderReservedGasPayment,inUInt256blobBaseFee)");
        MethodDeclarationSyntax evmHandoff = RequireMethod(processor, "TransactionProcessorBase", "ExecuteEvmTransaction", 16,
            "TransactionResultExecuteEvmTransaction(Transactiontx,BlockHeaderheader,IReleaseSpecspec,ITxTracertracer,ExecutionOptionsopts,boolrestore,boolcommit,booldeleteCallerAccount,inIntrinsicGas<TGasPolicy>intrinsicGas,TGasPolicygasAvailable,inUInt256opcodeGasPrice,inUInt256premiumPerGas,inUInt256senderReservedGasPayment,inUInt256blobBaseFee,CodeInfo?preloadedCodeInfo,Address?preloadedDelegationAddress)");

        VariableDeclaratorSyntax restore = RequireLocal(execute, "restore");
        VariableDeclaratorSyntax commit = RequireLocal(execute, "commit");
        VariableDeclaratorSyntax commitBefore = RequireLocal(execute, "commitBeforeExecution");
        InvocationExpressionSyntax prepareCall = RequireDirectInvocation(execute, "PrepareSimpleTransferFastPath", string.Empty,
            "tx,spec,outCodeInfo?preloadedCodeInfo,outAddress?preloadedDelegationAddress");
        InvocationExpressionSyntax incrementCall = RequireDirectInvocation(execute, "IncrementNonce", string.Empty,
            "tx,header,spec,tracer,opts");
        InvocationExpressionSyntax commitCall = RequireDirectInvocation(execute, "Commit", "WorldState",
            "spec,tracer.IsTracingState?tracer:NullTxTracer.Instance,commitRoots:false");
        InvocationExpressionSyntax availableCall = RequireInvocation(execute, "CalculateAvailableGas", string.Empty,
            "tx,spec,inintrinsicGas,outTGasPolicygasAvailable");
        RequireOrder("post-nonce dispatch", prepareCall, commitCall, availableCall);

        InvocationExpressionSyntax simpleCall = RequireDispatchInvocation(execute, "ExecuteSimpleTransfer", 15,
            "tx,header,spec,tracer,opts,restore,commit,deleteCallerAccount,simpleTransferRecipient,inintrinsicGas,gasAvailable,inopcodeGasPrice,inpremiumPerGas,insenderReservedGasPayment,inblobBaseFee");
        InvocationExpressionSyntax evmCall = RequireDispatchInvocation(execute, "ExecuteEvmTransaction", 16,
            "tx,header,spec,tracer,opts,restore,commit,deleteCallerAccount,inintrinsicGas,gasAvailable,inopcodeGasPrice,inpremiumPerGas,insenderReservedGasPayment,inblobBaseFee,preloadedCodeInfo,preloadedDelegationAddress");
        RequireOrder("typed handoff", availableCall, simpleCall, evmCall);
        IfStatementSyntax simpleDispatchIf = RequireIfContaining(execute, simpleCall);
        IfStatementSyntax gasFailureIf = RequireIfContaining(execute, availableCall);
        if (Canonical(gasFailureIf.Condition) != ExpectedGasFailurePredicate)
        {
            throw new ExtractionException("The CalculateAvailableGas failure guard must retain the exact admitted predicate.");
        }

        if (gasFailureIf.Else is not null ||
            gasFailureIf.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().SingleOrDefault() is not { Expression: IdentifierNameSyntax { Identifier.ValueText: "result" } })
        {
            throw new ExtractionException("The CalculateAvailableGas failure guard must return result without an alternate branch or dispatch.");
        }

        PostNonceControlFlow postNonceBoundary = RequirePostNonceControlFlow(
            processor, context, execute, incrementCall, prepareCall, commitCall, availableCall, simpleCall, evmCall,
            gasFailureIf, simpleDispatchIf);
        GasClassification gasClassification = ValidateGasClassification(processor, calculateAvailable, context);

        IfStatementSyntax precommitIf = RequireIfContaining(execute, commitCall);
        if (Canonical(precommitIf.Condition) != "commitBeforeExecution")
        {
            throw new ExtractionException("The precommit request is no longer guarded by commitBeforeExecution.");
        }
        if (Canonical(simpleDispatchIf.Condition) != "simpleTransferRecipientisnotnull")
        {
            throw new ExtractionException("The simple/EVM dispatch condition must remain the exact simpleTransferRecipient non-null predicate.");
        }
        if (simpleDispatchIf.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().SingleOrDefault() is not { Expression: InvocationExpressionSyntax { } })
        {
            throw new ExtractionException("The simple-transfer branch no longer returns its typed handoff.");
        }

        ExpressionSyntax restoreExpression = RequireInitializer(restore);
        ExpressionSyntax commitExpression = RequireInitializer(commit);
        ExpressionSyntax commitBeforeExpression = RequireInitializer(commitBefore);
        string restoreFormula = Canonical(restoreExpression);
        string commitFormula = Canonical(commitExpression);
        string commitBeforeFormula = Canonical(commitBeforeExpression);
        SourceBinding restoreBinding = Bind(processor, "TransactionProcessorBase<TGasPolicy>", "restoreOption", restoreExpression, context);
        SourceBinding commitBinding = Bind(processor, "TransactionProcessorBase<TGasPolicy>", "commitOption", commitExpression, context);
        ValidateRestoreFormula(restoreExpression, restoreBinding, processor, context);
        ValidateCommitFormula(commitExpression, commitBinding, processor, context);
        RequireCanonicalContains(commitBeforeFormula, "commit&&(simpleTransferRecipientisnull||restore||tracer.IsTracingState)",
            "Precommit guard formula changed.");

        AssignmentExpressionSyntax nullCode = RequireAssignment(prepare, "preloadedCodeInfo", "null");
        AssignmentExpressionSyntax nullDelegation = RequireAssignment(prepare, "preloadedDelegationAddress", "null");
        VariableDeclaratorSyntax recipient = RequireLocal(prepare, "recipient");
        ExpressionSyntax recipientInitializer = RequireInitializer(recipient);
        if (Canonical(recipientInitializer) != "tx.To")
        {
            throw new ExtractionException("The fast-path recipient must be initialized from tx.To.");
        }

        IfStatementSyntax candidateIf = RequireIf(prepare, "recipientisnull||!IsSimpleTransferFastPathCandidate(tx,_isCodeOverridable)");
        // The helper invocation is required in the prepare condition; the local operation is
        // retained as a source-bound node rather than replaced with a semantic table row.
        InvocationExpressionSyntax candidateCall = FindInvocation(candidateIf.Condition, "IsSimpleTransferFastPathCandidate")
            ?? throw new ExtractionException("The fast-path candidate guard no longer calls IsSimpleTransferFastPathCandidate.");
        BinaryExpressionSyntax candidateCondition = candidateIf.Condition as BinaryExpressionSyntax
            ?? throw new ExtractionException("The fast-path guard must retain its short-circuit OR condition.");
        InvocationExpressionSyntax lookupCall = RequireInvocation(prepare, "GetCachedCodeInfo", "_codeInfoRepository",
            "recipient,followDelegation:!spec.IsEip8037Enabled,spec,outpreloadedDelegationAddress");
        ReturnStatementSyntax simpleReturn = RequireReturnContaining(prepare, "HasNoExecutableCode");
        if (nullCode.SpanStart >= nullDelegation.SpanStart || nullDelegation.SpanStart >= recipient.SpanStart ||
            recipient.SpanStart >= candidateIf.SpanStart || candidateIf.SpanStart >= lookupCall.SpanStart || lookupCall.SpanStart >= simpleReturn.SpanStart)
        {
            throw new ExtractionException("PrepareSimpleTransferFastPath source order changed around preload, candidate, lookup, or decision.");
        }
        InvocationExpressionSyntax simpleDecisionCall = FindInvocation(simpleReturn.Expression!, "HasNoExecutableCode")
            ?? throw new ExtractionException("The fast-path simple decision no longer calls HasNoExecutableCode.");
        if (Canonical(simpleReturn.Expression!) != "HasNoExecutableCode(preloadedCodeInfo,preloadedDelegationAddress)?recipient:null")
        {
            throw new ExtractionException("The simple-transfer decision no longer returns recipient only for empty, non-delegated code.");
        }
        BinaryExpressionSyntax simplePredicate = RequireSimplePredicate(emptyHelper);
        if (!Canonical(simplePredicate).StartsWith("delegationAddressisnull&&", StringComparison.Ordinal) ||
            !Canonical(simplePredicate).Contains("codeInfo.IsEmpty", StringComparison.Ordinal))
        {
            throw new ExtractionException("The simple decision no longer preserves delegation-first short circuiting and CodeInfo.IsEmpty.");
        }

        AssignmentExpressionSyntax defaultGas = RequireAssignment(ethereumGasPolicy, "available", "default");
        SyntaxNode[] gasFields =
        [
            RequireField(ethereumGasPolicy, "EthereumGasPolicy", "Value"),
            RequireField(ethereumGasPolicy, "EthereumGasPolicy", "StateReservoir"),
            RequireField(ethereumGasPolicy, "EthereumGasPolicy", "StateGasUsed"),
            RequireField(ethereumGasPolicy, "EthereumGasPolicy", "StateGasSpill"),
            RequireField(ethereumGasPolicy, "EthereumGasPolicy", "StateGasSpillRefunded"),
        ];

        PropertyDeclarationSyntax to = RequireProperty(transaction, "Transaction", "To");
        PropertyDeclarationSyntax authorization = RequireProperty(transaction, "Transaction", "AuthorizationList");
        if (Canonical(to.Type) != "Address?" || Canonical(authorization.Type) != "AuthorizationTuple[]?")
        {
            throw new ExtractionException("Transaction.To and Transaction.AuthorizationList must retain their admitted nullable types.");
        }

        RequireInvocationBinding(Bind(processor, "TransactionProcessorBase<TGasPolicy>", "codeLookup", lookupCall, context), "GetCachedCodeInfo", "_codeInfoRepository");
        RequireInvocationBinding(Bind(processor, "TransactionProcessorBase<TGasPolicy>", "incrementNonce", incrementCall, context), "IncrementNonce", string.Empty);
        RequireInvocationBinding(Bind(processor, "TransactionProcessorBase<TGasPolicy>", "precommitRequest", commitCall, context), "Commit", "WorldState");
        RequireInvocationBinding(Bind(processor, "TransactionProcessorBase<TGasPolicy>", "calculateAvailableGas", availableCall, context), "CalculateAvailableGas", string.Empty);
        RequireInvocationBinding(Bind(processor, "TransactionProcessorBase<TGasPolicy>", "simpleHandoff", simpleCall, context), "ExecuteSimpleTransfer", string.Empty);
        RequireInvocationBinding(Bind(processor, "TransactionProcessorBase<TGasPolicy>", "evmHandoff", evmCall, context), "ExecuteEvmTransaction", string.Empty);
        RequireInvocationBinding(Bind(processor, "TransactionProcessorBase<TGasPolicy>", "Process -> ExecuteCore", processToCore, context), "ExecuteCore", string.Empty);
        RequireInvocationBinding(Bind(processor, "TransactionProcessorBase<TGasPolicy>", "Execute(3) -> Execute(6)", enterSix, context), "Execute", string.Empty);
        bindings.Add(restoreBinding);
        bindings.Add(commitBinding);
        bindings.Add(postNonceBoundary.SuccessPredicateBinding);
        bindings.Add(gasClassification.ConditionBinding);
        bindings.Add(gasClassification.SuccessBinding);
        bindings.Add(gasClassification.FailureBinding);

        SourceBinding[] methodBindings =
        [
            Bind(processor, "TransactionProcessorBase<TGasPolicy>", "Process", process, context),
            Bind(processor, "TransactionProcessorBase<TGasPolicy>", "ExecuteCore", executeCore, context),
            Bind(processor, "TransactionProcessorBase<TGasPolicy>", "Execute(3)", executeThree, context),
            Bind(processor, "TransactionProcessorBase<TGasPolicy>", "RecoverSenderBeforeIntrinsicGas", recoverBeforeIntrinsic, context),
            Bind(processor, "TransactionProcessorBase<TGasPolicy>", "CalculateIntrinsicGas", calculateIntrinsic, context),
            Bind(processor, "TransactionProcessorBase<TGasPolicy>", "Execute(6)", execute, context),
            Bind(processor, "TransactionProcessorBase<TGasPolicy>", "Process -> ExecuteCore", processToCore, context),
            Bind(processor, "TransactionProcessorBase<TGasPolicy>", "ExecuteCore -> UseSystemProcessor", systemRoute, context),
            Bind(processor, "TransactionProcessorBase<TGasPolicy>", "ExecuteCore -> Execute(3)", coreToExecute, context),
            Bind(processor, "TransactionProcessorBase<TGasPolicy>", "Execute(3) -> RecoverSenderBeforeIntrinsicGas", recoverCall, context),
            Bind(processor, "TransactionProcessorBase<TGasPolicy>", "Execute(3) -> CalculateIntrinsicGas", intrinsicCall, context),
            Bind(processor, "TransactionProcessorBase<TGasPolicy>", "Execute(3) -> Execute(6)", enterSix, context),
            Bind(processor, "TransactionProcessorBase<TGasPolicy>", "Execute(6) -> IncrementNonce", incrementCall, context),
            Bind(processor, "TransactionProcessorBase<TGasPolicy>", "PrepareSimpleTransferFastPath", prepare, context),
            Bind(processor, "TransactionProcessorBase<TGasPolicy>", "IsSimpleTransferFastPathCandidate", simpleHelper, context),
            Bind(processor, "TransactionProcessorBase<TGasPolicy>", "HasNoExecutableCode", emptyHelper, context),
            Bind(processor, "TransactionProcessorBase<TGasPolicy>", "CalculateAvailableGas", calculateAvailable, context),
            Bind(processor, "TransactionProcessorBase<TGasPolicy>", "ExecuteSimpleTransfer", simpleHandoff, context),
            Bind(processor, "TransactionProcessorBase<TGasPolicy>", "ExecuteEvmTransaction", evmHandoff, context),
            Bind(processor, "TransactionProcessorBase<TGasPolicy>", "candidateGuardCall", candidateCall, context),
            Bind(processor, "TransactionProcessorBase<TGasPolicy>", "simpleDecisionCall", simpleDecisionCall, context),
            Bind(transaction, "Transaction", "To", to, context),
            Bind(transaction, "Transaction", "AuthorizationList", authorization, context),
            Bind(ethereumGasPolicy, "EthereumGasPolicy", "available = default", defaultGas, context),
        ];
        bindings.AddRange(methodBindings);

        List<StageShape> stages =
        [
            new("prepareSimpleTransferFastPath", 1, "source-helper", Bind(processor, "TransactionProcessorBase<TGasPolicy>", "PrepareSimpleTransferFastPath", prepare, context)),
            new("commitBeforeExecution", 2, "guarded-effect", Bind(processor, "TransactionProcessorBase<TGasPolicy>", "commitBeforeExecution", commitBefore, context)),
            new("calculateAvailableGas", 3, "virtual-call", Bind(processor, "TransactionProcessorBase<TGasPolicy>", "CalculateAvailableGas", availableCall, context)),
            new("dispatchSimpleTransfer", 4, "typed-handoff", Bind(processor, "TransactionProcessorBase<TGasPolicy>", "ExecuteSimpleTransfer", simpleCall, context)),
            new("dispatchEvm", 5, "typed-handoff", Bind(processor, "TransactionProcessorBase<TGasPolicy>", "ExecuteEvmTransaction", evmCall, context)),
        ];
        bindings.AddRange(stages.Select(static stage => stage.Binding));

        List<BranchShape> branches =
        [
            Branch("prepareInitializesPreloadedOutputs", 1, Canonical(nullCode), nullCode, TerminalKind.NoTerminal,
                [SemanticEffect.InitializePreloadedOutputs], Bind(processor, "TransactionProcessorBase<TGasPolicy>", "prepareInitializesPreloadedOutputs", nullCode, context)),
            Branch("prepareNoRecipient", 2, Canonical(candidateCondition.Left), candidateCondition.Left, TerminalKind.NoTerminal,
                [SemanticEffect.AssignSimpleRecipient], Bind(processor, "TransactionProcessorBase<TGasPolicy>", "prepareNoRecipient", candidateCondition.Left, context)),
            Branch("prepareNotCandidate", 3, Canonical(candidateCall), candidateCall, TerminalKind.NoTerminal,
                [SemanticEffect.AssignSimpleRecipient], Bind(processor, "TransactionProcessorBase<TGasPolicy>", "prepareNotCandidate", candidateCall, context)),
            Branch("lookupEscapes", 4, Canonical(lookupCall), lookupCall, TerminalKind.EscapedLookup,
                [SemanticEffect.LookupCodeInfo], Bind(processor, "TransactionProcessorBase<TGasPolicy>", "lookupEscapes", lookupCall, context)),
            Branch("lookupReturns", 5, Canonical(lookupCall), lookupCall, TerminalKind.NoTerminal,
                [SemanticEffect.LookupCodeInfo, SemanticEffect.PreserveDelegationDesignator], Bind(processor, "TransactionProcessorBase<TGasPolicy>", "lookupReturns", lookupCall, context)),
            Branch("commitEscapes", 6, Canonical(commitCall), commitCall, TerminalKind.EscapedPrecommit,
                [SemanticEffect.RequestCommit], Bind(processor, "TransactionProcessorBase<TGasPolicy>", "commitEscapes", commitCall, context)),
            Branch("gasRejected", 7, Canonical(gasFailureIf.Condition), gasFailureIf.Condition, TerminalKind.GasRejected,
                [SemanticEffect.PreserveFiveZeroGasPolicy], Bind(processor, "TransactionProcessorBase<TGasPolicy>", "gasRejected", gasFailureIf.Condition, context)),
            Branch("simpleHandoff", 8, Canonical(simpleCall), simpleCall, TerminalKind.SimpleHandoff,
                [SemanticEffect.HandoffSimple], Bind(processor, "TransactionProcessorBase<TGasPolicy>", "simpleHandoff", simpleCall, context)),
            Branch("evmHandoff", 9, Canonical(evmCall), evmCall, TerminalKind.EvmHandoff,
                [SemanticEffect.HandoffEvm], Bind(processor, "TransactionProcessorBase<TGasPolicy>", "evmHandoff", evmCall, context)),
        ];
        bindings.AddRange(branches.Select(static branch => branch.Binding));

        List<EffectShape> effects =
        [
            Effect("preloadOutputs", 1, "assignment", ["out CodeInfo?", "out Address?"], ["preloadedCodeInfo", "preloadedDelegationAddress"], "always before recipient and candidate checks", SemanticEffect.InitializePreloadedOutputs, Bind(processor, "TransactionProcessorBase<TGasPolicy>", "preloadOutputs", nullCode, context)),
            Effect("codeLookup", 2, "effectful lookup", ["recipient", "!spec.IsEip8037Enabled", "spec"], ["code handle", "delegation designator"], "lookup exception escapes", SemanticEffect.LookupCodeInfo, Bind(processor, "TransactionProcessorBase<TGasPolicy>", "codeLookup", lookupCall, context)),
            Effect("precommit", 3, "effectful commit request", ["spec", "tracer.IsTracingState"], ["WorldState.Commit(commitRoots:false)"], "commit exception escapes", SemanticEffect.RequestCommit, Bind(processor, "TransactionProcessorBase<TGasPolicy>", "precommit", commitCall, context)),
            Effect("gasFailure", 4, "out policy", ["gasLimit", "intrinsic"], ["five zero gas fields", "no dispatch"], "GasLimitBelowIntrinsicGas returns", SemanticEffect.PreserveFiveZeroGasPolicy, Bind(ethereumGasPolicy, "EthereumGasPolicy", "gasFailure", defaultGas, context)),
            Effect("simpleHandoff", 5, "typed call", ["common fields", "recipient", "gasAvailable"], ["ExecuteSimpleTransfer"], "callee owns downstream execution", SemanticEffect.HandoffSimple, Bind(processor, "TransactionProcessorBase<TGasPolicy>", "simpleHandoff", simpleCall, context)),
            Effect("evmHandoff", 6, "typed call", ["common fields", "preload"], ["ExecuteEvmTransaction"], "callee owns downstream execution", SemanticEffect.HandoffEvm, Bind(processor, "TransactionProcessorBase<TGasPolicy>", "evmHandoff", evmCall, context)),
        ];
        bindings.AddRange(effects.Select(static effect => effect.Binding));

        SemanticLowering semantics = BuildSemanticLowering(
            processor, prepare, simpleHelper, emptyHelper, execute, calculateAvailable, simpleHandoff, evmHandoff,
            nullCode, nullDelegation, recipient, candidateIf, lookupCall, simplePredicate, commitCall, availableCall,
            gasFailureIf, simpleCall, evmCall, to, authorization, gasFields, transaction, ethereumGasPolicy, optionShape, gasClassification, context);

        DispatchShape shape = new(
            restoreFormula,
            commitFormula,
            commitBeforeFormula,
            Canonical(candidateIf.Condition),
            Canonical(lookupCall),
            Canonical(simplePredicate),
            Canonical(defaultGas),
            stages.ToArray(),
            branches.ToArray(),
            effects.ToArray(),
            [Bind(processor, "TransactionProcessorBase<TGasPolicy>", "ExecuteSimpleTransfer", simpleCall, context), Bind(processor, "TransactionProcessorBase<TGasPolicy>", "ExecuteEvmTransaction", evmCall, context)],
            methodBindings,
            [],
            [PreloadKind.None, PreloadKind.Loaded],
            semantics,
            postNonceBoundary);
        return new DispatchValidation(shape, restoreFormula, commitFormula, commitBeforeFormula);
    }

    private static PostNonceControlFlow RequirePostNonceControlFlow(
        SourceFile processor,
        SemanticContext context,
        MethodDeclarationSyntax execute,
        InvocationExpressionSyntax increment,
        InvocationExpressionSyntax prepare,
        InvocationExpressionSyntax commit,
        InvocationExpressionSyntax calculateAvailable,
        InvocationExpressionSyntax simple,
        InvocationExpressionSyntax evm,
        IfStatementSyntax gasFailureIf,
        IfStatementSyntax simpleDispatchIf)
    {
        ControlFlowGraph graph = context.Graph(execute)
            ?? throw new ExtractionException("The six-parameter Execute method has no control-flow graph.");

        IfStatementSyntax[] enclosingPredicates = increment.Ancestors().OfType<IfStatementSyntax>()
            .Where(candidate => candidate.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() == execute)
            .ToArray();
        if (enclosingPredicates.Length != 1 || Canonical(enclosingPredicates[0].Condition) != ExpectedPostNonceSuccessPredicate)
        {
            throw new ExtractionException("The post-IncrementNonce success edge must retain the exact admitted validation/buy-gas/nonce predicate.");
        }

        IfStatementSyntax postNoncePredicate = enclosingPredicates[0];
        if (postNoncePredicate.Else is not null ||
            postNoncePredicate.Statement is not BlockSyntax predicateBody ||
            predicateBody.Statements.LastOrDefault() is not ReturnStatementSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "result" } } predicateFailureReturn ||
            predicateBody.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().Count() != 1)
        {
            throw new ExtractionException("The post-IncrementNonce failure branch must end in the admitted return result without an alternate branch.");
        }

        SourceBinding predicateBinding = Bind(
            processor,
            "TransactionProcessorBase<TGasPolicy>",
            "postNonceSuccessPredicate",
            postNoncePredicate.Condition,
            context);
        if (predicateBinding.CanonicalSyntax != ExpectedPostNonceSuccessPredicate)
        {
            throw new ExtractionException("The post-IncrementNonce success edge binding changed its exact admitted predicate.");
        }

        BasicBlock incrementBlock = RequireBlock(graph, increment, "IncrementNonce");
        BasicBlock prepareBlock = RequireBlock(graph, prepare, "PrepareSimpleTransferFastPath");
        BasicBlock commitBlock = RequireBlock(graph, commit, "WorldState.Commit");
        BasicBlock gasBlock = RequireBlock(graph, calculateAvailable, "CalculateAvailableGas");
        BasicBlock simpleBlock = RequireBlock(graph, simple, "ExecuteSimpleTransfer");
        BasicBlock evmBlock = RequireBlock(graph, evm, "ExecuteEvmTransaction");
        BasicBlock predicateBlock = RequireBlock(graph, postNoncePredicate.Condition, "post-IncrementNonce predicate");
        BasicBlock predicateFailureBody = RequireBlock(graph, predicateFailureReturn, "post-IncrementNonce failure branch");
        if (predicateBlock.BranchValue is null ||
            predicateBlock.ConditionalSuccessor?.Destination is not BasicBlock conditionalDestination ||
            predicateBlock.FallThroughSuccessor?.Destination is not BasicBlock fallThroughDestination)
        {
            throw new ExtractionException("The post-IncrementNonce predicate has no complete success/failure CFG split.");
        }

        BasicBlock predicateTrue = predicateBlock.ConditionKind switch
        {
            ControlFlowConditionKind.WhenTrue => conditionalDestination,
            ControlFlowConditionKind.WhenFalse => fallThroughDestination,
            _ => throw new ExtractionException("The post-IncrementNonce predicate has no Boolean CFG condition."),
        };
        BasicBlock predicateFalse = predicateBlock.ConditionKind switch
        {
            ControlFlowConditionKind.WhenTrue => fallThroughDestination,
            ControlFlowConditionKind.WhenFalse => conditionalDestination,
            _ => throw new ExtractionException("The post-IncrementNonce predicate has no Boolean CFG condition."),
        };
        if (!CanReach(predicateTrue, predicateFailureBody) || CanReach(predicateFalse, predicateFailureBody))
        {
            throw new ExtractionException("The post-IncrementNonce CFG does not bind the true failure edge and false success edge to the admitted predicate.");
        }

        BasicBlock successEdge = predicateFalse;
        BasicBlock entry = graph.Blocks.SingleOrDefault(static block => block.Kind.ToString() == "Entry")
            ?? throw new ExtractionException("The six-parameter Execute control-flow graph has no entry block.");
        if (!CanReach(successEdge, prepareBlock) || !CanReach(prepareBlock, commitBlock) || !CanReach(commitBlock, gasBlock) ||
            !CanReach(gasBlock, simpleBlock) || !CanReach(gasBlock, evmBlock))
        {
            throw new ExtractionException("The post-nonce stages are not in the source control-flow order.");
        }

        if (!CanReach(predicateBlock, incrementBlock) ||
            increment.SpanStart >= prepare.SpanStart || prepare.SpanStart >= commit.SpanStart || commit.SpanStart >= calculateAvailable.SpanStart)
        {
            throw new ExtractionException("The CFG-bound source order no longer places post-nonce stages after their source predecessors.");
        }

        RequireDominates(entry, predicateBlock, prepareBlock, postNoncePredicate.Condition, prepare,
            "The post-IncrementNonce success predicate must dominate PrepareSimpleTransferFastPath");
        RequireDominates(entry, prepareBlock, gasBlock, prepare, calculateAvailable,
            "PrepareSimpleTransferFastPath must dominate CalculateAvailableGas");
        RequireDominates(entry, gasBlock, simpleBlock, calculateAvailable, simple,
            "CalculateAvailableGas must dominate ExecuteSimpleTransfer");
        RequireDominates(entry, gasBlock, evmBlock, calculateAvailable, evm,
            "CalculateAvailableGas must dominate ExecuteEvmTransaction");

        NormalExit[] preGasExits = EnumerateNormalExitsBefore(predicateBlock, gasBlock, predicateFailureBody).ToArray();
        if (preGasExits.Any(static exit => !exit.PassedAllowedBoundary))
        {
            int[] unclosed = preGasExits
                .Where(static exit => !exit.PassedAllowedBoundary)
                .Select(static exit => exit.Block.Ordinal)
                .Distinct()
                .OrderBy(static ordinal => ordinal)
                .ToArray();
            throw new ExtractionException("The post-IncrementNonce success edge has normal exits before CalculateAvailableGas: " +
                string.Join(", ", unclosed) + ".");
        }

        BasicBlock dispatchCondition = RequireBlock(graph, simpleDispatchIf.Condition, "simple/EVM dispatch condition");
        ReturnStatementSyntax gasFailureReturn = gasFailureIf.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>()
            .SingleOrDefault(static statement => statement.Expression is IdentifierNameSyntax { Identifier.ValueText: "result" })
            ?? throw new ExtractionException("The CalculateAvailableGas failure guard has no admitted return result body.");
        BasicBlock gasFailureBody = RequireBlock(graph, gasFailureReturn, "CalculateAvailableGas failure branch");
        if (gasBlock.BranchValue is null ||
            gasBlock.ConditionKind is not (ControlFlowConditionKind.WhenTrue or ControlFlowConditionKind.WhenFalse) ||
            gasBlock.ConditionalSuccessor?.Destination is not BasicBlock gasConditionalDestination ||
            gasBlock.FallThroughSuccessor?.Destination is not BasicBlock gasFallThroughDestination)
        {
            throw new ExtractionException("The CalculateAvailableGas failure guard has no complete success/failure CFG split.");
        }

        BasicBlock gasTrue = gasBlock.ConditionKind == ControlFlowConditionKind.WhenTrue
            ? gasConditionalDestination
            : gasFallThroughDestination;
        BasicBlock gasFalse = gasBlock.ConditionKind == ControlFlowConditionKind.WhenTrue
            ? gasFallThroughDestination
            : gasConditionalDestination;
        if (!CanReach(gasTrue, gasFailureBody) || CanReach(gasFalse, gasFailureBody))
        {
            throw new ExtractionException("The CalculateAvailableGas CFG no longer binds the true failure edge and false dispatch edge.");
        }

        NormalExit[] preDispatchExits = EnumerateNormalExitsBefore(gasBlock, dispatchCondition, gasFailureBody).ToArray();
        if (preDispatchExits.Any(static exit => !exit.PassedAllowedBoundary))
        {
            int[] unclosed = preDispatchExits
                .Where(static exit => !exit.PassedAllowedBoundary)
                .Select(static exit => exit.Block.Ordinal)
                .Distinct()
                .OrderBy(static ordinal => ordinal)
                .ToArray();
            throw new ExtractionException("The post-IncrementNonce prefix has normal exits before dispatch outside the admitted gas-failure branch: " +
                string.Join(", ", unclosed) + ".");
        }

        BasicBlock[] dispatchSuccessors = Successors(dispatchCondition).Where(static block => block.IsReachable).ToArray();
        if (dispatchCondition.BranchValue is null ||
            dispatchCondition.ConditionKind is not (ControlFlowConditionKind.WhenTrue or ControlFlowConditionKind.WhenFalse) ||
            dispatchSuccessors.Length != 2 ||
            dispatchSuccessors.Any(successor => CanReach(successor, simpleBlock) == CanReach(successor, evmBlock)) ||
            dispatchSuccessors.Count(successor => CanReach(successor, simpleBlock)) != 1 ||
            dispatchSuccessors.Count(successor => CanReach(successor, evmBlock)) != 1)
        {
            throw new ExtractionException("The simple/EVM dispatch condition no longer separates exactly one reachable branch for each typed handoff.");
        }

        BasicBlock dispatchTrue = dispatchCondition.ConditionKind == ControlFlowConditionKind.WhenTrue
            ? dispatchCondition.ConditionalSuccessor!.Destination as BasicBlock
            ?? throw new ExtractionException("The simple/EVM dispatch condition has no true branch.")
            : dispatchCondition.FallThroughSuccessor!.Destination as BasicBlock
            ?? throw new ExtractionException("The simple/EVM dispatch condition has no true branch.");
        BasicBlock dispatchFalse = dispatchCondition.ConditionKind == ControlFlowConditionKind.WhenTrue
            ? dispatchCondition.FallThroughSuccessor!.Destination as BasicBlock
            ?? throw new ExtractionException("The simple/EVM dispatch condition has no false branch.")
            : dispatchCondition.ConditionalSuccessor!.Destination as BasicBlock
            ?? throw new ExtractionException("The simple/EVM dispatch condition has no false branch.");
        if (!CanReach(dispatchTrue, simpleBlock) || CanReach(dispatchTrue, evmBlock) ||
            !CanReach(dispatchFalse, evmBlock) || CanReach(dispatchFalse, simpleBlock))
        {
            throw new ExtractionException("The simple/EVM dispatch CFG no longer binds the true edge to simple transfer and false edge to EVM.");
        }

        foreach (BasicBlock successor in dispatchSuccessors)
        {
            BasicBlock target = CanReach(successor, simpleBlock) ? simpleBlock : evmBlock;
            if (CanReachExitWithout(graph, successor, target))
            {
                throw new ExtractionException("A simple/EVM dispatch branch can exit before its typed handoff.");
            }
        }

        int[] closedNormalExits = preGasExits
            .Concat(preDispatchExits)
            .Select(static exit => exit.Block.Ordinal)
            .Distinct()
            .OrderBy(static ordinal => ordinal)
            .ToArray();
        return new(
            ExpectedPostNonceSuccessPredicate,
            predicateBlock.Ordinal,
            successEdge.Ordinal,
            gasBlock.Ordinal,
            dispatchCondition.Ordinal,
            closedNormalExits,
            predicateBinding);
    }

    private static void RequireDominates(
        BasicBlock entry,
        BasicBlock source,
        BasicBlock target,
        SyntaxNode sourceSyntax,
        SyntaxNode targetSyntax,
        string description)
    {
        if (ReferenceEquals(source, target))
        {
            if (sourceSyntax.SpanStart >= targetSyntax.SpanStart)
            {
                throw new ExtractionException(description + " in the same CFG block has the wrong source order.");
            }

            return;
        }

        if (CanReachWithout(entry, target, source))
        {
            throw new ExtractionException(description + ".");
        }
    }

    private static SemanticLowering BuildSemanticLowering(
        SourceFile processor,
        MethodDeclarationSyntax prepare,
        MethodDeclarationSyntax candidateHelper,
        MethodDeclarationSyntax emptyHelper,
        MethodDeclarationSyntax execute,
        MethodDeclarationSyntax calculateAvailable,
        MethodDeclarationSyntax simpleHandoff,
        MethodDeclarationSyntax evmHandoff,
        AssignmentExpressionSyntax nullCode,
        AssignmentExpressionSyntax nullDelegation,
        VariableDeclaratorSyntax recipient,
        IfStatementSyntax candidateIf,
        InvocationExpressionSyntax lookupCall,
        BinaryExpressionSyntax simplePredicate,
        InvocationExpressionSyntax commitCall,
        InvocationExpressionSyntax availableCall,
        IfStatementSyntax gasFailureIf,
        InvocationExpressionSyntax simpleCall,
        InvocationExpressionSyntax evmCall,
        PropertyDeclarationSyntax to,
        PropertyDeclarationSyntax authorization,
        SyntaxNode[] gasFields,
        SourceFile transaction,
        SourceFile ethereumGasPolicy,
        OptionShape optionShape,
        GasClassification gasClassification,
        SemanticContext context)
    {
        SourceBinding prepareBinding = Bind(processor, "TransactionProcessorBase<TGasPolicy>", "PrepareSimpleTransferFastPath", prepare, context);

        WidthShape[] widths =
        [
            new("uint64", NumericWidth.UInt64, 64, Canonical(RequireProperty(transaction, "Transaction", "GasLimit")),
                Bind(transaction, "Transaction", "GasLimit", RequireProperty(transaction, "Transaction", "GasLimit"), context)),
            new("uint256", NumericWidth.UInt256, 256, Canonical(RequireProperty(transaction, "Transaction", "Value")),
                Bind(transaction, "Transaction", "Value", RequireProperty(transaction, "Transaction", "Value"), context)),
            new("executionOptions", NumericWidth.Int32, 32, optionShape.Binding.CanonicalSyntax, optionShape.Binding),
        ];

        SemanticOperation[] operations =
        [
            Operation("restoreOption", 1, SemanticFormula.OptionHasFlag, [NumericWidth.Int32], NumericWidth.Int32,
                RequireInitializer(RequireLocal(execute, "restore")), ["opts", "ExecutionOptions.Restore"],
                Bind(processor, "TransactionProcessorBase<TGasPolicy>", "restoreOption", RequireInitializer(RequireLocal(execute, "restore")), context)),
            Operation("commitOption", 2, SemanticFormula.EffectiveCommit, [NumericWidth.Int32], NumericWidth.Int32,
                RequireInitializer(RequireLocal(execute, "commit")), ["opts", "ExecutionOptions.Commit", "ExecutionOptions.SkipValidation", "spec.IsEip658Enabled"],
                Bind(processor, "TransactionProcessorBase<TGasPolicy>", "commitOption", RequireInitializer(RequireLocal(execute, "commit")), context)),
            Operation("prepareFastPath", 3, SemanticFormula.PrepareFastPath, [], NumericWidth.Int32,
                prepare, ["preloadedCodeInfo", "preloadedDelegationAddress", "recipient"], prepareBinding),
            Operation("candidateGuard", 4, SemanticFormula.CandidateGuard, [], NumericWidth.Int32,
                candidateIf.Condition, ["recipient", "_isCodeOverridable", "tx.AuthorizationList", "ForceSimpleTransferDisabled"],
                Bind(processor, "TransactionProcessorBase<TGasPolicy>", "candidateGuard", candidateIf.Condition, context)),
            Operation("codeLookup", 5, SemanticFormula.CodeLookup, [], NumericWidth.Int32,
                lookupCall, ["recipient", "!spec.IsEip8037Enabled", "spec", "out delegation"],
                Bind(processor, "TransactionProcessorBase<TGasPolicy>", "codeLookup", lookupCall, context)),
            Operation("simpleDecision", 6, SemanticFormula.SimpleDecision, [], NumericWidth.Int32,
                simplePredicate, ["delegationAddress", "codeInfo.IsEmpty"],
                Bind(processor, "TransactionProcessorBase<TGasPolicy>", "simpleDecision", simplePredicate, context)),
            Operation("commitBeforeExecution", 7, SemanticFormula.CommitBeforeExecution, [NumericWidth.Int32], NumericWidth.Int32,
                RequireInitializer(RequireLocal(execute, "commitBeforeExecution")), ["commit", "simpleTransferRecipient", "restore", "tracer.IsTracingState"],
                Bind(processor, "TransactionProcessorBase<TGasPolicy>", "commitBeforeExecution", RequireInitializer(RequireLocal(execute, "commitBeforeExecution")), context)),
            Operation("precommitRequest", 8, SemanticFormula.PrecommitRequest, [], NumericWidth.Int32,
                commitCall, ["WorldState", "spec", "tracer.IsTracingState ? tracer : NullTxTracer.Instance", "commitRoots:false"],
                Bind(processor, "TransactionProcessorBase<TGasPolicy>", "precommitRequest", commitCall, context)),
            Operation("calculateAvailableGas", 9, SemanticFormula.CalculateAvailableGas, [], NumericWidth.Int32,
                availableCall, ["tx.GasLimit", "intrinsicGas.Standard", "spec", "out gasAvailable"],
                Bind(processor, "TransactionProcessorBase<TGasPolicy>", "calculateAvailableGas", availableCall, context)),
            Operation("gasFailurePolicy", 10, SemanticFormula.GasFailurePolicy, [], NumericWidth.Int32,
                RequireAssignment(ethereumGasPolicy, "available", "default"),
                ["GasLimitBelowIntrinsicGas", "available.Value", "available.StateReservoir", "available.StateGasUsed", "available.StateGasSpill", "available.StateGasSpillRefunded"],
                Bind(ethereumGasPolicy, "EthereumGasPolicy", "gasFailurePolicy", RequireAssignment(ethereumGasPolicy, "available", "default"), context)),
            Operation("simpleHandoff", 11, SemanticFormula.SimpleHandoff, [], NumericWidth.Int32,
                simpleCall, ["tx", "header", "spec", "tracer", "opts", "restore", "commit", "deleteCallerAccount", "recipient", "intrinsic", "gasAvailable", "opcodeGasPrice", "premium", "reserved", "blobBaseFee"],
                Bind(processor, "TransactionProcessorBase<TGasPolicy>", "simpleHandoff", simpleCall, context)),
            Operation("evmHandoff", 12, SemanticFormula.EvmHandoff, [], NumericWidth.Int32,
                evmCall, ["tx", "header", "spec", "tracer", "opts", "restore", "commit", "deleteCallerAccount", "intrinsic", "gasAvailable", "opcodeGasPrice", "premium", "reserved", "blobBaseFee", "preloadedCodeInfo", "preloadedDelegationAddress"],
                Bind(processor, "TransactionProcessorBase<TGasPolicy>", "evmHandoff", evmCall, context)),
        ];

        // Keep the exact output initializers and the two source projections in the semantic IR.
        // They are deliberately represented as effects rather than inferred from a field list.
        _ = nullCode;
        _ = nullDelegation;
        _ = recipient;
        _ = to;
        _ = authorization;
        _ = gasFields;
        SourceBinding lookupBinding = Bind(processor, "TransactionProcessorBase<TGasPolicy>", "codeLookup", lookupCall, context);
        SourceBinding commitBinding = Bind(processor, "TransactionProcessorBase<TGasPolicy>", "precommitRequest", commitCall, context);
        SourceBinding gasFailureBinding = Bind(ethereumGasPolicy, "EthereumGasPolicy", "gasFailurePolicy",
            RequireAssignment(ethereumGasPolicy, "available", "default"), context);
        SourceBinding domainCandidateBinding = Bind(processor, "TransactionProcessorBase<TGasPolicy>", "codeLookup.domain", candidateIf.Condition, context);
        SourceBinding domainCommitBinding = Bind(processor, "TransactionProcessorBase<TGasPolicy>", "precommitRequest.domain",
            RequireInitializer(RequireLocal(execute, "commitBeforeExecution")), context);
        SourceBinding domainGasBinding = Bind(processor, "TransactionProcessorBase<TGasPolicy>", "gasFailurePolicy.domain", gasFailureIf.Condition, context);

        return new SemanticLowering(widths, operations,
        [
            new("codeRepositoryResult", CodeRepositoryInterfacePath, "ICodeInfoRepository.GetCachedCodeInfo",
                "CodeInfo + out Address? delegationAddress", "lookup returns one coherent code/delegation observation; EIP-8037 does not resolve the target code in this stage.",
                "recipient != null && candidate", [lookupBinding],
                [LowerSourceSyntax(lookupCall)], LowerSourceSyntax(candidateIf.Condition), domainCandidateBinding),
            new("worldStateCommit", WorldStateInterfacePath, "IWorldState.Commit",
                "effectful request with spec, chosen tracer, commitRoots:false", "the request may escape; successful completion permits gas initialization.",
                "commitBeforeExecution", [commitBinding], [LowerSourceSyntax(commitCall)],
                LowerSourceSyntax(RequireInitializer(RequireLocal(execute, "commitBeforeExecution"))), domainCommitBinding),
            new("gasPolicyOut", EthereumGasPolicyPath, "EthereumGasPolicy.TryCreateAvailableFromIntrinsic",
                "five-field EthereumGasPolicy output", "on intrinsic-gas rejection the source assigns default, so every five field is zero and no dispatch is reached.",
                "gas initialization returns false", [
                    gasFailureBinding,
                    ..gasFields.Select(field => Bind(ethereumGasPolicy, "EthereumGasPolicy", "gasField", field, context)),
                ], [
                    LowerSourceSyntax(RequireAssignment(ethereumGasPolicy, "available", "default")),
                    ..gasFields.Select(LowerSourceSyntax),
                ], LowerSourceSyntax(gasFailureIf.Condition), domainGasBinding),
        ], gasClassification);
    }

    private static SemanticOperation Operation(
        string id,
        int ordinal,
        SemanticFormula formula,
        NumericWidth[] inputWidths,
        NumericWidth outputWidth,
        SyntaxNode evidence,
        string[] sourceOperands,
        SourceBinding binding)
    {
        SourceExpression expressionAst = LowerSourceSyntax(evidence);
        if (!MatchesFormulaGrammar(formula, expressionAst))
        {
            throw new ExtractionException($"Semantic operation {id} is outside its admitted source expression grammar.");
        }

        return new(id, ordinal, formula, inputWidths, outputWidth, Canonical(evidence), sourceOperands, binding, expressionAst);
    }

    private static ClassDeclarationSyntax RequireClass(SourceFile source, string name)
    {
        ClassDeclarationSyntax[] matches = source.Root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Where(candidate => candidate.Identifier.ValueText == name).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ExtractionException($"Missing class {name} in {source.RelativePath}."),
            _ => throw new ExtractionException($"Expected exactly one class {name} in {source.RelativePath}."),
        };
    }

    private static MethodDeclarationSyntax RequireExecuteOverload(SourceFile source)
    {
        MethodDeclarationSyntax[] matches = source.Root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == "Execute" && method.ParameterList.Parameters.Count == 6 &&
                OwnerName(method) == "TransactionProcessorBase" &&
                Canonical(method.ParameterList).Contains("inIntrinsicGas<TGasPolicy>intrinsicGas", StringComparison.Ordinal))
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ExtractionException("Missing admitted six-parameter TransactionProcessorBase.Execute overload."),
            _ => throw new ExtractionException("Expected exactly one admitted six-parameter Execute overload."),
        };
    }

    private static MethodDeclarationSyntax RequireMethod(SourceFile source, string owner, string name, int parameterCount, string signature)
    {
        string expectedParameters = ParameterShape(signature);
        MethodDeclarationSyntax[] matches = source.Root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == name && method.ParameterList.Parameters.Count == parameterCount &&
                OwnerName(method) == OwnerName(owner) && Canonical(method.ParameterList) == expectedParameters)
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ExtractionException($"Missing admitted {owner}.{name}{expectedParameters} in {source.RelativePath}."),
            _ => throw new ExtractionException($"Expected exactly one admitted {owner}.{name}{expectedParameters} in {source.RelativePath}."),
        };
    }

    private static PropertyDeclarationSyntax RequireProperty(SourceFile source, string owner, string name)
    {
        PropertyDeclarationSyntax[] matches = source.Root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .Where(property => property.Identifier.ValueText == name && OwnerName(property) == OwnerName(owner)).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ExtractionException($"Missing admitted {owner}.{name} in {source.RelativePath}."),
            _ => throw new ExtractionException($"Expected exactly one admitted {owner}.{name} in {source.RelativePath}."),
        };
    }

    private static VariableDeclaratorSyntax RequireField(SourceFile source, string owner, string name)
    {
        VariableDeclaratorSyntax[] matches = source.Root.DescendantNodes().OfType<FieldDeclarationSyntax>()
            .Where(field => OwnerName(field) == OwnerName(owner))
            .SelectMany(field => field.Declaration.Variables)
            .Where(variable => variable.Identifier.ValueText == name)
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ExtractionException($"Missing admitted field {owner}.{name} in {source.RelativePath}."),
            _ => throw new ExtractionException($"Expected exactly one admitted field {owner}.{name} in {source.RelativePath}."),
        };
    }

    private static EnumDeclarationSyntax RequireSingleEnum(SourceFile source, string name)
    {
        EnumDeclarationSyntax[] matches = source.Root.DescendantNodes().OfType<EnumDeclarationSyntax>()
            .Where(declaration => declaration.Identifier.ValueText == name).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ExtractionException($"Missing enum {name} in {source.RelativePath}."),
            _ => throw new ExtractionException($"Expected exactly one enum {name} in {source.RelativePath}."),
        };
    }

    private static int RequireEnumValue(EnumDeclarationSyntax declaration, string name, string expected)
    {
        EnumMemberDeclarationSyntax member = declaration.Members.SingleOrDefault(candidate => candidate.Identifier.ValueText == name)
            ?? throw new ExtractionException($"Missing ExecutionOptions.{name}.");
        if (member.EqualsValue is null || Canonical(member.EqualsValue.Value) != expected)
        {
            throw new ExtractionException($"ExecutionOptions.{name} changed from {expected}.");
        }

        return int.Parse(expected, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static VariableDeclaratorSyntax RequireLocal(MethodDeclarationSyntax method, string name)
    {
        VariableDeclaratorSyntax[] matches = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(variable => variable.Identifier.ValueText == name).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ExtractionException($"Missing local {name} in {method.Identifier.ValueText}."),
            _ => throw new ExtractionException($"Expected exactly one local {name} in {method.Identifier.ValueText}."),
        };
    }

    private static ExpressionSyntax RequireInitializer(VariableDeclaratorSyntax variable) =>
        variable.Initializer?.Value as ExpressionSyntax
        ?? throw new ExtractionException($"Local {variable.Identifier.ValueText} must have an initializer.");

    private static AssignmentExpressionSyntax RequireAssignment(SourceFile source, string left, string right)
    {
        AssignmentExpressionSyntax[] matches = source.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(assignment => Canonical(assignment.Left) == left && Canonical(assignment.Right) == right).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ExtractionException($"Missing exact assignment {left} = {right} in {source.RelativePath}."),
            _ => throw new ExtractionException($"Expected exactly one exact assignment {left} = {right} in {source.RelativePath}."),
        };
    }

    private static AssignmentExpressionSyntax RequireAssignment(MethodDeclarationSyntax method, string left, string right)
    {
        AssignmentExpressionSyntax[] matches = method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(assignment => Canonical(assignment.Left) == left && Canonical(assignment.Right) == right).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ExtractionException($"Missing exact assignment {left} = {right} in {method.Identifier.ValueText}."),
            _ => throw new ExtractionException($"Expected exactly one exact assignment {left} = {right} in {method.Identifier.ValueText}."),
        };
    }

    private static IfStatementSyntax RequireIf(MethodDeclarationSyntax method, string expectedCondition)
    {
        IfStatementSyntax[] matches = method.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(candidate => Canonical(candidate.Condition) == expectedCondition).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ExtractionException($"Missing exact if condition {expectedCondition} in {method.Identifier.ValueText}."),
            _ => throw new ExtractionException($"Expected exactly one exact if condition {expectedCondition} in {method.Identifier.ValueText}."),
        };
    }

    private static IfStatementSyntax RequireIfContaining(MethodDeclarationSyntax method, InvocationExpressionSyntax invocation)
    {
        IfStatementSyntax[] matches = invocation.Ancestors().OfType<IfStatementSyntax>().
            Where(candidate => candidate.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() == method).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ExtractionException($"The invocation {InvocationName(invocation)} is not guarded by an admitted if in {method.Identifier.ValueText}."),
            _ => matches.OrderBy(candidate => candidate.Span.Length).First(),
        };
    }

    private static InvocationExpressionSyntax RequireDirectInvocation(
        MethodDeclarationSyntax method,
        string name,
        string receiver,
        string arguments)
    {
        InvocationExpressionSyntax[] matches = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(invocation => InvocationName(invocation) == name && InvocationReceiver(invocation) == receiver &&
                Canonical(invocation.ArgumentList) == "(" + arguments + ")" &&
                invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() == method &&
                invocation.Ancestors().All(ancestor => ancestor is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax))
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ExtractionException($"Missing direct {name} invocation in {method.Identifier.ValueText}."),
            _ => throw new ExtractionException($"Expected exactly one direct {name} invocation in {method.Identifier.ValueText}."),
        };
    }

    private static InvocationExpressionSyntax RequireInvocation(
        MethodDeclarationSyntax method,
        string name,
        string receiver,
        string arguments)
    {
        InvocationExpressionSyntax[] matches = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(invocation => InvocationName(invocation) == name &&
                (receiver.Length == 0 || InvocationReceiver(invocation) == receiver) &&
                Canonical(invocation.ArgumentList) == "(" + arguments + ")" &&
                invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() == method)
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ExtractionException($"Missing admitted {name} invocation in {method.Identifier.ValueText}."),
            _ => throw new ExtractionException($"Expected exactly one admitted {name} invocation in {method.Identifier.ValueText}."),
        };
    }

    private static InvocationExpressionSyntax RequireDispatchInvocation(MethodDeclarationSyntax method, string name, int argumentCount, string arguments) =>
        method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(invocation => InvocationName(invocation) == name && invocation.ArgumentList.Arguments.Count == argumentCount &&
                Canonical(invocation.ArgumentList) == "(" + arguments + ")" &&
                invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() == method)
            .ToArray() switch
        {
            [var invocation] => invocation,
            [] => throw new ExtractionException($"Missing typed {name} handoff in {method.Identifier.ValueText}."),
            _ => throw new ExtractionException($"Expected exactly one typed {name} handoff in {method.Identifier.ValueText}."),
        };

    private static ReturnStatementSyntax RequireReturnContaining(MethodDeclarationSyntax method, string invocationName)
    {
        ReturnStatementSyntax[] matches = method.DescendantNodes().OfType<ReturnStatementSyntax>()
            .Where(returnStatement => returnStatement.Expression is not null && FindInvocation(returnStatement.Expression, invocationName) is not null).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ExtractionException($"Missing return containing {invocationName} in {method.Identifier.ValueText}."),
            _ => throw new ExtractionException($"Expected exactly one return containing {invocationName} in {method.Identifier.ValueText}."),
        };
    }

    private static BinaryExpressionSyntax RequireSimplePredicate(MethodDeclarationSyntax method)
    {
        BinaryExpressionSyntax[] matches = method.DescendantNodes().OfType<BinaryExpressionSyntax>()
            .Where(binary => binary.IsKind(SyntaxKind.LogicalAndExpression) &&
                Canonical(binary.Left) == "delegationAddressisnull" && Canonical(binary.Right) == "codeInfo.IsEmpty").ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ExtractionException("HasNoExecutableCode no longer has delegation-first short-circuit semantics."),
            _ => throw new ExtractionException("Expected exactly one HasNoExecutableCode predicate."),
        };
    }

    private static void ValidateRestoreFormula(
        ExpressionSyntax expression,
        SourceBinding binding,
        SourceFile processor,
        SemanticContext context)
    {
        if (Canonical(expression) != ExpectedRestoreFormula || binding.CanonicalSyntax != ExpectedRestoreFormula ||
            expression is not InvocationExpressionSyntax invocation)
        {
            throw new ExtractionException("Restore must retain the exact HasFlag(ExecutionOptions.Restore) initializer.");
        }

        ValidateHasFlagInvocation(processor, invocation, "ExecutionOptions.Restore", "restore", context);
        RequireInvocationBinding(binding, "HasFlag", "opts");
    }

    private static void ValidateCommitFormula(
        ExpressionSyntax expression,
        SourceBinding binding,
        SourceFile processor,
        SemanticContext context)
    {
        if (Canonical(expression) != ExpectedCommitFormula || binding.CanonicalSyntax != ExpectedCommitFormula)
        {
            throw new ExtractionException("Commit must retain the exact effective-commit initializer AST.");
        }

        if (expression is not BinaryExpressionSyntax root || !root.IsKind(SyntaxKind.LogicalOrExpression))
        {
            throw new ExtractionException("Effective commit must retain its logical-or root.");
        }

        if (root.Left is not InvocationExpressionSyntax commitInvocation ||
            root.Right is not ParenthesizedExpressionSyntax parenthesized ||
            parenthesized.Expression is not BinaryExpressionSyntax conjunction ||
            !conjunction.IsKind(SyntaxKind.LogicalAndExpression) ||
            conjunction.Left is not PrefixUnaryExpressionSyntax commitSkipNegation ||
            conjunction.Right is not PrefixUnaryExpressionSyntax eipNegation)
        {
            throw new ExtractionException("Commit must retain its exact effective-commit initializer AST.");
        }

        ValidateHasFlagInvocation(processor, commitInvocation, "ExecutionOptions.Commit", "commit", context);
        ExpressionSyntax skipExpression = RequireLogicalNegation(commitSkipNegation, "SkipValidation");
        if (skipExpression is not InvocationExpressionSyntax skipInvocation)
        {
            throw new ExtractionException("Effective commit must negate HasFlag(ExecutionOptions.SkipValidation).");
        }

        ValidateHasFlagInvocation(processor, skipInvocation, "ExecutionOptions.SkipValidation", "skip-validation", context);
        ExpressionSyntax eipExpression = RequireLogicalNegation(eipNegation, "IsEip658Enabled");
        if (eipExpression is not MemberAccessExpressionSyntax eipMember || Canonical(eipMember) != "spec.IsEip658Enabled")
        {
            throw new ExtractionException("Effective commit must negate spec.IsEip658Enabled.");
        }

        SourceBinding eipBinding = Bind(processor, "TransactionProcessorBase<TGasPolicy>", "commit.eip658", eipMember, context);
        if (eipBinding.NodeKind != nameof(SyntaxKind.SimpleMemberAccessExpression) ||
            !eipBinding.TargetSymbol.EndsWith(".IsEip658Enabled", StringComparison.Ordinal))
        {
            throw new ExtractionException("Effective commit no longer binds spec.IsEip658Enabled to the release-spec property.");
        }

        RequireBindingShape(binding, nameof(SyntaxKind.LogicalOrExpression), ExpectedCommitFormula, "effective commit");
    }

    private static ExpressionSyntax RequireLogicalNegation(PrefixUnaryExpressionSyntax expression, string operand)
    {
        if (!expression.IsKind(SyntaxKind.LogicalNotExpression))
        {
            throw new ExtractionException($"The effective-commit formula must logically negate {operand}.");
        }

        return expression.Operand;
    }

    private static void ValidateHasFlagInvocation(
        SourceFile processor,
        InvocationExpressionSyntax invocation,
        string expectedFlag,
        string bindingMember,
        SemanticContext context)
    {
        if (InvocationName(invocation) != "HasFlag" || InvocationReceiver(invocation) != "opts" ||
            invocation.ArgumentList.Arguments.Count != 1 ||
            Canonical(invocation.ArgumentList.Arguments[0].Expression) != expectedFlag)
        {
            throw new ExtractionException($"The {bindingMember} initializer no longer binds opts.HasFlag({expectedFlag}).");
        }

        SourceBinding invocationBinding = Bind(processor, "TransactionProcessorBase<TGasPolicy>", bindingMember, invocation, context);
        RequireInvocationBinding(invocationBinding, "HasFlag", "opts");
        if (!invocationBinding.TargetSymbol.Contains("System.Enum.HasFlag(System.Enum)", StringComparison.Ordinal))
        {
            throw new ExtractionException($"The {bindingMember} initializer resolved a different HasFlag overload.");
        }
        ExpressionSyntax flagExpression = invocation.ArgumentList.Arguments[0].Expression;
        SourceBinding flagBinding = Bind(processor, "TransactionProcessorBase<TGasPolicy>", bindingMember + ".flag", flagExpression, context);
        if (flagBinding.NodeKind != nameof(SyntaxKind.SimpleMemberAccessExpression) ||
            !flagBinding.TargetSymbol.EndsWith("." + expectedFlag[(expectedFlag.LastIndexOf('.') + 1)..], StringComparison.Ordinal))
        {
            throw new ExtractionException($"The {bindingMember} initializer no longer binds the admitted execution-option member.");
        }
    }

    private static GasClassification ValidateGasClassification(
        SourceFile processor,
        MethodDeclarationSyntax calculateAvailable,
        SemanticContext context)
    {
        if (BodyOrExpression(calculateAvailable) is not ConditionalExpressionSyntax conditional ||
            conditional.Condition is not InvocationExpressionSyntax condition ||
            Canonical(condition) != ExpectedGasCondition ||
            Canonical(conditional.WhenTrue) != ExpectedGasSuccessResult ||
            Canonical(conditional.WhenFalse) != ExpectedGasFailureResult)
        {
            throw new ExtractionException("CalculateAvailableGas must retain its exact source-bound condition and TransactionResult arms.");
        }

        if (InvocationName(condition) != "TryCreateAvailableFromIntrinsic" || InvocationReceiver(condition) != "TGasPolicy" ||
            Canonical(condition.ArgumentList) != "(tx.GasLimit,intrinsicGas.Standard,spec,outgasAvailable)")
        {
            throw new ExtractionException("CalculateAvailableGas no longer calls TryCreateAvailableFromIntrinsic with its admitted arguments.");
        }

        SourceBinding conditionBinding = Bind(processor, "TransactionProcessorBase<TGasPolicy>", "CalculateAvailableGas.condition", condition, context);
        RequireInvocationBinding(conditionBinding, "TryCreateAvailableFromIntrinsic", "TGasPolicy");
        SourceBinding successBinding = Bind(processor, "TransactionProcessorBase<TGasPolicy>", "CalculateAvailableGas.success", conditional.WhenTrue, context);
        SourceBinding failureBinding = Bind(processor, "TransactionProcessorBase<TGasPolicy>", "CalculateAvailableGas.failure", conditional.WhenFalse, context);
        RequireTransactionResultBinding(successBinding, "Ok");
        RequireTransactionResultBinding(failureBinding, "GasLimitBelowIntrinsicGas");
        return new(ExpectedGasCondition, ExpectedGasSuccessResult, ExpectedGasFailureResult, conditionBinding, successBinding, failureBinding,
            LowerSourceSyntax(condition), LowerSourceSyntax(conditional.WhenTrue), LowerSourceSyntax(conditional.WhenFalse));
    }

    private static void RequireTransactionResultBinding(SourceBinding binding, string member)
    {
        if (binding.NodeKind != nameof(SyntaxKind.SimpleMemberAccessExpression) || binding.CanonicalSyntax != "TransactionResult." + member ||
            !binding.TargetSymbol.EndsWith("TransactionResult." + member, StringComparison.Ordinal))
        {
            throw new ExtractionException($"CalculateAvailableGas no longer binds TransactionResult.{member} as an exact result arm.");
        }
    }

    private static void RequireBindingShape(SourceBinding binding, string nodeKind, string canonical, string description)
    {
        if (binding.NodeKind != nodeKind || binding.CanonicalSyntax != canonical)
        {
            throw new ExtractionException($"The source binding for {description} no longer records its exact syntax node.");
        }
    }

    private static InvocationExpressionSyntax? FindInvocation(SyntaxNode node, string name) =>
        node.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
            .SingleOrDefault(invocation => InvocationName(invocation) == name);

    private static InvocationExpressionSyntax? FindInvocation(ExpressionSyntax node, string name) => FindInvocation((SyntaxNode)node, name);

    private static InvocationExpressionSyntax RequireRegistrationInvocation(SourceFile source, MethodDeclarationSyntax load, string canonicalTypeArguments)
    {
        InvocationExpressionSyntax[] matches = load.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(invocation => InvocationName(invocation) == "AddScoped" &&
                HasTypeArguments(invocation, canonicalTypeArguments) &&
                invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() == load &&
                IsBuilderRegistrationChain(invocation, "builder")).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ExtractionException($"Missing AddScoped<{canonicalTypeArguments}> registration in {source.RelativePath}."),
            _ => throw new ExtractionException($"Expected exactly one AddScoped<{canonicalTypeArguments}> registration in {source.RelativePath}."),
        };
    }

    private static bool IsBuilderRegistrationChain(InvocationExpressionSyntax invocation, string builderIdentifier)
    {
        ExpressionSyntax expression = invocation.Expression;
        while (expression is MemberAccessExpressionSyntax member)
        {
            expression = member.Expression;
            if (expression is InvocationExpressionSyntax nested)
            {
                expression = nested.Expression;
            }
        }

        return expression is IdentifierNameSyntax identifier && identifier.Identifier.ValueText == builderIdentifier;
    }

    private static bool HasTypeArguments(InvocationExpressionSyntax invocation, string canonicalTypeArguments)
    {
        GenericNameSyntax? name = invocation.Expression switch
        {
            GenericNameSyntax generic => generic,
            MemberAccessExpressionSyntax { Name: GenericNameSyntax generic } => generic,
            _ => null,
        };
        return name is not null && Canonical(name.TypeArgumentList) == "<" + canonicalTypeArguments + ">";
    }

    private static void RequireOrder(string label, params InvocationExpressionSyntax[] invocations)
    {
        for (int index = 1; index < invocations.Length; index++)
        {
            if (invocations[index - 1].SpanStart >= invocations[index].SpanStart)
            {
                throw new ExtractionException($"{label} changed order at {InvocationName(invocations[index - 1])} and {InvocationName(invocations[index])}.");
            }
        }
    }

    private static string InvocationName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        _ => string.Empty,
    };

    private static string InvocationReceiver(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => Canonical(member.Expression),
        _ => string.Empty,
    };

    private static SyntaxNode BodyOrExpression(MethodDeclarationSyntax method)
    {
        if (method.ExpressionBody is not null) return method.ExpressionBody.Expression;
        if (method.Body is not null) return method.Body;
        throw new ExtractionException($"{method.Identifier.ValueText} must have a body.");
    }

    private static string OwnerName(SyntaxNode node) =>
        node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText ?? string.Empty;

    private static string TypeIdentity(TypeDeclarationSyntax? type)
    {
        if (type is null)
        {
            return string.Empty;
        }

        string namespaceName = string.Join('.', type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse()
            .Select(static declaration => declaration.Name.ToString()));
        string[] containingTypes = type.Ancestors().OfType<TypeDeclarationSyntax>()
            .Reverse()
            .Append(type)
            .Select(static declaration => declaration.Identifier.ValueText +
                (declaration.TypeParameterList is null ? string.Empty : $"`{declaration.TypeParameterList.Parameters.Count}"))
            .ToArray();
        return namespaceName.Length == 0 ? string.Join('.', containingTypes) : namespaceName + "." + string.Join('.', containingTypes);
    }

    private static string OwnerName(string owner) => owner.Split('<')[0];

    private static string ParameterShape(string signature)
    {
        int open = signature.IndexOf('(');
        int close = signature.LastIndexOf(')');
        if (open < 0 && close < 0)
        {
            return "(" + signature + ")";
        }

        if (open < 0 || close < open)
        {
            throw new ExtractionException("An admitted method signature has no parameter list.");
        }

        return signature[open..(close + 1)];
    }

    private static BranchShape Branch(
        string id,
        int ordinal,
        string condition,
        SyntaxNode conditionEvidence,
        TerminalKind terminal,
        SemanticEffect[] effects,
        SourceBinding binding) =>
        new(id, ordinal, condition, terminal.ToString(), terminal, effects.Select(static effect => effect.ToString()).ToArray(), binding,
            LowerSourceSyntax(conditionEvidence));

    private static EffectShape Effect(
        string id,
        int ordinal,
        string kind,
        string[] reads,
        string[] writes,
        string failureVisibility,
        SemanticEffect effect,
        SourceBinding binding) =>
        new(id, ordinal, kind, reads, writes, failureVisibility, binding, effect);

    private static void RequireInvocationBinding(SourceBinding binding, string targetMember, string receiver)
    {
        if (binding.NodeKind != nameof(SyntaxKind.InvocationExpression) ||
            !binding.TargetSymbol.Contains("." + targetMember, StringComparison.Ordinal) ||
            receiver.Length != 0 && !binding.Receiver.Contains(receiver, StringComparison.Ordinal))
        {
            throw new ExtractionException($"The semantic binding for {targetMember} no longer resolves the admitted receiver/overload.");
        }
    }

    private static SourceBinding Bind(SourceFile source, string owner, string member, SyntaxNode node, SemanticContext context)
    {
        FileLinePositionSpan span = node.GetLocation().GetLineSpan();
        string canonical = Canonical(node);
        SemanticModel model = context.Model(source.Tree);
        IOperation? operation = model.GetOperation(node);
        SymbolInfo symbolInfo = model.GetSymbolInfo(node);
        if (symbolInfo.CandidateReason != CandidateReason.None || symbolInfo.CandidateSymbols.Length != 0)
        {
            throw new ExtractionException($"The source binding for {member} is ambiguous or only a candidate symbol: {canonical}.");
        }
        if (operation is IInvalidOperation)
        {
            throw new ExtractionException($"The source binding for {member} resolved to an invalid operation: {canonical}.");
        }
        ISymbol? symbol = operation switch
        {
            IInvocationOperation invocation => invocation.TargetMethod,
            IPropertyReferenceOperation property => property.Property,
            IFieldReferenceOperation field => field.Field,
            IMethodReferenceOperation methodReference => methodReference.Method,
            _ => model.GetSymbolInfo(node).Symbol,
        };
        symbol ??= node switch
        {
            MethodDeclarationSyntax method => model.GetDeclaredSymbol(method),
            PropertyDeclarationSyntax property => model.GetDeclaredSymbol(property),
            EnumDeclarationSyntax declaration => model.GetDeclaredSymbol(declaration),
            ClassDeclarationSyntax declaration => model.GetDeclaredSymbol(declaration),
            InterfaceDeclarationSyntax declaration => model.GetDeclaredSymbol(declaration),
            VariableDeclaratorSyntax variable => model.GetDeclaredSymbol(variable),
            _ => null,
        };
        symbol ??= symbolInfo.Symbol;
        bool isErrorSymbol = symbol is not null && HasErrorSymbol(symbol);
        if (isErrorSymbol)
        {
            throw new ExtractionException($"The source binding for {member} resolved through an error type: {canonical}.");
        }
        if ((operation is IInvocationOperation or IPropertyReferenceOperation or IFieldReferenceOperation or IMethodReferenceOperation) && symbol is null)
        {
            throw new ExtractionException($"The source binding for {member} has no resolved target symbol: {canonical}.");
        }
        if (operation is null && symbol is null)
        {
            throw new ExtractionException($"The source binding for {member} has no semantic operation or declaration symbol: {canonical}.");
        }
        string receiver = operation switch
        {
            IInvocationOperation invocation when invocation.Instance?.Type is ITypeSymbol type =>
                InvocationReceiver(node as InvocationExpressionSyntax ?? throw new ExtractionException("Invocation syntax mismatch")) + " :: " + type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            IPropertyReferenceOperation property when property.Instance?.Type is ITypeSymbol type =>
                type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            _ => node is InvocationExpressionSyntax invocation ? InvocationReceiver(invocation) : string.Empty,
        };
        DataFlowInfo dataFlow = AnalyzeDataFlow(model, node);
        ControlFlowInfo flow = context.Flow(node);
        return new SourceBinding(
            source.RelativePath,
            owner,
            member,
            SignatureOf(node),
            node.Kind().ToString(),
            canonical,
            TokenFingerprint(node),
            Sha256(Encoding.UTF8.GetBytes(canonical)),
            ContainingMember(node),
            receiver,
            symbol?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? string.Empty,
            symbol?.Kind.ToString() ?? string.Empty,
            symbolInfo.CandidateReason.ToString(),
            isErrorSymbol,
            symbolInfo.CandidateSymbols.Length != 0,
            StatementOrdinal(node),
            ControlFlowPath(node),
            flow.Block,
            node.NormalizeWhitespace().ToFullString(),
            operation?.Kind.ToString() ?? "Syntax:" + node.Kind(),
            operation?.Type?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? string.Empty,
            operation?.IsImplicit ?? false,
            dataFlow.Succeeded,
            dataFlow.ReadInside,
            dataFlow.WrittenInside,
            dataFlow.ReadOutside,
            dataFlow.WrittenOutside,
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            span.EndLinePosition.Line + 1,
            span.EndLinePosition.Character + 1);
    }

    private static DataFlowInfo AnalyzeDataFlow(SemanticModel model, SyntaxNode node)
    {
        try
        {
            DataFlowAnalysis analysis = model.AnalyzeDataFlow(node);
            return new(
                analysis.Succeeded,
                SymbolNames(analysis.ReadInside),
                SymbolNames(analysis.WrittenInside),
                SymbolNames(analysis.ReadOutside),
                SymbolNames(analysis.WrittenOutside));
        }
        catch (ArgumentException)
        {
            return DataFlowInfo.Empty;
        }
        catch (InvalidOperationException)
        {
            return DataFlowInfo.Empty;
        }
        catch (NotSupportedException)
        {
            return DataFlowInfo.Empty;
        }
    }

    private static string[] SymbolNames(IEnumerable<ISymbol> symbols) => symbols
        .Select(static symbol => symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(static symbol => symbol, StringComparer.Ordinal)
        .ToArray();

    private static bool HasErrorSymbol(ISymbol symbol)
    {
        if (symbol is ITypeSymbol type && HasErrorType(type))
        {
            return true;
        }

        if (symbol is IMethodSymbol method &&
            (HasErrorType(method.ReturnType) || method.Parameters.Any(parameter => HasErrorType(parameter.Type)) ||
             method.TypeParameters.Any(parameter => parameter.ConstraintTypes.Any(HasErrorType))))
        {
            return true;
        }

        if (symbol is IPropertySymbol property && HasErrorType(property.Type) ||
            symbol is IFieldSymbol field && HasErrorType(field.Type) ||
            symbol is IEventSymbol @event && HasErrorType(@event.Type) ||
            symbol is IParameterSymbol parameter && HasErrorType(parameter.Type) ||
            symbol is ILocalSymbol local && HasErrorType(local.Type))
        {
            return true;
        }

        for (ISymbol? current = symbol; current is not null; current = current.ContainingSymbol)
        {
            if (current is IErrorTypeSymbol || current.Kind == SymbolKind.ErrorType ||
                current is IMethodSymbol methodSymbol && HasErrorType(methodSymbol.ContainingType) ||
                current is IPropertySymbol propertySymbol && HasErrorType(propertySymbol.ContainingType) ||
                current is IFieldSymbol fieldSymbol && HasErrorType(fieldSymbol.ContainingType))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasErrorType(ITypeSymbol type)
        => HasErrorType(type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default));

    private static bool HasErrorType(ITypeSymbol type, HashSet<ITypeSymbol> seen)
    {
        if (!seen.Add(type))
        {
            return false;
        }

        if (type is IErrorTypeSymbol || type.TypeKind == TypeKind.Error)
        {
            return true;
        }

        if (type is INamedTypeSymbol named &&
            (named.ContainingType is not null && HasErrorType(named.ContainingType, seen) || named.TypeArguments.Any(argument => HasErrorType(argument, seen))))
        {
            return true;
        }

        if (type is ITypeParameterSymbol typeParameter && typeParameter.ConstraintTypes.Any(constraint => HasErrorType(constraint, seen)))
        {
            return true;
        }

        return type switch
        {
            IArrayTypeSymbol array => HasErrorType(array.ElementType, seen),
            IPointerTypeSymbol pointer => HasErrorType(pointer.PointedAtType, seen),
            _ => false,
        };
    }

    /// <summary>Builds the finite source-expression tree consumed by the Lean emitter.</summary>
    /// <remarks>
    /// This is intentionally a closed grammar. A source node that is not represented here cannot
    /// silently become metadata while the emitter continues to execute an old term.
    /// </remarks>
    private static SourceExpression LowerSourceSyntax(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax method => SourceTree(SourceExpressionKind.Projection, method, method.Identifier.ValueText, []),
        PropertyDeclarationSyntax property => SourceTree(SourceExpressionKind.Projection, property, property.Identifier.ValueText, []),
        EnumDeclarationSyntax declaration => SourceTree(SourceExpressionKind.Projection, declaration, declaration.Identifier.ValueText, []),
        FieldDeclarationSyntax field when field.Declaration.Variables.Count == 1 =>
            SourceTree(SourceExpressionKind.Projection, field, field.Declaration.Variables[0].Identifier.ValueText, []),
        BlockSyntax block => SourceTree(SourceExpressionKind.Block, block, "block", block.Statements.Select(LowerSourceSyntax).ToArray()),
        LocalDeclarationStatementSyntax declaration => SourceTree(SourceExpressionKind.LocalDeclaration, declaration,
            "local", [LowerSourceSyntax(declaration.Declaration)]),
        VariableDeclarationSyntax declaration => SourceTree(SourceExpressionKind.LocalDeclaration, declaration,
            Canonical(declaration.Type), declaration.Variables.Select(LowerSourceSyntax).ToArray()),
        VariableDeclaratorSyntax declarator => SourceTree(SourceExpressionKind.VariableDeclarator, declarator,
            declarator.Identifier.ValueText, declarator.Initializer is null ? [] : [LowerSourceSyntax(declarator.Initializer.Value)]),
        IfStatementSyntax conditional => SourceTree(SourceExpressionKind.If, conditional, "if",
            conditional.Else is null
                ? [LowerSourceSyntax(conditional.Condition), LowerSourceSyntax(conditional.Statement)]
                : [LowerSourceSyntax(conditional.Condition), LowerSourceSyntax(conditional.Statement), LowerSourceSyntax(conditional.Else.Statement)]),
        ReturnStatementSyntax returned => SourceTree(SourceExpressionKind.Return, returned, "return",
            returned.Expression is null ? [] : [LowerSourceSyntax(returned.Expression)]),
        ExpressionStatementSyntax statement => SourceTree(SourceExpressionKind.ExpressionStatement, statement,
            "expression", [LowerSourceSyntax(statement.Expression)]),
        ArgumentSyntax argument => SourceTree(SourceExpressionKind.Argument, argument, ArgumentKind(argument),
            [LowerSourceSyntax(argument.Expression)]),
        IdentifierNameSyntax identifier => SourceTree(SourceExpressionKind.Identifier, identifier, identifier.Identifier.ValueText, []),
        PredefinedTypeSyntax type => SourceTree(SourceExpressionKind.Identifier, type, Canonical(type), []),
        ThisExpressionSyntax self => SourceTree(SourceExpressionKind.Identifier, self, "this", []),
        LiteralExpressionSyntax literal => SourceTree(SourceExpressionKind.Literal, literal, literal.Token.ValueText, []),
        DefaultExpressionSyntax defaultExpression => SourceTree(SourceExpressionKind.Default, defaultExpression, "default", []),
        MemberAccessExpressionSyntax access => SourceTree(SourceExpressionKind.MemberAccess, access,
            access.Name.Identifier.ValueText, [LowerSourceSyntax(access.Expression)]),
        QualifiedNameSyntax name => SourceTree(SourceExpressionKind.MemberAccess, name, name.Right.Identifier.ValueText,
            [LowerSourceSyntax(name.Left)]),
        GenericNameSyntax generic => SourceTree(SourceExpressionKind.Identifier, generic, generic.Identifier.ValueText, []),
        InvocationExpressionSyntax invocation => SourceTree(SourceExpressionKind.Invocation, invocation,
            InvocationName(invocation), [LowerSourceSyntax(invocation.Expression), .. invocation.ArgumentList.Arguments.Select(LowerSourceSyntax)]),
        PrefixUnaryExpressionSyntax unary => SourceTree(SourceExpressionKind.Unary, unary, unary.OperatorToken.ValueText,
            [LowerSourceSyntax(unary.Operand)]),
        PostfixUnaryExpressionSyntax unary => SourceTree(SourceExpressionKind.Unary, unary, unary.OperatorToken.ValueText,
            [LowerSourceSyntax(unary.Operand)]),
        BinaryExpressionSyntax binary => SourceTree(SourceExpressionKind.Binary, binary, binary.OperatorToken.ValueText,
            [LowerSourceSyntax(binary.Left), LowerSourceSyntax(binary.Right)]),
        ConditionalExpressionSyntax conditional => SourceTree(SourceExpressionKind.Conditional, conditional, "?:",
            [LowerSourceSyntax(conditional.Condition), LowerSourceSyntax(conditional.WhenTrue), LowerSourceSyntax(conditional.WhenFalse)]),
        IsPatternExpressionSyntax pattern => SourceTree(SourceExpressionKind.IsPattern, pattern, "is",
            [LowerSourceSyntax(pattern.Expression), LowerPattern(pattern.Pattern)]),
        AssignmentExpressionSyntax assignment => SourceTree(SourceExpressionKind.Assignment, assignment,
            assignment.OperatorToken.ValueText, [LowerSourceSyntax(assignment.Left), LowerSourceSyntax(assignment.Right)]),
        ParenthesizedExpressionSyntax parenthesized => SourceTree(SourceExpressionKind.Parenthesized, parenthesized, "()",
            [LowerSourceSyntax(parenthesized.Expression)]),
        ConstantPatternSyntax pattern => SourceTree(SourceExpressionKind.PatternConstant, pattern, "constant",
            [LowerSourceSyntax(pattern.Expression)]),
        UnaryPatternSyntax pattern => SourceTree(SourceExpressionKind.PatternUnary, pattern, pattern.OperatorToken.ValueText,
            [LowerPattern(pattern.Pattern)]),
        BinaryPatternSyntax pattern => SourceTree(SourceExpressionKind.PatternBinary, pattern, pattern.OperatorToken.ValueText,
            [LowerPattern(pattern.Left), LowerPattern(pattern.Right)]),
        DeclarationExpressionSyntax declaration => SourceTree(SourceExpressionKind.Projection, declaration,
            Canonical(declaration), []),
        SingleVariableDesignationSyntax designation => SourceTree(SourceExpressionKind.Projection, designation,
            designation.Identifier.ValueText, []),
        _ => throw new ExtractionException("The source contains syntax outside the admitted typed expression grammar: " +
            node.Kind() + " (" + Canonical(node) + ")."),
    };

    private static SourceExpression LowerPattern(PatternSyntax pattern) => pattern switch
    {
        ConstantPatternSyntax constant => LowerSourceSyntax(constant),
        UnaryPatternSyntax unary => LowerSourceSyntax(unary),
        BinaryPatternSyntax binary => LowerSourceSyntax(binary),
        _ => throw new ExtractionException("The source contains a pattern outside the admitted typed expression grammar: " +
            pattern.Kind() + " (" + Canonical(pattern) + ")."),
    };

    private static SourceExpression SourceTree(
        SourceExpressionKind kind,
        SyntaxNode node,
        string symbol,
        SourceExpression[] children)
    {
        if (string.IsNullOrWhiteSpace(symbol) || children.Any(static child => child is null))
        {
            throw new ExtractionException("The typed source-expression tree is incomplete.");
        }

        return new SourceExpression(kind, Canonical(node), symbol, GrammarIdentityTag(node, kind, symbol),
            GrammarTypeCategory(node, kind), children);
    }

    private static string ArgumentKind(ArgumentSyntax argument)
    {
        string name = argument.NameColon?.Name.Identifier.ValueText ?? argument.NameEquals?.Name.Identifier.ValueText ?? string.Empty;
        string refKind = string.IsNullOrEmpty(argument.RefKindKeyword.ValueText) ? "value" : argument.RefKindKeyword.ValueText;
        return name.Length == 0 ? refKind : name + ":" + refKind;
    }

    private static string GrammarIdentityTag(SyntaxNode node, SourceExpressionKind kind, string symbol) => node switch
    {
        IdentifierNameSyntax => "identifier:" + symbol,
        PredefinedTypeSyntax => "identifier:" + symbol,
        ThisExpressionSyntax => "identifier:this",
        LiteralExpressionSyntax => "literal:" + node.Kind(),
        MemberAccessExpressionSyntax access => "member:" + Canonical(access),
        QualifiedNameSyntax name => "member:" + Canonical(name),
        InvocationExpressionSyntax invocation => "invocation:" + Canonical(invocation.Expression) + "#" + InvocationName(invocation),
        PrefixUnaryExpressionSyntax unary => "operator:" + unary.Kind() + ":" + symbol,
        PostfixUnaryExpressionSyntax unary => "operator:" + unary.Kind() + ":" + symbol,
        BinaryExpressionSyntax binary => "operator:" + binary.Kind() + ":" + symbol,
        ConditionalExpressionSyntax => "operator:conditional:?:",
        IsPatternExpressionSyntax => "operator:is-pattern:is",
        AssignmentExpressionSyntax assignment => "operator:" + assignment.Kind() + ":" + symbol,
        ParenthesizedExpressionSyntax => "operator:parenthesized:()",
        ConstantPatternSyntax => "pattern:constant",
        UnaryPatternSyntax unary => "pattern:unary:" + symbol,
        BinaryPatternSyntax binary => "pattern:binary:" + symbol,
        ArgumentSyntax argument => "argument:" + ArgumentKind(argument),
        _ => kind + ":" + symbol,
    };

    private static string GrammarTypeCategory(SyntaxNode node, SourceExpressionKind kind) => node switch
    {
        BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalAndExpression) ||
            binary.IsKind(SyntaxKind.LogicalOrExpression) || binary.IsKind(SyntaxKind.EqualsExpression) ||
            binary.IsKind(SyntaxKind.NotEqualsExpression) || binary.IsKind(SyntaxKind.LessThanExpression) ||
            binary.IsKind(SyntaxKind.GreaterThanExpression) || binary.IsKind(SyntaxKind.LessThanOrEqualExpression) ||
            binary.IsKind(SyntaxKind.GreaterThanOrEqualExpression) => "bool",
        PrefixUnaryExpressionSyntax unary when unary.IsKind(SyntaxKind.LogicalNotExpression) => "bool",
        IsPatternExpressionSyntax => "bool",
        InvocationExpressionSyntax invocation => InvocationResultType(invocation),
        LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.TrueLiteralExpression) ||
            literal.IsKind(SyntaxKind.FalseLiteralExpression) => "bool",
        LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.NullLiteralExpression) => "null",
        LiteralExpressionSyntax => "literal",
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText switch
        {
            "IsEip658Enabled" or "IsTracingState" or "IsEmpty" => "bool",
            "Restore" or "Commit" or "SkipValidation" => "ExecutionOptions",
            _ => "member",
        },
        QualifiedNameSyntax => "member",
        AssignmentExpressionSyntax => "assignment",
        ArgumentSyntax => "argument",
        _ => kind switch
        {
            SourceExpressionKind.Identifier or SourceExpressionKind.MemberAccess => "projection",
            SourceExpressionKind.Default => "default",
            SourceExpressionKind.PatternConstant or SourceExpressionKind.PatternUnary or SourceExpressionKind.PatternBinary => "pattern",
            SourceExpressionKind.Block or SourceExpressionKind.If or SourceExpressionKind.Return or
                SourceExpressionKind.ExpressionStatement or SourceExpressionKind.LocalDeclaration or
                SourceExpressionKind.VariableDeclarator => "statement",
            _ => "syntax",
        },
    };

    private static string InvocationResultType(InvocationExpressionSyntax invocation) => InvocationName(invocation) switch
    {
        "HasFlag" or "TryCreateAvailableFromIntrinsic" or "IsSimpleTransferFastPathCandidate" or "HasNoExecutableCode" => "bool",
        "GetCachedCodeInfo" => "CodeInfo",
        "Commit" => "void",
        "ExecuteSimpleTransfer" or "ExecuteEvmTransaction" or "CalculateAvailableGas" => "TransactionResult",
        _ => "invocation",
    };

    private static bool IsIdentifier(SourceExpression expression, string name) =>
        expression.Kind == SourceExpressionKind.Identifier && expression.Symbol == name;

    private static bool IsMember(SourceExpression expression, string receiver, string member) =>
        expression.Kind == SourceExpressionKind.MemberAccess && expression.Symbol == member && expression.Children.Length == 1 &&
        IsIdentifier(expression.Children[0], receiver);

    private static bool IsNullPattern(SourceExpression expression, string value) =>
        expression.Kind == SourceExpressionKind.IsPattern && expression.Symbol == "is" && expression.Children.Length == 2 &&
        IsIdentifier(expression.Children[0], value) && expression.Children[1].Kind == SourceExpressionKind.PatternConstant &&
        expression.Children[1].Children.Length == 1 && expression.Children[1].Children[0].Kind == SourceExpressionKind.Literal &&
        expression.Children[1].Children[0].Symbol == "null";

    private static bool IsHasFlag(SourceExpression expression, string option) =>
        expression.Kind == SourceExpressionKind.Invocation && expression.Symbol == "HasFlag" && expression.Children.Length == 2 &&
        expression.Children[0].Kind == SourceExpressionKind.MemberAccess && expression.Children[0].Symbol == "HasFlag" &&
        expression.Children[0].Children.Length == 1 && IsIdentifier(expression.Children[0].Children[0], "opts") &&
        expression.Children[1].Kind == SourceExpressionKind.Argument && expression.Children[1].Children.Length == 1 &&
        IsMember(expression.Children[1].Children[0], "ExecutionOptions", option);

    private static bool IsNot(SourceExpression expression, Func<SourceExpression, bool> predicate) =>
        expression.Kind == SourceExpressionKind.Unary && expression.Symbol == "!" && expression.Children.Length == 1 &&
        predicate(expression.Children[0]);

    private static SourceExpression UnwrapParenthesized(SourceExpression expression) =>
        expression.Kind == SourceExpressionKind.Parenthesized && expression.Children.Length == 1
            ? UnwrapParenthesized(expression.Children[0])
            : expression;

    internal static bool MatchesFormulaGrammar(SemanticFormula formula, SourceExpression expression) => formula switch
    {
        SemanticFormula.OptionHasFlag => IsHasFlag(expression, "Restore"),
        SemanticFormula.EffectiveCommit => IsEffectiveCommitExpression(expression),
        SemanticFormula.PrepareFastPath => expression.Kind == SourceExpressionKind.Projection && expression.Symbol == "PrepareSimpleTransferFastPath",
        SemanticFormula.CandidateGuard => IsCandidateGuardExpression(expression),
        SemanticFormula.CodeLookup => IsLookupExpression(expression),
        SemanticFormula.SimpleDecision => IsSimpleDecisionExpression(expression),
        SemanticFormula.CommitBeforeExecution => IsCommitBeforeExpression(expression),
        SemanticFormula.PrecommitRequest => IsCommitInvocation(expression),
        SemanticFormula.CalculateAvailableGas => IsAvailableGasInvocation(expression),
        SemanticFormula.GasFailurePolicy => IsAvailableDefaultAssignment(expression),
        SemanticFormula.SimpleHandoff => IsHandoffExpression(expression, "ExecuteSimpleTransfer"),
        SemanticFormula.EvmHandoff => IsHandoffExpression(expression, "ExecuteEvmTransaction"),
        _ => false,
    };

    private static bool IsEffectiveCommitExpression(SourceExpression expression)
    {
        SourceExpression root = UnwrapParenthesized(expression);
        if (root.Kind != SourceExpressionKind.Binary || root.Symbol != "||" || root.Children.Length != 2 ||
            !IsHasFlag(root.Children[0], "Commit"))
        {
            return false;
        }

        SourceExpression conjunction = UnwrapParenthesized(root.Children[1]);
        return conjunction.Kind == SourceExpressionKind.Binary && conjunction.Symbol == "&&" && conjunction.Children.Length == 2 &&
            IsNot(conjunction.Children[0], operand => IsHasFlag(operand, "SkipValidation")) &&
            IsNot(conjunction.Children[1], operand => IsMember(operand, "spec", "IsEip658Enabled"));
    }

    private static bool IsCandidateGuardExpression(SourceExpression expression)
    {
        SourceExpression root = UnwrapParenthesized(expression);
        return root.Kind == SourceExpressionKind.Binary && root.Symbol == "||" && root.Children.Length == 2 &&
            IsNullPattern(root.Children[0], "recipient") && IsNot(root.Children[1], IsCandidateInvocation);
    }

    private static bool IsCandidateInvocation(SourceExpression expression) =>
        expression.Kind == SourceExpressionKind.Invocation && expression.Symbol == "IsSimpleTransferFastPathCandidate" &&
        expression.Children.Length == 3 && IsIdentifier(expression.Children[0], "IsSimpleTransferFastPathCandidate") &&
        expression.Children[1].Kind == SourceExpressionKind.Argument && expression.Children[2].Kind == SourceExpressionKind.Argument &&
        expression.Children[1].Children.Length == 1 && expression.Children[2].Children.Length == 1 &&
        IsIdentifier(expression.Children[1].Children[0], "tx") && IsIdentifier(expression.Children[2].Children[0], "_isCodeOverridable");

    private static bool IsLookupExpression(SourceExpression expression) =>
        expression.Kind == SourceExpressionKind.Invocation && expression.Symbol == "GetCachedCodeInfo" && expression.Children.Length == 5 &&
        expression.Children[0].Kind == SourceExpressionKind.MemberAccess && expression.Children[0].Symbol == "GetCachedCodeInfo" &&
        expression.Children[0].Children.Length == 1 && IsIdentifier(expression.Children[0].Children[0], "_codeInfoRepository") &&
        expression.Children[1].Kind == SourceExpressionKind.Argument && expression.Children[1].Children.Length == 1 &&
        IsIdentifier(expression.Children[1].Children[0], "recipient") &&
        expression.Children[2].Kind == SourceExpressionKind.Argument && expression.Children[2].Children.Length == 1 &&
        expression.Children[2].Symbol == "followDelegation:value" &&
        IsNot(expression.Children[2].Children[0], operand => IsMember(operand, "spec", "IsEip8037Enabled")) &&
        expression.Children[3].Kind == SourceExpressionKind.Argument && expression.Children[3].Children.Length == 1 &&
        IsIdentifier(expression.Children[3].Children[0], "spec") &&
        expression.Children[4].Kind == SourceExpressionKind.Argument && expression.Children[4].Children.Length == 1 &&
        expression.Children[4].Symbol == "out:value" && expression.Children[4].Children[0].Kind == SourceExpressionKind.Projection;

    private static bool IsSimpleDecisionExpression(SourceExpression expression)
    {
        SourceExpression root = UnwrapParenthesized(expression);
        return root.Kind == SourceExpressionKind.Binary && root.Symbol == "&&" && root.Children.Length == 2 &&
            IsNullPattern(root.Children[0], "delegationAddress") && IsMember(root.Children[1], "codeInfo", "IsEmpty");
    }

    private static bool IsCommitBeforeExpression(SourceExpression expression)
    {
        SourceExpression root = UnwrapParenthesized(expression);
        if (root.Kind != SourceExpressionKind.Binary || root.Symbol != "&&" || root.Children.Length != 2 ||
            !IsIdentifier(root.Children[0], "commit"))
        {
            return false;
        }

        SourceExpression disjunction = UnwrapParenthesized(root.Children[1]);
        SourceExpression[] disjuncts = FlattenLogicalOr(disjunction).ToArray();
        return disjuncts.Length == 3 &&
            IsNullPattern(disjuncts[0], "simpleTransferRecipient") &&
            IsIdentifier(disjuncts[1], "restore") &&
            IsTracingState(disjuncts[2]);
    }

    private static IEnumerable<SourceExpression> FlattenLogicalOr(SourceExpression expression)
    {
        SourceExpression root = UnwrapParenthesized(expression);
        if (root.Kind == SourceExpressionKind.Binary && root.Symbol == "||" && root.Children.Length == 2)
        {
            foreach (SourceExpression child in FlattenLogicalOr(root.Children[0])) yield return child;
            foreach (SourceExpression child in FlattenLogicalOr(root.Children[1])) yield return child;
            yield break;
        }

        yield return root;
    }

    private static bool IsTracingState(SourceExpression expression) => IsMember(expression, "tracer", "IsTracingState");

    private static bool IsCommitInvocation(SourceExpression expression) =>
        expression.Kind == SourceExpressionKind.Invocation && expression.Symbol == "Commit" && expression.Children.Length == 4 &&
        expression.Children[0].Kind == SourceExpressionKind.MemberAccess && expression.Children[0].Symbol == "Commit" &&
        expression.Children[0].Children.Length == 1 && IsIdentifier(expression.Children[0].Children[0], "WorldState") &&
        expression.Children[1].Kind == SourceExpressionKind.Argument && expression.Children[1].Children.Length == 1 && IsIdentifier(expression.Children[1].Children[0], "spec") &&
        expression.Children[2].Kind == SourceExpressionKind.Argument && expression.Children[2].Children.Length == 1 &&
        expression.Children[2].Symbol == "value" && expression.Children[2].Children[0].Kind == SourceExpressionKind.Conditional &&
        expression.Children[3].Kind == SourceExpressionKind.Argument && expression.Children[3].Children.Length == 1 &&
        expression.Children[3].Symbol == "commitRoots:value" && expression.Children[3].Children[0].Kind == SourceExpressionKind.Literal &&
        expression.Children[3].Children[0].Symbol == "false";

    private static bool IsAvailableGasInvocation(SourceExpression expression) =>
        expression.Kind == SourceExpressionKind.Invocation && expression.Symbol == "CalculateAvailableGas" &&
        expression.Children.Length == 5 && IsIdentifier(expression.Children[0], "CalculateAvailableGas") &&
        expression.Children[1].Kind == SourceExpressionKind.Argument && expression.Children[1].Children.Length == 1 && IsIdentifier(expression.Children[1].Children[0], "tx") &&
        expression.Children[2].Kind == SourceExpressionKind.Argument && expression.Children[2].Children.Length == 1 && IsIdentifier(expression.Children[2].Children[0], "spec") &&
        expression.Children[3].Kind == SourceExpressionKind.Argument && expression.Children[3].Children.Length == 1 && expression.Children[3].Symbol == "in:value" && IsIdentifier(expression.Children[3].Children[0], "intrinsicGas") &&
        expression.Children[4].Kind == SourceExpressionKind.Argument && expression.Children[4].Children.Length == 1 && expression.Children[4].Symbol == "out:value" &&
        expression.Children[4].Children[0].Kind == SourceExpressionKind.Projection;

    private static bool IsAvailableDefaultAssignment(SourceExpression expression) =>
        expression.Kind == SourceExpressionKind.Assignment && expression.Symbol == "=" && expression.Children.Length == 2 &&
        IsIdentifier(expression.Children[0], "available") && expression.Children[1].Kind == SourceExpressionKind.Default &&
        expression.Children[1].Symbol == "default";

    private static bool IsHandoffExpression(SourceExpression expression, string name) =>
        expression.Kind == SourceExpressionKind.Invocation && expression.Symbol == name && expression.Children.Length >= 2 &&
        IsIdentifier(expression.Children[0], name) && expression.Children.Skip(1).All(static child => child.Kind == SourceExpressionKind.Argument && child.Children.Length == 1);

    private static void ValidateSourceExpression(SourceExpression? expression)
    {
        if (expression is null || string.IsNullOrWhiteSpace(expression.Normalized) || string.IsNullOrWhiteSpace(expression.Symbol) ||
            string.IsNullOrWhiteSpace(expression.SymbolId) || string.IsNullOrWhiteSpace(expression.TypeName) || expression.Children is null ||
            expression.Normalized != expression.Normalized.Trim() || expression.Normalized.Any(char.IsWhiteSpace) ||
            !HasAdmittedExpressionIdentity(expression))
        {
            throw new ExtractionException("The source expression tree is incomplete or not canonical.");
        }

        foreach (SourceExpression child in expression.Children)
        {
            ValidateSourceExpression(child);
        }
    }

    private static bool HasAdmittedExpressionIdentity(SourceExpression expression) => expression.Kind switch
    {
        SourceExpressionKind.Identifier => expression.SymbolId == "identifier:" + expression.Symbol,
        SourceExpressionKind.Literal => expression.SymbolId.StartsWith("literal:", StringComparison.Ordinal),
        SourceExpressionKind.Default => expression.Symbol == "default" && expression.SymbolId == "Default:default",
        SourceExpressionKind.MemberAccess => expression.SymbolId.StartsWith("member:", StringComparison.Ordinal) &&
            expression.SymbolId.EndsWith("." + expression.Symbol, StringComparison.Ordinal),
        SourceExpressionKind.Invocation => expression.SymbolId.StartsWith("invocation:", StringComparison.Ordinal) &&
            expression.SymbolId.EndsWith("#" + expression.Symbol, StringComparison.Ordinal),
        SourceExpressionKind.Argument => expression.SymbolId == "argument:" + expression.Symbol,
        SourceExpressionKind.Unary or SourceExpressionKind.Binary or SourceExpressionKind.Conditional or
            SourceExpressionKind.IsPattern or SourceExpressionKind.Assignment or SourceExpressionKind.Parenthesized =>
            expression.SymbolId.StartsWith("operator:", StringComparison.Ordinal),
        SourceExpressionKind.Projection or SourceExpressionKind.Block or SourceExpressionKind.LocalDeclaration or
            SourceExpressionKind.VariableDeclarator or SourceExpressionKind.If or SourceExpressionKind.Return or
            SourceExpressionKind.ExpressionStatement => expression.SymbolId.StartsWith(expression.Kind + ":", StringComparison.Ordinal),
        SourceExpressionKind.PatternConstant or SourceExpressionKind.PatternUnary or SourceExpressionKind.PatternBinary =>
            expression.SymbolId.StartsWith("pattern:", StringComparison.Ordinal),
        _ => false,
    };

    internal static bool SourceExpressionMatchesBinding(SourceBinding binding, string normalized, SourceExpression candidate)
    {
        if (binding is null || string.IsNullOrWhiteSpace(binding.SourceSyntax) || string.IsNullOrWhiteSpace(normalized)) return false;

        foreach (SyntaxNode root in ParseBoundSyntax(binding.SourceSyntax))
        {
            SyntaxNode[] matches = root.DescendantNodesAndSelf().Where(node => Canonical(node) == normalized).ToArray();
            if (matches.Length == 1 && SourceExpressionsEqual(LowerSourceSyntax(matches[0]), candidate)) return true;
        }

        return false;
    }

    private static IEnumerable<SyntaxNode> ParseBoundSyntax(string sourceSyntax)
    {
        ExpressionSyntax expression = SyntaxFactory.ParseExpression(sourceSyntax, options: ParseOptions);
        if (!expression.ContainsDiagnostics) yield return expression;
        StatementSyntax statement = SyntaxFactory.ParseStatement(sourceSyntax, options: ParseOptions);
        if (!statement.ContainsDiagnostics) yield return statement;
        MemberDeclarationSyntax? member = SyntaxFactory.ParseMemberDeclaration(sourceSyntax, options: ParseOptions);
        if (member is not null && !member.ContainsDiagnostics) yield return member;
        CompilationUnitSyntax wrappedMember = SyntaxFactory.ParseCompilationUnit("class C{" + sourceSyntax + "}", options: ParseOptions);
        if (!wrappedMember.ContainsDiagnostics)
        {
            foreach (MemberDeclarationSyntax candidate in wrappedMember.DescendantNodes().OfType<MemberDeclarationSyntax>()) yield return candidate;
        }
        CompilationUnitSyntax wrappedBlock = SyntaxFactory.ParseCompilationUnit("class C{void M()" + sourceSyntax + "}", options: ParseOptions);
        if (!wrappedBlock.ContainsDiagnostics)
        {
            foreach (BlockSyntax block in wrappedBlock.DescendantNodes().OfType<BlockSyntax>()) yield return block;
        }
    }

    private static bool SourceExpressionsEqual(SourceExpression left, SourceExpression right)
    {
        if (left is null || right is null || left.Kind != right.Kind || left.Normalized != right.Normalized || left.Symbol != right.Symbol ||
            left.SymbolId != right.SymbolId || left.TypeName != right.TypeName || left.Children.Length != right.Children.Length) return false;
        for (int index = 0; index < left.Children.Length; index++)
        {
            if (!SourceExpressionsEqual(left.Children[index], right.Children[index])) return false;
        }
        return true;
    }

    private static string SignatureOf(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax method => method.Identifier.ValueText + Canonical(method.ParameterList),
        ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText + Canonical(constructor.ParameterList),
        PropertyDeclarationSyntax property => property.Identifier.ValueText + ":" + Canonical(property.Type),
        EnumDeclarationSyntax declaration => declaration.Identifier.ValueText,
        BaseTypeDeclarationSyntax declaration => declaration.Identifier.ValueText,
        _ => node.Kind().ToString(),
    };

    private static string ContainingMember(SyntaxNode node)
    {
        SyntaxNode? member = node.AncestorsAndSelf().FirstOrDefault(static candidate => candidate is MethodDeclarationSyntax or
            PropertyDeclarationSyntax or EnumDeclarationSyntax or BaseTypeDeclarationSyntax);
        return member is null ? string.Empty : SignatureOf(member);
    }

    private static int StatementOrdinal(SyntaxNode node)
    {
        StatementSyntax? statement = node.AncestorsAndSelf().OfType<StatementSyntax>().LastOrDefault();
        if (statement?.Parent is not BlockSyntax block)
        {
            return 0;
        }

        for (int index = 0; index < block.Statements.Count; index++)
        {
            if (block.Statements[index] == statement || block.Statements[index].Span.Contains(node.Span))
            {
                return index + 1;
            }
        }

        return 0;
    }

    private static string ControlFlowPath(SyntaxNode node)
    {
        List<string> path = [];
        foreach (SyntaxNode ancestor in node.Ancestors().Reverse())
        {
            switch (ancestor)
            {
                case IfStatementSyntax:
                    path.Add("if");
                    break;
                case ConditionalExpressionSyntax:
                    path.Add("conditional");
                    break;
                case ReturnStatementSyntax:
                    path.Add("return");
                    break;
                case BlockSyntax:
                    path.Add("block");
                    break;
            }
        }

        return string.Join('/', path);
    }

    private static string TokenFingerprint(SyntaxNode node)
    {
        StringBuilder builder = new();
        foreach (SyntaxToken token in node.DescendantTokens(descendIntoTrivia: false))
        {
            builder.Append(token.RawKind).Append(':').Append(token.Text.Length).Append(':').Append(token.Text).Append('\n');
        }

        return Sha256(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static string Canonical(SyntaxNode node)
    {
        string text = node.WithoutTrivia().ToFullString();
        StringBuilder builder = new(text.Length);
        foreach (char character in text)
        {
            if (!char.IsWhiteSpace(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static void RequireCanonicalContains(string actual, string expected, string message)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
        {
            throw new ExtractionException(message);
        }
    }

    private static void RequireSha256(string value, string field)
    {
        if (!IsSha256(value))
        {
            throw new ExtractionException($"{field} is not a SHA-256 identity.");
        }
    }

    private static bool IsSha256(string? value) => value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string CombinedSourceHash(SourceIdentity[] sources)
    {
        StringBuilder builder = new();
        foreach (SourceIdentity source in sources)
        {
            builder.Append(source.Path).Append('\n').Append(source.Role).Append('\n').Append(source.Sha256).Append('\n');
        }

        return Sha256(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static void EnsureWithin(string parent, string child)
    {
        if (!IsChildOf(parent, child))
        {
            throw new ExtractionException($"Path escaped repository root: {child}.");
        }
    }

    private static bool IsChildOf(string parent, string child)
    {
        string normalizedParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string normalizedChild = Path.GetFullPath(child);
        return normalizedChild.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static byte[] Serialize<T>(T value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        string normalized = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n') + "\n";
        return new UTF8Encoding(false).GetBytes(normalized);
    }

    private static IrDocument DeserializeIr(byte[] bytes)
    {
        try
        {
            IrDocument document = JsonSerializer.Deserialize<IrDocument>(bytes, JsonOptions)
                ?? throw new ExtractionException("The serialized ordinary post-nonce IR was empty.");
            ValidateIr(document);
            return document;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException("The serialized ordinary post-nonce IR is not valid JSON: " + exception.Message);
        }
    }

    private static Manifest DeserializeManifest(byte[] bytes)
    {
        try
        {
            Manifest manifest = JsonSerializer.Deserialize<Manifest>(bytes, JsonOptions)
                ?? throw new ExtractionException("The serialized ordinary post-nonce manifest was empty.");
            ValidateManifest(manifest);
            return manifest;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException("The serialized ordinary post-nonce manifest is not valid JSON: " + exception.Message);
        }
    }

    private static void ValidateRoundTrip<T>(T source, T candidate) where T : notnull
    {
        if (!Serialize(source).AsSpan().SequenceEqual(Serialize(candidate)))
        {
            throw new ExtractionException("Candidate artifact differs from source-derived artifact.");
        }
    }

    private static void CompareFiles(string expectedPath, string actualPath)
    {
        if (!File.Exists(expectedPath) || !File.Exists(actualPath) || !File.ReadAllBytes(expectedPath).AsSpan().SequenceEqual(File.ReadAllBytes(actualPath)))
        {
            throw new ExtractionException($"Generated artifact drift: {Path.GetFileName(expectedPath)}.");
        }
    }

    private static void ValidateIr(IrDocument document)
    {
        if (document.SchemaVersion != SchemaVersion || document.ExtractorVersion != ExtractorVersion ||
            document.Kernel != Kernel || document.AcceptanceState != AcceptanceState ||
            !IsSha256(document.SourceClosureSha256))
        {
            throw new ExtractionException("The ordinary post-nonce IR header changed.");
        }

        if (document.Sources is null || document.Dependencies is null || document.CompilerReferences is null || document.Options is null || document.Route is null ||
            document.Dispatch is null || document.ArithmeticRules is null || document.ExternalCorrespondence is null ||
            document.Sources.Length != SourceSpecs.Length || document.Dependencies.Length != DependencySpecs.Length)
        {
            throw new ExtractionException("The ordinary post-nonce IR is incomplete.");
        }

        if (document.Options.None != 0 || document.Options.Commit != 1 || document.Options.Restore != 2 ||
            document.Options.SkipValidation != 4 || document.Options.Warmup != 8 || document.Options.BuildUp != 16 ||
            string.IsNullOrWhiteSpace(document.Options.RestoreFormula) || string.IsNullOrWhiteSpace(document.Options.CommitFormula) ||
            string.IsNullOrWhiteSpace(document.Options.CommitBeforeFormula))
        {
            throw new ExtractionException("The source-bound execution option shape changed.");
        }

        ValidateBinding(document.Options.Binding);
        ValidateRouteShape(document.Route);
        ValidateDispatchShape(document.Dispatch);
        foreach (SourceIdentity source in document.Sources)
        {
            if (source is null || string.IsNullOrWhiteSpace(source.Path) || string.IsNullOrWhiteSpace(source.Role) || !IsSha256(source.Sha256))
            {
                throw new ExtractionException("The IR contains an incomplete source identity.");
            }
        }

        foreach (DependencyIdentity dependency in document.Dependencies)
        {
            if (dependency is null || string.IsNullOrWhiteSpace(dependency.Path) || string.IsNullOrWhiteSpace(dependency.Role) || !IsSha256(dependency.Sha256))
            {
                throw new ExtractionException("The IR contains an incomplete dependency identity.");
            }
        }

        ValidateCompilerReferenceIdentities(document.CompilerReferences);
    }

    private static void ValidateCompilerReferenceIdentities(CompilerReferenceIdentity[] references)
    {
        if (references is null || references.Length == 0)
        {
            throw new ExtractionException("The compiler/reference provenance inventory is empty.");
        }

        string[] orderedPaths = references
            .Where(static reference => reference is not null)
            .Select(static reference => reference.Path)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();
        string[] actualPaths = references
            .Where(static reference => reference is not null)
            .Select(static reference => reference.Path)
            .ToArray();
        if (actualPaths.Length != references.Length || !actualPaths.SequenceEqual(orderedPaths, StringComparer.Ordinal))
        {
            throw new ExtractionException("The compiler/reference provenance inventory is not in canonical path order.");
        }

        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> selectedNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (CompilerReferenceIdentity reference in references)
        {
            if (reference is null || string.IsNullOrWhiteSpace(reference.Path) || !paths.Add(reference.Path) ||
                string.IsNullOrWhiteSpace(reference.AssemblyName) ||
                !string.Equals(reference.AssemblyName, Path.GetFileNameWithoutExtension(reference.Path), StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(reference.Mvid) || reference.Dependencies is null)
            {
                throw new ExtractionException("The IR contains an incomplete compiler/reference provenance identity.");
            }

            string normalized = Normalize(reference.Path);
            if (!string.Equals(normalized, reference.Path, StringComparison.Ordinal) ||
                normalized.Contains("../", StringComparison.Ordinal) || normalized.Contains("/..", StringComparison.Ordinal) ||
                !normalized.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                throw new ExtractionException($"The compiler/reference provenance path is not canonical: {reference.Path}.");
            }

            bool isPlatform = normalized.StartsWith("platform/", StringComparison.Ordinal);
            if (isPlatform && Path.GetFileName(normalized["platform/".Length..]) != normalized["platform/".Length..])
            {
                throw new ExtractionException($"The platform compiler reference path is not a file name: {reference.Path}.");
            }

            bool isClosure = CompilerReferenceClosurePaths.Any(relativePath =>
                normalized.StartsWith(Normalize(relativePath) + "/", StringComparison.Ordinal));
            if (!isPlatform && !isClosure)
            {
                throw new ExtractionException($"The compiler/reference provenance escaped the admitted closure: {reference.Path}.");
            }

            RequireSha256(reference.Sha256, $"compiler reference {reference.Path}");
            if (!Guid.TryParseExact(reference.Mvid, "D", out _))
            {
                throw new ExtractionException($"Compiler reference {reference.Path} has an invalid MVID.");
            }

            string[] orderedDependencies = reference.Dependencies
                .Where(static dependency => dependency is not null)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(dependency => dependency, StringComparer.Ordinal)
                .ToArray();
            if (orderedDependencies.Length != reference.Dependencies.Length ||
                !reference.Dependencies.SequenceEqual(orderedDependencies, StringComparer.Ordinal) ||
                reference.Dependencies.Any(static dependency => string.IsNullOrWhiteSpace(dependency)))
            {
                throw new ExtractionException($"The compiler/reference provenance dependency inventory is not canonical for {reference.Path}.");
            }

            if (reference.Selected && !selectedNames.Add(reference.AssemblyName))
            {
                throw new ExtractionException($"The compiler/reference provenance selects assembly '{reference.AssemblyName}' more than once.");
            }
        }

        string[] missingRequired = RequiredCompilerAssemblyNames
            .Where(required => !selectedNames.Contains(required))
            .ToArray();
        if (missingRequired.Length != 0)
        {
            throw new ExtractionException("The compiler/reference provenance is missing required selected assemblies: " +
                string.Join(", ", missingRequired));
        }

        if (!references.Any(static reference => reference.Selected && reference.AssemblyName == "Nethermind.Evm" &&
            reference.Path == "src/Nethermind/artifacts/bin/Nethermind.Init/release/Nethermind.Evm.dll"))
        {
            throw new ExtractionException("The compiler/reference provenance does not contain the selected hash-pinned Nethermind.Evm metadata reference.");
        }
    }

    private static void ValidateRouteShape(RouteShape route)
    {
        if (route.Bindings is null || route.ExcludedRoutes is null || route.ExcludedRoutes.Length == 0 ||
            route.Registration != "BlockProcessingModule.AddScoped<ITransactionProcessor, EthereumTransactionProcessor>" ||
            route.WorldStateRegistration != "BlockProcessingModule.AddScoped<IWorldState, WorldState>" ||
            route.CodeRepositoryRegistration != "BlockProcessingModule.AddScoped<ICodeInfoRepository, CacheCodeInfoRepository>" ||
            route.BlobCalculatorRegistration != "BlockProcessingModule.AddScoped<ITransactionProcessor.IBlobBaseFeeCalculator, BlobBaseFeeCalculator>" ||
            route.ProcessorInheritance != "EthereumTransactionProcessorBase : TransactionProcessorBase<EthereumGasPolicy>" ||
            route.CommitInterface != "IWorldState.Commit(IReleaseSpec, IWorldStateTracer, bool, bool)" ||
            route.CommitImplementation != "WorldState.Commit -> StateProvider.Commit")
        {
            throw new ExtractionException("The standard-mainnet route shape changed.");
        }

        foreach (SourceBinding binding in route.Bindings)
        {
            ValidateBinding(binding);
        }
    }

    private static void ValidateDispatchShape(DispatchShape dispatch)
    {
        if (dispatch.Stages is null || dispatch.Branches is null || dispatch.Effects is null || dispatch.Handoffs is null ||
            dispatch.MethodBindings is null || dispatch.RouteBindings is null || dispatch.Semantics is null ||
            dispatch.PreloadStates is null || dispatch.PostNonceBoundary is null ||
            dispatch.Stages.Length != ExpectedStageIds.Length || dispatch.Branches.Length != ExpectedBranchIds.Length ||
            dispatch.Effects.Length != 6 || dispatch.Handoffs.Length != 2 ||
            !dispatch.PreloadStates.SequenceEqual(new[] { PreloadKind.None, PreloadKind.Loaded }))
        {
            throw new ExtractionException("The post-nonce dispatch cardinality changed.");
        }

        for (int index = 0; index < ExpectedStageIds.Length; index++)
        {
            StageShape stage = dispatch.Stages[index];
            if (stage is null || stage.Id != ExpectedStageIds[index] || stage.Ordinal != index + 1 || string.IsNullOrWhiteSpace(stage.Kind))
            {
                throw new ExtractionException("The source-bound post-nonce stage order changed.");
            }

            ValidateBinding(stage.Binding);
        }

        for (int index = 0; index < ExpectedBranchIds.Length; index++)
        {
            BranchShape branch = dispatch.Branches[index];
            string[] expectedBranchEffects = index switch
            {
                0 => [nameof(SemanticEffect.InitializePreloadedOutputs)],
                1 or 2 => [nameof(SemanticEffect.AssignSimpleRecipient)],
                3 => [nameof(SemanticEffect.LookupCodeInfo)],
                4 => [nameof(SemanticEffect.LookupCodeInfo), nameof(SemanticEffect.PreserveDelegationDesignator)],
                5 => [nameof(SemanticEffect.RequestCommit)],
                6 => [nameof(SemanticEffect.PreserveFiveZeroGasPolicy)],
                7 => [nameof(SemanticEffect.HandoffSimple)],
                8 => [nameof(SemanticEffect.HandoffEvm)],
                _ => throw new UnreachableException(),
            };
            if (branch is null || branch.Id != ExpectedBranchIds[index] || branch.Ordinal != index + 1 ||
                branch.TerminalKind != ExpectedTerminalKinds[index] || branch.Terminal != branch.TerminalKind.ToString() ||
                string.IsNullOrWhiteSpace(branch.Condition) || branch.Effects is null || branch.Effects.Length == 0 ||
                !branch.Effects.SequenceEqual(expectedBranchEffects) || branch.Condition != branch.Binding.CanonicalSyntax ||
                branch.ConditionAst is null || branch.ConditionAst.Normalized != branch.Condition ||
                !SourceExpressionMatchesBinding(branch.Binding, branch.ConditionAst.Normalized, branch.ConditionAst))
            {
                throw new ExtractionException("The source-bound post-nonce branch order or terminal shape changed.");
            }

            ValidateBinding(branch.Binding);
            ValidateSourceExpression(branch.ConditionAst);
        }

        string[] expectedEffects = ["preloadOutputs", "codeLookup", "precommit", "gasFailure", "simpleHandoff", "evmHandoff"];
        SemanticEffect[] expectedEffectKinds =
        [
            SemanticEffect.InitializePreloadedOutputs,
            SemanticEffect.LookupCodeInfo,
            SemanticEffect.RequestCommit,
            SemanticEffect.PreserveFiveZeroGasPolicy,
            SemanticEffect.HandoffSimple,
            SemanticEffect.HandoffEvm,
        ];
        for (int index = 0; index < expectedEffects.Length; index++)
        {
            EffectShape effect = dispatch.Effects[index];
            if (effect is null || effect.Id != expectedEffects[index] || effect.Ordinal != index + 1 ||
                effect.Effect != expectedEffectKinds[index] || string.IsNullOrWhiteSpace(effect.Kind) ||
                effect.Reads is null || effect.Writes is null || string.IsNullOrWhiteSpace(effect.FailureVisibility))
            {
                throw new ExtractionException("The source-bound post-nonce effect shape changed.");
            }

            ValidateBinding(effect.Binding);
        }

        ValidatePostNonceControlFlow(dispatch.PostNonceBoundary);

        string[] expectedOperations = ExpectedOperationIds;
        SemanticFormula[] expectedFormulas =
        [
            SemanticFormula.OptionHasFlag,
            SemanticFormula.EffectiveCommit,
            SemanticFormula.PrepareFastPath,
            SemanticFormula.CandidateGuard,
            SemanticFormula.CodeLookup,
            SemanticFormula.SimpleDecision,
            SemanticFormula.CommitBeforeExecution,
            SemanticFormula.PrecommitRequest,
            SemanticFormula.CalculateAvailableGas,
            SemanticFormula.GasFailurePolicy,
            SemanticFormula.SimpleHandoff,
            SemanticFormula.EvmHandoff,
        ];
        string[] expectedBindingMembers =
            ["restoreOption", "commitOption", "PrepareSimpleTransferFastPath", "candidateGuard", "codeLookup", "simpleDecision", "commitBeforeExecution", "precommitRequest", "calculateAvailableGas", "gasFailurePolicy", "simpleHandoff", "evmHandoff"];
        if (dispatch.Semantics.Widths is null || dispatch.Semantics.Operations is null || dispatch.Semantics.AdapterPremises is null ||
            dispatch.Semantics.GasClassification is null ||
            dispatch.Semantics.Widths.Length != 3 || dispatch.Semantics.Operations.Length != expectedOperations.Length ||
            dispatch.Semantics.AdapterPremises.Length != 3)
        {
            throw new ExtractionException("The source-bound post-nonce semantic lowering cardinality changed.");
        }

        for (int index = 0; index < expectedOperations.Length; index++)
        {
            SemanticOperation operation = dispatch.Semantics.Operations[index];
            NumericWidth[] expectedInputWidths = index is 0 or 1 or 6 ? [NumericWidth.Int32] : [];
            string[] expectedSourceOperands = index switch
            {
                0 => ["opts", "ExecutionOptions.Restore"],
                1 => ["opts", "ExecutionOptions.Commit", "ExecutionOptions.SkipValidation", "spec.IsEip658Enabled"],
                2 => ["preloadedCodeInfo", "preloadedDelegationAddress", "recipient"],
                3 => ["recipient", "_isCodeOverridable", "tx.AuthorizationList", "ForceSimpleTransferDisabled"],
                4 => ["recipient", "!spec.IsEip8037Enabled", "spec", "out delegation"],
                5 => ["delegationAddress", "codeInfo.IsEmpty"],
                6 => ["commit", "simpleTransferRecipient", "restore", "tracer.IsTracingState"],
                7 => ["WorldState", "spec", "tracer.IsTracingState ? tracer : NullTxTracer.Instance", "commitRoots:false"],
                8 => ["tx.GasLimit", "intrinsicGas.Standard", "spec", "out gasAvailable"],
                9 => ["GasLimitBelowIntrinsicGas", "available.Value", "available.StateReservoir", "available.StateGasUsed", "available.StateGasSpill", "available.StateGasSpillRefunded"],
                10 => ["tx", "header", "spec", "tracer", "opts", "restore", "commit", "deleteCallerAccount", "recipient", "intrinsic", "gasAvailable", "opcodeGasPrice", "premium", "reserved", "blobBaseFee"],
                11 => ["tx", "header", "spec", "tracer", "opts", "restore", "commit", "deleteCallerAccount", "intrinsic", "gasAvailable", "opcodeGasPrice", "premium", "reserved", "blobBaseFee", "preloadedCodeInfo", "preloadedDelegationAddress"],
                _ => throw new UnreachableException(),
            };
            if (operation is null || operation.Id != expectedOperations[index] || operation.Ordinal != index + 1 ||
                operation.Formula != expectedFormulas[index] || operation.InputWidths is null || !operation.InputWidths.SequenceEqual(expectedInputWidths) ||
                operation.SourceOperands is null || !operation.SourceOperands.SequenceEqual(expectedSourceOperands) || operation.Binding.Member != expectedBindingMembers[index] ||
                string.IsNullOrWhiteSpace(operation.Expression) ||
                operation.Expression != operation.Binding.CanonicalSyntax || operation.ExpressionAst is null ||
                operation.Expression != operation.ExpressionAst.Normalized ||
                !SourceExpressionMatchesBinding(operation.Binding, operation.ExpressionAst.Normalized, operation.ExpressionAst) ||
                !MatchesFormulaGrammar(operation.Formula, operation.ExpressionAst))
            {
                throw new ExtractionException("The source-bound semantic operation order or formula changed.");
            }

            ValidateBinding(operation.Binding);
            ValidateSourceExpression(operation.ExpressionAst);
            if (index == 0)
            {
                RequireInvocationBinding(operation.Binding, "HasFlag", "opts");
            }
            else if (index == 1)
            {
                RequireBindingShape(operation.Binding, nameof(SyntaxKind.LogicalOrExpression), ExpectedCommitFormula, "effective commit");
            }
        }

        ValidateGasClassificationShape(dispatch.Semantics.GasClassification);

        NumericWidth[] expectedWidths = [NumericWidth.UInt64, NumericWidth.UInt256, NumericWidth.Int32];
        string[] expectedWidthIds = ["uint64", "uint256", "executionOptions"];
        for (int index = 0; index < expectedWidths.Length; index++)
        {
            WidthShape width = dispatch.Semantics.Widths[index];
            if (width is null || width.Id != expectedWidthIds[index] || width.Width != expectedWidths[index] ||
                width.Bits != (index == 0 ? 64 : index == 1 ? 256 : 32) || width.SourceDefinition != width.Binding.CanonicalSyntax)
            {
                throw new ExtractionException("The source-bound numeric width shape changed.");
            }

            ValidateBinding(width.Binding);
        }

        foreach (AdapterPremise premise in dispatch.Semantics.AdapterPremises)
        {
            if (premise is null || premise.Bindings is null || premise.Bindings.Length == 0 ||
                premise.ProjectionAsts is null || premise.ProjectionAsts.Length != premise.Bindings.Length ||
                premise.DomainAst is null || premise.DomainBinding is null ||
                string.IsNullOrWhiteSpace(premise.Projection) || string.IsNullOrWhiteSpace(premise.Assumption) ||
                string.IsNullOrWhiteSpace(premise.DomainPredicate))
            {
                throw new ExtractionException("The source-bound adapter premise is incomplete.");
            }

            ValidateSourceExpression(premise.DomainAst);
            ValidateBinding(premise.DomainBinding);
            if (!SourceExpressionMatchesBinding(premise.DomainBinding, premise.DomainAst.Normalized, premise.DomainAst))
            {
                throw new ExtractionException("The source-bound adapter domain changed.");
            }

            for (int projectionIndex = 0; projectionIndex < premise.Bindings.Length; projectionIndex++)
            {
                ValidateSourceExpression(premise.ProjectionAsts[projectionIndex]);
                if (!SourceExpressionMatchesBinding(premise.Bindings[projectionIndex], premise.ProjectionAsts[projectionIndex].Normalized,
                        premise.ProjectionAsts[projectionIndex]))
                {
                    throw new ExtractionException("The source-bound adapter projection changed.");
                }
            }

            foreach (SourceBinding binding in premise.Bindings)
            {
                ValidateBinding(binding);
            }
        }

        foreach (SourceBinding handoff in dispatch.Handoffs)
        {
            ValidateBinding(handoff);
        }
        ValidateInvocationBinding(dispatch.Semantics.Operations[4].Binding, "GetCachedCodeInfo", "_codeInfoRepository");
        ValidateInvocationBinding(dispatch.Semantics.Operations[7].Binding, "Commit", "WorldState");
        ValidateInvocationBinding(dispatch.Semantics.Operations[8].Binding, "CalculateAvailableGas", string.Empty);
        ValidateInvocationBinding(dispatch.Semantics.Operations[10].Binding, "ExecuteSimpleTransfer", string.Empty);
        ValidateInvocationBinding(dispatch.Semantics.Operations[11].Binding, "ExecuteEvmTransaction", string.Empty);
        ValidateInvocationBinding(dispatch.Handoffs[0], "ExecuteSimpleTransfer", string.Empty);
        ValidateInvocationBinding(dispatch.Handoffs[1], "ExecuteEvmTransaction", string.Empty);
        foreach (SourceBinding binding in dispatch.MethodBindings.Concat(dispatch.RouteBindings))
        {
            ValidateBinding(binding);
            switch (binding.Member)
            {
                case "Process -> ExecuteCore":
                    ValidateInvocationBinding(binding, "ExecuteCore", string.Empty);
                    break;
                case "ExecuteCore -> UseSystemProcessor":
                    ValidateInvocationBinding(binding, "UseSystemProcessor", "SystemTransactionRoutingKernel");
                    break;
                case "ExecuteCore -> Execute(3)":
                    ValidateInvocationBinding(binding, "Execute", string.Empty);
                    break;
                case "Execute(3) -> RecoverSenderBeforeIntrinsicGas":
                    ValidateInvocationBinding(binding, "RecoverSenderBeforeIntrinsicGas", string.Empty);
                    break;
                case "Execute(3) -> CalculateIntrinsicGas":
                    ValidateInvocationBinding(binding, "CalculateIntrinsicGas", string.Empty);
                    break;
                case "Execute(3) -> Execute(6)":
                    ValidateInvocationBinding(binding, "Execute", string.Empty);
                    break;
                case "Execute(6) -> IncrementNonce":
                    ValidateInvocationBinding(binding, "IncrementNonce", string.Empty);
                    break;
                case "codeLookup":
                    ValidateInvocationBinding(binding, "GetCachedCodeInfo", "_codeInfoRepository");
                    break;
                case "precommitRequest":
                    ValidateInvocationBinding(binding, "Commit", "WorldState");
                    break;
                case "calculateAvailableGas":
                    ValidateInvocationBinding(binding, "CalculateAvailableGas", string.Empty);
                    break;
                case "simpleHandoff":
                    ValidateInvocationBinding(binding, "ExecuteSimpleTransfer", string.Empty);
                    break;
                case "evmHandoff":
                    ValidateInvocationBinding(binding, "ExecuteEvmTransaction", string.Empty);
                    break;
            }
        }

        if (dispatch.RestoreFormula != ExpectedRestoreFormula || dispatch.CommitFormula != ExpectedCommitFormula)
        {
            throw new ExtractionException("The execution-option formulas were not source-bound to their exact initializers.");
        }
        RequireCanonicalContains(dispatch.CommitBeforeFormula, "commit&&(simpleTransferRecipientisnull||restore||tracer.IsTracingState)", "The precommit guard was not source-bound.");
        RequireCanonicalContains(dispatch.CandidateFormula, "recipientisnull||!IsSimpleTransferFastPathCandidate(tx,_isCodeOverridable)", "The candidate guard was not source-bound.");
        RequireCanonicalContains(dispatch.LookupFormula, "GetCachedCodeInfo(recipient,followDelegation:!spec.IsEip8037Enabled,spec,outpreloadedDelegationAddress)", "The lookup shape was not source-bound.");
        if (dispatch.SimpleFormula != "delegationAddressisnull&&codeInfo.IsEmpty" || dispatch.AvailableGasFailureFormula != "available=default")
        {
            throw new ExtractionException("The simple decision or available-gas failure policy changed.");
        }
    }

    private static void ValidateGasClassificationShape(GasClassification classification)
    {
        if (classification.Condition != ExpectedGasCondition || classification.SuccessResult != ExpectedGasSuccessResult ||
            classification.FailureResult != ExpectedGasFailureResult || classification.ConditionBinding.CanonicalSyntax != classification.Condition ||
            classification.SuccessBinding.CanonicalSyntax != classification.SuccessResult ||
            classification.FailureBinding.CanonicalSyntax != classification.FailureResult ||
            classification.ConditionAst is null || classification.SuccessAst is null || classification.FailureAst is null ||
            classification.ConditionAst.Normalized != classification.Condition ||
            classification.SuccessAst.Normalized != classification.SuccessResult ||
            classification.FailureAst.Normalized != classification.FailureResult ||
            !SourceExpressionMatchesBinding(classification.ConditionBinding, classification.ConditionAst.Normalized, classification.ConditionAst) ||
            !SourceExpressionMatchesBinding(classification.SuccessBinding, classification.SuccessAst.Normalized, classification.SuccessAst) ||
            !SourceExpressionMatchesBinding(classification.FailureBinding, classification.FailureAst.Normalized, classification.FailureAst))
        {
            throw new ExtractionException("The source-bound CalculateAvailableGas classification changed.");
        }

        ValidateBinding(classification.ConditionBinding);
        ValidateBinding(classification.SuccessBinding);
        ValidateBinding(classification.FailureBinding);
        ValidateSourceExpression(classification.ConditionAst);
        ValidateSourceExpression(classification.SuccessAst);
        ValidateSourceExpression(classification.FailureAst);
        RequireInvocationBinding(classification.ConditionBinding, "TryCreateAvailableFromIntrinsic", "TGasPolicy");
        RequireTransactionResultBinding(classification.SuccessBinding, "Ok");
        RequireTransactionResultBinding(classification.FailureBinding, "GasLimitBelowIntrinsicGas");
    }

    private static void ValidatePostNonceControlFlow(PostNonceControlFlow boundary)
    {
        if (boundary is null)
        {
            throw new ExtractionException("The exhaustive post-IncrementNonce CFG boundary is missing.");
        }

        int[] orderedExits = boundary.ClosedNormalExitBlocks?.Distinct().OrderBy(static ordinal => ordinal).ToArray() ?? [];
        int[] boundaryBlocks = [boundary.PredicateBlock, boundary.SuccessEdgeBlock, boundary.GasBoundaryBlock, boundary.DispatchBoundaryBlock];
        if (boundary.SuccessPredicate != ExpectedPostNonceSuccessPredicate ||
            boundary.PredicateBlock < 0 || boundary.SuccessEdgeBlock < 0 || boundary.GasBoundaryBlock < 0 ||
            boundary.DispatchBoundaryBlock < 0 || boundary.ClosedNormalExitBlocks is null || orderedExits.Length == 0 ||
            !boundary.ClosedNormalExitBlocks.SequenceEqual(orderedExits) ||
            boundaryBlocks.Distinct().Count() != boundaryBlocks.Length || boundaryBlocks.Any(orderedExits.Contains) ||
            boundary.SuccessPredicateBinding is null || boundary.SuccessPredicateBinding.Member != "postNonceSuccessPredicate" ||
            boundary.SuccessPredicateBinding.CanonicalSyntax != ExpectedPostNonceSuccessPredicate)
        {
            throw new ExtractionException("The exhaustive post-IncrementNonce CFG boundary changed or left a normal exit unclosed.");
        }

        ValidateBinding(boundary.SuccessPredicateBinding);
    }

    private static void ValidateManifest(Manifest manifest)
    {
        if (manifest.SchemaVersion != SchemaVersion || manifest.ExtractorVersion != ExtractorVersion || manifest.Kernel != Kernel ||
            manifest.AcceptanceState != AcceptanceState || !IsSha256(manifest.SourceClosureSha256) || !IsSha256(manifest.CombinedSourceSha256) ||
            !IsSha256(manifest.SemanticIrSha256) || manifest.Sources is null || manifest.Dependencies is null || manifest.CompilerReferences is null || manifest.Bindings is null ||
            manifest.Ir is null || manifest.Lean is null || manifest.Sources.Length != SourceSpecs.Length || manifest.Dependencies.Length != DependencySpecs.Length)
        {
            throw new ExtractionException("The ordinary post-nonce manifest header changed.");
        }

        foreach (SourceBinding binding in manifest.Bindings)
        {
            ValidateBinding(binding);
        }
        foreach (SourceIdentity source in manifest.Sources)
        {
            if (source is null || !IsSha256(source.Sha256)) throw new ExtractionException("The manifest has an invalid source identity.");
        }
        foreach (DependencyIdentity dependency in manifest.Dependencies)
        {
            if (dependency is null || !IsSha256(dependency.Sha256)) throw new ExtractionException("The manifest has an invalid dependency identity.");
        }
        ValidateCompilerReferenceIdentities(manifest.CompilerReferences);
        if (!IsSha256(manifest.Ir.Sha256) || !IsSha256(manifest.Lean.Sha256) || string.IsNullOrWhiteSpace(manifest.Ir.Path) || string.IsNullOrWhiteSpace(manifest.Lean.Path))
        {
            throw new ExtractionException("The manifest artifact identities are incomplete.");
        }
    }

    private static void ValidateBinding(SourceBinding? binding)
    {
        if (binding is null || string.IsNullOrWhiteSpace(binding.Path) || string.IsNullOrWhiteSpace(binding.Owner) ||
            string.IsNullOrWhiteSpace(binding.Member) || string.IsNullOrWhiteSpace(binding.Signature) ||
            string.IsNullOrWhiteSpace(binding.NodeKind) || string.IsNullOrWhiteSpace(binding.CanonicalSyntax) ||
            string.IsNullOrWhiteSpace(binding.SourceSyntax) || string.IsNullOrWhiteSpace(binding.OperationKind) ||
            !binding.OperationKind.StartsWith("Syntax:", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(binding.OperationType) ||
            string.IsNullOrWhiteSpace(binding.ContainingMember) || binding.Receiver is null || binding.TargetSymbol is null ||
            binding.ReadInside is null || binding.WrittenInside is null || binding.ReadOutside is null || binding.WrittenOutside is null ||
            string.IsNullOrWhiteSpace(binding.CandidateReason) || binding.CandidateReason != "None" ||
            binding.IsErrorSymbol || binding.HasCandidateSymbols ||
            binding.TargetSymbolKind == "ErrorType" || binding.TargetSymbol.Contains("<error", StringComparison.OrdinalIgnoreCase) ||
            binding.TargetSymbol.Length != 0 && string.IsNullOrWhiteSpace(binding.TargetSymbolKind) ||
            binding.StatementOrdinal < 0 || binding.ControlFlowBlock < -1 || binding.StartLine <= 0 || binding.StartColumn <= 0 ||
            binding.EndLine <= 0 || binding.EndColumn <= 0 || !IsSha256(binding.TokenSha256) || !IsSha256(binding.CanonicalSyntaxSha256) ||
            binding.CanonicalSyntaxSha256 != Sha256(Encoding.UTF8.GetBytes(binding.CanonicalSyntax)) ||
            binding.ReadInside.Distinct(StringComparer.Ordinal).Count() != binding.ReadInside.Length ||
            binding.WrittenInside.Distinct(StringComparer.Ordinal).Count() != binding.WrittenInside.Length ||
            binding.ReadOutside.Distinct(StringComparer.Ordinal).Count() != binding.ReadOutside.Length ||
            binding.WrittenOutside.Distinct(StringComparer.Ordinal).Count() != binding.WrittenOutside.Length ||
            binding.NodeKind == nameof(SyntaxKind.InvocationExpression) && string.IsNullOrWhiteSpace(binding.TargetSymbol))
        {
            throw new ExtractionException("The IR contains an incomplete or tampered source binding.");
        }
    }

    private static void ValidateInvocationBinding(SourceBinding binding, string targetMember, string receiver)
    {
        if (binding.NodeKind != nameof(SyntaxKind.InvocationExpression) ||
            !binding.TargetSymbol.Contains("." + targetMember, StringComparison.Ordinal) ||
            receiver.Length != 0 && !binding.Receiver.Contains(receiver, StringComparison.Ordinal))
        {
            throw new ExtractionException($"The serialized binding for {targetMember} no longer resolves its admitted receiver/overload.");
        }
    }

    private static ControlFlowGraph? TryGetControlFlowGraph(SemanticModel model, MethodDeclarationSyntax method)
    {
        try
        {
            IOperation? body = model.GetOperation(method);
            return body switch
            {
                IMethodBodyOperation methodBody => ControlFlowGraph.Create(methodBody),
                _ => null,
            };
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static BasicBlock? FindBlock(ControlFlowGraph graph, SyntaxNode syntax) => graph.Blocks
        .Where(block => BlockContainsSyntax(block, syntax))
        .OrderBy(block => block.Ordinal)
        .FirstOrDefault();

    private static BasicBlock RequireBlock(ControlFlowGraph graph, SyntaxNode syntax, string description) =>
        FindBlock(graph, syntax) is { } block && block.IsReachable
            ? block
            : throw new ExtractionException($"{description} is not on a reachable control-flow block.");

    private static bool BlockContainsSyntax(BasicBlock block, SyntaxNode syntax) =>
        block.Operations.Any(operation => OperationContainsSyntax(operation, syntax)) ||
        block.BranchValue is not null && OperationContainsSyntax(block.BranchValue, syntax);

    private static bool OperationContainsSyntax(IOperation operation, SyntaxNode syntax) =>
        ReferenceEquals(operation.Syntax.SyntaxTree, syntax.SyntaxTree) && operation.Syntax.FullSpan.Contains(syntax.FullSpan);

    private static IEnumerable<BasicBlock> Successors(BasicBlock block)
    {
        HashSet<int> seen = [];
        foreach (ControlFlowBranch? branch in new[] { block.FallThroughSuccessor, block.ConditionalSuccessor })
        {
            if (branch?.Destination is BasicBlock destination && seen.Add(destination.Ordinal))
            {
                yield return destination;
            }
        }
    }

    private static bool CanReach(BasicBlock start, BasicBlock target)
    {
        Queue<BasicBlock> pending = new();
        HashSet<int> visited = [];
        pending.Enqueue(start);
        while (pending.TryDequeue(out BasicBlock? block))
        {
            if (!visited.Add(block.Ordinal) || !block.IsReachable)
            {
                continue;
            }

            if (ReferenceEquals(block, target))
            {
                return true;
            }

            foreach (BasicBlock successor in Successors(block))
            {
                pending.Enqueue(successor);
            }
        }

        return false;
    }

    private static bool CanReachWithout(BasicBlock start, BasicBlock target, BasicBlock excluded)
    {
        Queue<BasicBlock> pending = new();
        HashSet<int> visited = [];
        pending.Enqueue(start);
        while (pending.TryDequeue(out BasicBlock? block))
        {
            if (!visited.Add(block.Ordinal) || !block.IsReachable || ReferenceEquals(block, excluded))
            {
                continue;
            }

            if (ReferenceEquals(block, target))
            {
                return true;
            }

            foreach (BasicBlock successor in Successors(block))
            {
                pending.Enqueue(successor);
            }
        }

        return false;
    }

    private static bool CanReachExitWithout(ControlFlowGraph graph, BasicBlock start, BasicBlock excluded)
    {
        Queue<BasicBlock> pending = new();
        HashSet<int> visited = [];
        pending.Enqueue(start);
        while (pending.TryDequeue(out BasicBlock? block))
        {
            if (!visited.Add(block.Ordinal) || !block.IsReachable || ReferenceEquals(block, excluded))
            {
                continue;
            }

            if (block.Kind.ToString() == "Exit")
            {
                return true;
            }

            foreach (BasicBlock successor in Successors(block))
            {
                pending.Enqueue(successor);
            }
        }

        return false;
    }

    private static IEnumerable<NormalExit> EnumerateNormalExitsBefore(
        BasicBlock start,
        BasicBlock boundary,
        BasicBlock? allowedBoundary)
    {
        Queue<(BasicBlock Block, bool PassedAllowedBoundary)> pending = new();
        HashSet<(int Ordinal, bool PassedAllowedBoundary)> visited = [];
        pending.Enqueue((start, ReferenceEquals(start, allowedBoundary)));
        while (pending.TryDequeue(out (BasicBlock Block, bool PassedAllowedBoundary) state))
        {
            BasicBlock block = state.Block;
            bool passedAllowedBoundary = state.PassedAllowedBoundary || ReferenceEquals(block, allowedBoundary);
            if (!visited.Add((block.Ordinal, passedAllowedBoundary)) || !block.IsReachable || ReferenceEquals(block, boundary))
            {
                continue;
            }

            if (block.Kind.ToString() == "Exit")
            {
                yield return new NormalExit(block, passedAllowedBoundary);
                continue;
            }

            foreach (BasicBlock successor in Successors(block))
            {
                pending.Enqueue((successor, passedAllowedBoundary));
            }
        }
    }

    private sealed class SemanticContext(Dictionary<SyntaxTree, SemanticModel> models)
    {
        private readonly Dictionary<SyntaxTree, SemanticModel> _models = models;
        private readonly Dictionary<MethodDeclarationSyntax, ControlFlowGraph?> _graphs = [];

        internal SemanticModel Model(SyntaxTree tree) => _models[tree];

        internal ControlFlowGraph? Graph(MethodDeclarationSyntax method)
        {
            if (!_graphs.TryGetValue(method, out ControlFlowGraph? graph))
            {
                graph = TryGetControlFlowGraph(Model(method.SyntaxTree), method);
                _graphs.Add(method, graph);
            }

            return graph;
        }

        internal ControlFlowInfo Flow(SyntaxNode node)
        {
            MethodDeclarationSyntax? method = node.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            if (method is null)
            {
                return new ControlFlowInfo(-1);
            }

            ControlFlowGraph? graph = Graph(method);

            return graph is null ? new ControlFlowInfo(-1) : new ControlFlowInfo(FindBlock(graph, node)?.Ordinal ?? -1);
        }
    }

    private sealed record ControlFlowInfo(int Block);

    private sealed record DataFlowInfo(
        bool Succeeded,
        string[] ReadInside,
        string[] WrittenInside,
        string[] ReadOutside,
        string[] WrittenOutside)
    {
        internal static DataFlowInfo Empty { get; } = new(false, [], [], [], []);
    }

    private sealed record NormalExit(BasicBlock Block, bool PassedAllowedBoundary);

    private sealed record CompilerReferenceClosure(
        MetadataReference[] References,
        CompilerReferenceIdentity[] Identities,
        Dictionary<string, string> ResolvedPaths);

    private sealed record SourceSpec(string Path, string Role);

    private sealed record DependencySpec(string Path, string Role);

    private sealed record AdmissionSource(string Role, string Path, string Sha256);

    private sealed record AdmissionDependency(string Role, string Path, string Sha256);

    private sealed record AdmissionClosure(string Sha256, AdmissionSource[] Sources, AdmissionDependency[] Dependencies);

    private sealed record DispatchValidation(DispatchShape Shape, string RestoreFormula, string CommitFormula, string CommitBeforeFormula);

}
