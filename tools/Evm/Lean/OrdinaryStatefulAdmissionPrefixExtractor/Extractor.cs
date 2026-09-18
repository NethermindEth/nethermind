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

namespace Nethermind.Evm.Lean.OrdinaryStatefulAdmissionPrefixExtractor;

internal static class Extractor
{
    internal const string TransactionProcessorPath =
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs";
    internal const string OptionsPath =
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/ExecutionOptions.cs";
    internal const string TransactionExtensionsPath =
        "src/Nethermind/Nethermind.Core/TransactionExtensions.cs";
    internal const string TransactionPath =
        "src/Nethermind/Nethermind.Core/Transaction.cs";
    internal const string TxTypePath =
        "src/Nethermind/Nethermind.Core/TxType.cs";
    internal const string TxTypeExtensionsPath =
        "src/Nethermind/Nethermind.Core/TxTypeExtensions.cs";
    internal const string BlobGasCalculatorPath =
        "src/Nethermind/Nethermind.Evm/BlobGasCalculator.cs";
    internal const string Eip4844ConstantsPath =
        "src/Nethermind/Nethermind.Core/Eip4844Constants.cs";
    internal const string RoutingKernelPath =
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/SystemTransactionRoutingKernel.cs";
    internal const string MainnetDiPath =
        "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs";
    internal const string SystemProcessorPath =
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/SystemTransactionProcessor.cs";
    internal const string XdcProcessorPath =
        "src/Nethermind/Nethermind.Xdc/XdcTransactionProcessor.cs";
    internal const string TaikoProcessorPath =
        "src/Nethermind/Nethermind.Taiko/TaikoTransactionProcessor.cs";
    internal const string WorldStatePath =
        "src/Nethermind/Nethermind.State/WorldState.cs";
    internal const string StateProviderPath =
        "src/Nethermind/Nethermind.State/StateProvider.cs";
    internal const string SourceGrammarAuditPath =
        "<external compiler-resolution audit>";
    internal const string SourceGrammarAuditMember =
        "finite Roslyn AST-subset grammar tags";

    private const int SchemaVersion = 4;
    private const string ExtractorVersion = "2.1.0";
    private const string IrFileName = "OrdinaryStatefulAdmissionPrefix.ir.json";
    private const string ManifestFileName = "OrdinaryStatefulAdmissionPrefix.source-manifest.json";
    private const string DefaultLeanPath =
        "tools/Evm/Lean/OrdinaryStatefulAdmissionPrefixExtractor/Generated/OrdinaryStatefulAdmissionPrefix.lean";
    private const string ScratchBasePath = @"D:\tmp";
    private const string AdmissionClosurePath =
        "tools/Evm/Lean/OrdinaryStatefulAdmissionPrefixExtractor/Admission/ProductionClosure.txt";
    private const string Kernel =
        "Nethermind ordinary standard-mainnet transaction stateful admission prefix";
    private const string AcceptanceState =
        "bounded-source-extraction-and-refinement-only";

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
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private static readonly string[] ExpectedStageIds =
    [
        "validateStatic",
        "calculateEffectiveGasPrice",
        "updateMetrics",
        "recoverSenderIfNeeded",
        "validateSender",
        "buyGas",
        "incrementNonce",
    ];

    private static readonly string[] ExpectedBranchIds =
    [
        "validateStaticReturn",
        "recoverSenderCreatesAccount",
        "recoverSenderThrows",
        "validateSenderContract",
        "buyGasPremiumBelowBaseFee",
        "buyGasReservedPaymentOverflow",
        "buyGasMaximumFeeOverflow",
        "buyGasValueOverflow",
        "buyGasBlobMaximumFeeOverflow",
        "buyGasBlobFeeCalculationOverflow",
        "buyGasBlobFeeCapBelowBaseFee",
        "buyGasBlobPaymentOverflow",
        "buyGasInsufficientBalanceWarmup",
        "buyGasInsufficientBalanceReturn",
        "buyGasDebit",
        "incrementNonceMismatch",
        "incrementNonceSet",
        "combinedAdmissionFailureRestore",
        "continueAfterAdmissionPrefix",
    ];

    private static readonly BranchTerminalKind[] ExpectedBranchTerminalKinds =
    [
        BranchTerminalKind.Return,
        BranchTerminalKind.Continue,
        BranchTerminalKind.Throw,
        BranchTerminalKind.Return,
        BranchTerminalKind.Return,
        BranchTerminalKind.Return,
        BranchTerminalKind.Return,
        BranchTerminalKind.Return,
        BranchTerminalKind.Return,
        BranchTerminalKind.Return,
        BranchTerminalKind.Return,
        BranchTerminalKind.Return,
        BranchTerminalKind.Continue,
        BranchTerminalKind.Return,
        BranchTerminalKind.Continue,
        BranchTerminalKind.Return,
        BranchTerminalKind.Continue,
        BranchTerminalKind.Reset,
        BranchTerminalKind.Continue,
    ];

    private static readonly string[] RequiredCompleteBindingKeys =
    [
        OptionsPath + "|ExecutionOptions|enum|ExecutionOptions",
        RoutingKernelPath + "|SystemTransactionRoutingKernel|UseSystemProcessor|UseSystemProcessor(boolisSystemTransaction,ExecutionOptionsoptions)",
        MainnetDiPath + "|BlockProcessingModule|AddScoped<ITransactionProcessor, EthereumTransactionProcessor>|InvocationExpression",
        MainnetDiPath + "|BlockProcessingModule|AddScoped<ITransactionProcessor.IBlobBaseFeeCalculator, BlobBaseFeeCalculator>|InvocationExpression",
        MainnetDiPath + "|BlockProcessingModule|AddScoped<IWorldState, WorldState>|InvocationExpression",
        TransactionProcessorPath + "|TransactionProcessorBase<TGasPolicy>|Process|Process(Transactiontransaction,ITxTracertxTracer,ExecutionOptionsoptions)",
        TransactionProcessorPath + "|TransactionProcessorBase<TGasPolicy>|ExecuteCore|ExecuteCore(Transactiontx,ITxTracertracer,ExecutionOptionsopts)",
        TransactionProcessorPath + "|TransactionProcessorBase<TGasPolicy>|Execute|Execute(Transactiontx,ITxTracertracer,ExecutionOptionsopts)",
        TransactionProcessorPath + "|TransactionProcessorBase<TGasPolicy>|RecoverSenderBeforeIntrinsicGas|RecoverSenderBeforeIntrinsicGas(Transactiontx,IReleaseSpecspec)",
        TransactionProcessorPath + "|TransactionProcessorBase<TGasPolicy>|CalculateIntrinsicGas|CalculateIntrinsicGas(Transactiontx,IReleaseSpecspec,ulongblockGasLimit)",
        TransactionProcessorPath + "|TransactionProcessorBase<TGasPolicy>|Execute|Execute(Transactiontx,ITxTracertracer,ExecutionOptionsopts,BlockHeaderheader,IReleaseSpecspec,inIntrinsicGas<TGasPolicy>intrinsicGas)",
        TransactionProcessorPath + "|TransactionProcessorBase<TGasPolicy>|ValidateStatic|ValidateStatic(Transactiontx,BlockHeaderheader,IReleaseSpecspec,ExecutionOptionsopts,inIntrinsicGas<TGasPolicy>intrinsicGas)",
        TransactionProcessorPath + "|TransactionProcessorBase<TGasPolicy>|CalculateEffectiveGasPrice|CalculateEffectiveGasPrice(Transactiontx,booleip1559Enabled,inUInt256baseFee,outUInt256opcodeGasPrice)",
        TransactionProcessorPath + "|TransactionProcessorBase<TGasPolicy>|UpdateMetrics|UpdateMetrics(ExecutionOptionsopts,UInt256effectiveGasPrice)",
        TransactionProcessorPath + "|TransactionProcessorBase<TGasPolicy>|RecoverSenderIfNeeded|RecoverSenderIfNeeded(Transactiontx,IReleaseSpecspec,ExecutionOptionsopts,inUInt256effectiveGasPrice)",
        TransactionProcessorPath + "|TransactionProcessorBase<TGasPolicy>|ValidateSender|ValidateSender(Transactiontx,BlockHeaderheader,IReleaseSpecspec,ITxTracertracer,ExecutionOptionsopts)",
        TransactionProcessorPath + "|TransactionProcessorBase<TGasPolicy>|BuyGas|BuyGas(Transactiontx,IReleaseSpecspec,ITxTracertracer,ExecutionOptionsopts,inUInt256effectiveGasPrice,outUInt256premiumPerGas,outUInt256senderReservedGasPayment,outUInt256blobBaseFee)",
        TransactionProcessorPath + "|TransactionProcessorBase<TGasPolicy>|IncrementNonce|IncrementNonce(Transactiontx,BlockHeaderheader,IReleaseSpecspec,ITxTracertracer,ExecutionOptionsopts)",
        TransactionProcessorPath + "|TransactionProcessorBase<TGasPolicy>|ShouldValidateGas|ShouldValidateGas(Transactiontx,ExecutionOptionsopts)",
        TransactionProcessorPath + "|BlobBaseFeeCalculator|TryCalculateBlobFees|TryCalculateBlobFees(BlockHeaderheader,Transactiontransaction,ulongblobGasPriceUpdateFraction,outUInt256feePerBlobGas,outUInt256totalBlobBaseFee)",
        TransactionExtensionsPath + "|TransactionExtensions|CalculateEffectiveGasPrice|CalculateEffectiveGasPrice(booleip1559Enabled,inUInt256baseFee)",
        TransactionExtensionsPath + "|TransactionExtensions|TryCalculatePremiumPerGas|TryCalculatePremiumPerGas(inUInt256baseFeePerGas,outUInt256premiumPerGas)",
        TransactionExtensionsPath + "|TransactionExtensions|IsFree|IsFree()",
        TransactionExtensionsPath + "|TransactionExtensions|IsSystem|IsSystem()",
        TransactionExtensionsPath + "|TransactionExtensions.TransactionAccessor|GetBlobCount|GetBlobCount()",
        TransactionPath + "|Transaction|Supports1559|Supports1559:bool",
        TransactionPath + "|Transaction|SupportsBlobs|SupportsBlobs:bool",
        TransactionPath + "|Transaction|IsMessageCall|IsMessageCall:bool",
        TransactionPath + "|Transaction|SenderAddress|SenderAddress:Address?",
        TransactionPath + "|Transaction|Signature|Signature:Signature?",
        TransactionPath + "|Transaction|MaxFeePerBlobGas|MaxFeePerBlobGas:UInt256?",
        TransactionPath + "|Transaction|BlobVersionedHashes|BlobVersionedHashes:byte[]?[]?",
        TransactionPath + "|Transaction|GasLimit|GasLimit:ulong",
        TransactionPath + "|Transaction|Nonce|Nonce:ulong",
        TxTypePath + "|TxType|enum|TxType",
        TxTypeExtensionsPath + "|TxTypeExtensions|Supports1559|Supports1559(thisTxTypetxType)",
        TxTypeExtensionsPath + "|TxTypeExtensions|SupportsBlobs|SupportsBlobs(thisTxTypetxType)",
        BlobGasCalculatorPath + "|BlobGasCalculator|CalculateBlobGas|CalculateBlobGas(Transactiontransaction)",
        BlobGasCalculatorPath + "|BlobGasCalculator|CalculateBlobGas|CalculateBlobGas(intblobCount)",
        BlobGasCalculatorPath + "|BlobGasCalculator|CalculateBlobGas|CalculateBlobGas(ulongblobCount)",
        Eip4844ConstantsPath + "|Eip4844Constants|GasPerBlob|GasPerBlob:ulong",
        WorldStatePath + "|WorldState|CreateAccount|CreateAccount(Addressaddress,inUInt256balance,inulongnonce=default)",
        WorldStatePath + "|WorldState|SetNonce|SetNonce(Addressaddress,inulongnonce)",
        StateProviderPath + "|StateProvider|CreateAccount|CreateAccount(Addressaddress,inUInt256balance,inulongnonce=default)",
        StateProviderPath + "|StateProvider|SetNonce|SetNonce(Addressaddress,inulongnonce)",
    ];

    internal static ExtractionResult Extract(string repoRoot, string outputDirectory, string? leanOutputPath = null) =>
        ExtractCore(repoRoot, outputDirectory, leanOutputPath, requireReviewedAdmission: true);

    /// <summary>Exercises the source-to-IR lowering in tests without accepting the result as an admitted artifact.</summary>
    internal static ExtractionResult ExtractWithoutReviewedAdmissionForTest(
        string repoRoot,
        string outputDirectory,
        string? leanOutputPath = null) =>
        ExtractCore(repoRoot, outputDirectory, leanOutputPath, requireReviewedAdmission: false);

    private static ExtractionResult ExtractCore(
        string repoRoot,
        string outputDirectory,
        string? leanOutputPath,
        bool requireReviewedAdmission)
    {
        string canonicalRoot = Path.GetFullPath(repoRoot);
        AdmissionClosure admission = ReadAdmissionClosure(canonicalRoot);
        SourceFile[] sources =
        [
            Read(canonicalRoot, TransactionProcessorPath, "ordinary transaction processor"),
            Read(canonicalRoot, OptionsPath, "execution options"),
            Read(canonicalRoot, TransactionExtensionsPath, "transaction extensions"),
            Read(canonicalRoot, TransactionPath, "transaction input projection"),
            Read(canonicalRoot, TxTypePath, "transaction type projection"),
            Read(canonicalRoot, TxTypeExtensionsPath, "transaction type predicates"),
            Read(canonicalRoot, BlobGasCalculatorPath, "blob gas projection"),
            Read(canonicalRoot, Eip4844ConstantsPath, "EIP-4844 gas-per-blob constant"),
            Read(canonicalRoot, RoutingKernelPath, "system routing kernel"),
            Read(canonicalRoot, MainnetDiPath, "standard mainnet registration"),
            Read(canonicalRoot, SystemProcessorPath, "system transaction override"),
            Read(canonicalRoot, XdcProcessorPath, "XDC transaction override"),
            Read(canonicalRoot, TaikoProcessorPath, "Taiko transaction override"),
            Read(canonicalRoot, WorldStatePath, "standard world-state adapter"),
            Read(canonicalRoot, StateProviderPath, "standard state-provider adapter"),
        ];
        for (int index = 0; index < sources.Length; index++)
        {
            RejectErrors(sources[index]);
        }
        if (requireReviewedAdmission)
        {
            ValidateSourceClosure(sources, admission);
        }

        List<SourceBinding> bindings = [];
        OptionsShape options = ValidateOptions(sources[1], bindings);
        RouteShape route = ValidateRoute(sources, bindings);
        PrefixValidation prefix = ValidatePrefix(sources[0], sources[2], bindings);
        ValidateInputProjections(sources, bindings);
        if (requireReviewedAdmission)
        {
            AdmitCompleteBindings(bindings, admission);
        }

        SourceIdentity[] identities = new SourceIdentity[sources.Length];
        for (int index = 0; index < sources.Length; index++)
        {
            identities[index] = new SourceIdentity(sources[index].RelativePath, sources[index].Role, sources[index].Sha256);
        }

        IrDocument document = new(
            SchemaVersion,
            ExtractorVersion,
            Kernel,
            AcceptanceState,
            admission.Sha256,
            route,
            options,
            identities,
            prefix.Stages,
            prefix.Branches,
            prefix.Effects,
            ExtractSemanticLowering(sources, prefix),
            [
                "Transaction gas, nonce, and header gas preserve source-width UInt64 behavior with explicit modulo-2^64 operations for unchecked ulong arithmetic.",
                "Balance, fee cap, effective-price, and blob-fee calculations preserve UInt256 checked add/multiply overflow predicates and their modulo-2^256 out values; blob gas is widened from its source UInt64 value.",
                "The warmup insufficient-funds branch preserves the source min(reservedPayment,balance) debit and continues.",
                "Nonce update preserves validate || nonce < ulong.MaxValue ? nonce + 1 : 0 rather than a natural-number successor.",
            ],
            [
                "ValidateStatic is an admitted TransactionResult boundary; only its null supplied-sender rejection is source-bound for the production corollary, while detailed validation remains outside this package.",
                "ECDSA recovery, account lookup, invalid-contract-sender lookup, logging, tracing, header/spec reads, and blob base-fee calculation are typed external observations. The production corollary is restricted to the standard IWorldState -> WorldState -> StateProvider route and an explicit adapter-coherence predicate.",
                "WorldState.Reset(resetBlockChanges: false) is recorded as a journal action only; cache and database semantics remain external.",
                "The production-facing ordinary route includes Process -> ExecuteCore -> three-parameter Execute -> EIP-2780 RecoverSenderBeforeIntrinsicGas -> CalculateIntrinsicGas -> six-parameter Execute. It requires static success, a non-null supplied sender, a non-free/non-system transaction, coherent supplied/effective account facts, coherent blob nullability, a nonnegative signed-Int32 ExecutionOptions value, and options other than exactly ExecutionOptions.SkipValidation; that exact raw option routes through the system processor.",
                "The grammar-assigned SourceExpression identity and category tags are not compiler-resolved symbols or types. An error-free compiler-resolution audit over the reviewed closure must establish that each admitted tag agrees with the C# binding; this package records that as the sourceGrammarSemanticsCoherent composition obligation rather than proving it.",
                "The claim stops before PrepareSimpleTransferFastPath, code lookup, precommit, CalculateAvailableGas, VM execution, settlement, receipts, and block accounting.",
                "SystemTransactionProcessor, XdcTransactionProcessor, TaikoTransactionProcessor, plugins, DI resolution, CLR/JIT behavior, and non-standard chains are excluded.",
            ]);
        ValidateIr(document);
        byte[] irBytes = Serialize(document);
        string irSha256 = Sha256(irBytes);
        IrDocument roundTrippedDocument = DeserializeIr(irBytes);
        ValidateRoundTrip(document, roundTrippedDocument);
        byte[] leanBytes = LeanEmitter.Emit(roundTrippedDocument, irSha256);

        string output = Path.GetFullPath(outputDirectory);
        string leanPath = Path.GetFullPath(leanOutputPath ?? Path.Combine(canonicalRoot, DefaultLeanPath));
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(Path.GetDirectoryName(leanPath)!);
        string irPath = Path.Combine(output, IrFileName);
        string manifestPath = Path.Combine(output, ManifestFileName);
        Write(irPath, irBytes);
        Write(leanPath, leanBytes);

        Manifest manifest = new(
            SchemaVersion,
            ExtractorVersion,
            typeof(CSharpCompilation).Assembly.GetName().Version?.ToString() ?? "unknown",
            LanguageVersion.CSharp14.ToDisplayString(),
            Kernel,
            AcceptanceState,
            admission.Sha256,
            identities,
            bindings.ToArray(),
            new ArtifactIdentity(IrFileName, irSha256),
            new ArtifactIdentity(Normalize(DefaultLeanPath), Sha256(leanBytes)),
            CombinedHash(identities),
            irSha256);
        ValidateManifest(manifest);
        byte[] manifestBytes = Serialize(manifest);
        Manifest roundTrippedManifest = DeserializeManifest(manifestBytes);
        ValidateRoundTrip(manifest, roundTrippedManifest);
        Write(manifestPath, manifestBytes);

        return new ExtractionResult(
            irPath,
            manifestPath,
            leanPath,
            sources.Length,
            document.Branches.Length,
            irSha256,
            Sha256(manifestBytes),
            Sha256(leanBytes));
    }

    internal static void ValidateExistingArtifacts(string repoRoot, string outputDirectory, string? leanOutputPath)
    {
        string scratchBase = Path.GetFullPath(ScratchBasePath);
        Directory.CreateDirectory(scratchBase);
        string scratch = Path.Combine(scratchBase, "ordinary-stateful-admission-check-" + Guid.NewGuid().ToString("N"));
        string freshOutput = Path.Combine(scratch, "Generated");
        string freshLean = Path.Combine(freshOutput, "OrdinaryStatefulAdmissionPrefix.lean");
        string checkedOutput = Path.GetFullPath(outputDirectory);
        string checkedLean = Path.GetFullPath(leanOutputPath ?? Path.Combine(repoRoot, DefaultLeanPath));
        if (!IsChildOf(scratchBase, scratch))
        {
            throw new ExtractionException("The temporary artifact validation path escaped D:\\tmp.");
        }

        try
        {
            ExtractionResult fresh = Extract(repoRoot, freshOutput, freshLean);
            CompareFiles(Path.Combine(checkedOutput, IrFileName), fresh.IrPath);
            CompareFiles(Path.Combine(checkedOutput, ManifestFileName), fresh.ManifestPath);
            CompareFiles(checkedLean, fresh.LeanPath);
        }
        finally
        {
            if (Directory.Exists(scratch) && IsChildOf(scratchBase, scratch))
            {
                Directory.Delete(scratch, recursive: true);
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

    /// <summary>Exercises the emitter's independent fail-closed semantic guard in tests.</summary>
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

    private static OptionsShape ValidateOptions(SourceFile source, List<SourceBinding> bindings)
    {
        EnumDeclarationSyntax declaration = RequireSingleEnum(source, "ExecutionOptions");
        if (declaration.BaseList is not null)
        {
            throw new ExtractionException("ExecutionOptions must retain C#'s default signed Int32 underlying type.");
        }

        RequireEnumValue(declaration, "None", "0");
        RequireEnumValue(declaration, "Commit", "1");
        RequireEnumValue(declaration, "Restore", "2");
        RequireEnumValue(declaration, "SkipValidation", "4");
        RequireEnumValue(declaration, "Warmup", "8");
        RequireEnumValue(declaration, "BuildUp", "16");
        RequireEnumValue(declaration, "SkipValidationAndCommit", "Commit|SkipValidation");
        RequireEnumValue(declaration, "CommitAndRestore", "Commit|Restore|SkipValidation");
        bindings.Add(Bind(source, "ExecutionOptions", "enum", declaration));
        return new OptionsShape(
            None: 0,
            Commit: 1,
            Restore: 2,
            SkipValidation: 4,
            Warmup: 8,
            BuildUp: 16,
            MetricsEqualityGate: "opts is ExecutionOptions.Commit or ExecutionOptions.None",
            RecoveryCommitFormula: "opts.HasFlag(ExecutionOptions.Commit) || !spec.IsEip658Enabled",
            ShouldValidateGasFormula: "!opts.HasFlag(ExecutionOptions.SkipValidation) || tx.MaxFeePerGas != 0UL || tx.MaxPriorityFeePerGas != 0UL");
    }

    private static RouteShape ValidateRoute(SourceFile[] sources, List<SourceBinding> bindings)
    {
        SourceFile processor = FindSource(sources, TransactionProcessorPath);
        SourceFile routing = FindSource(sources, RoutingKernelPath);
        SourceFile registration = FindSource(sources, MainnetDiPath);
        SourceFile worldState = FindSource(sources, WorldStatePath);
        SourceFile stateProvider = FindSource(sources, StateProviderPath);
        SourceFile system = FindSource(sources, SystemProcessorPath);
        SourceFile xdc = FindSource(sources, XdcProcessorPath);
        SourceFile taiko = FindSource(sources, TaikoProcessorPath);
        MethodDeclarationSyntax route = RequireMethod(routing, "SystemTransactionRoutingKernel", "UseSystemProcessor", 2,
            "boolisSystemTransaction,ExecutionOptionsoptions");
        RequireCanonicalContains(
            Canonical(route),
            "isSystemTransaction||options==ExecutionOptions.SkipValidation",
            "System routing must retain exact isSystem or options equals SkipValidation behavior.");
        bindings.Add(Bind(routing, "SystemTransactionRoutingKernel", "UseSystemProcessor", route));

        MethodDeclarationSyntax load = RequireMethod(registration, "BlockProcessingModule", "Load", 1, "ContainerBuilderbuilder");
        InvocationExpressionSyntax registrationInvocation = RequireRegistrationInvocation(
            registration, load, "ITransactionProcessor,EthereumTransactionProcessor");
        bindings.Add(Bind(registration, "BlockProcessingModule", "AddScoped<ITransactionProcessor, EthereumTransactionProcessor>", registrationInvocation));
        InvocationExpressionSyntax blobCalculatorRegistration = RequireRegistrationInvocation(
            registration, load, "ITransactionProcessor.IBlobBaseFeeCalculator,BlobBaseFeeCalculator");
        bindings.Add(Bind(registration, "BlockProcessingModule",
            "AddScoped<ITransactionProcessor.IBlobBaseFeeCalculator, BlobBaseFeeCalculator>", blobCalculatorRegistration));
        InvocationExpressionSyntax worldStateRegistration = RequireRegistrationInvocation(
            registration, load, "IWorldState,WorldState");
        bindings.Add(Bind(registration, "BlockProcessingModule", "AddScoped<IWorldState, WorldState>", worldStateRegistration));

        RequireCanonicalContains(Canonical(processor.Root), "publicsealedclassEthereumTransactionProcessor(",
            "The standard processor concrete type was not found.");
        RequireCanonicalContains(Canonical(processor.Root), ":EthereumTransactionProcessorBase(",
            "The standard processor no longer closes EthereumTransactionProcessorBase.");
        RequireCanonicalContains(Canonical(processor.Root), ":TransactionProcessorBase<EthereumGasPolicy>(",
            "EthereumTransactionProcessorBase no longer closes TransactionProcessorBase<EthereumGasPolicy>.");
        MethodDeclarationSyntax process = RequireMethod(processor, "TransactionProcessorBase", "Process", 3,
            "Transactiontransaction,ITxTracertxTracer,ExecutionOptionsoptions");
        MethodDeclarationSyntax executeCore = RequireMethod(processor, "TransactionProcessorBase", "ExecuteCore", 3,
            "Transactiontx,ITxTracertracer,ExecutionOptionsopts");
        MethodDeclarationSyntax executeThree = RequireMethod(processor, "TransactionProcessorBase", "Execute", 3,
            "Transactiontx,ITxTracertracer,ExecutionOptionsopts");
        MethodDeclarationSyntax recoverBeforeIntrinsic = RequireMethod(processor, "TransactionProcessorBase", "RecoverSenderBeforeIntrinsicGas", 2,
            "Transactiontx,IReleaseSpecspec");
        MethodDeclarationSyntax calculateIntrinsic = RequireMethod(processor, "TransactionProcessorBase", "CalculateIntrinsicGas", 3,
            "Transactiontx,IReleaseSpecspec,ulongblockGasLimit");
        MethodDeclarationSyntax executeSix = RequireExecuteOverload(processor);
        InvocationExpressionSyntax processToCore = RequireDirectInvocation(process, "ExecuteCore", "", "transaction,txTracer,options");
        InvocationExpressionSyntax coreToExecute = RequireDirectInvocation(executeCore, "Execute", "", "tx,tracer,opts");
        InvocationExpressionSyntax recoverBefore = RequireDirectInvocation(executeThree, "RecoverSenderBeforeIntrinsicGas", "", "tx,spec");
        InvocationExpressionSyntax intrinsic = RequireDirectInvocation(executeThree, "CalculateIntrinsicGas", "", "tx,spec,header.GasLimit");
        InvocationExpressionSyntax enterSix = RequireDirectInvocation(executeThree, "Execute", "", "tx,tracer,opts,header,spec,inintrinsicGas");
        RequireOrder("EIP-2780 recovery before intrinsic gas", recoverBefore, intrinsic, enterSix);
        RequireCanonicalContains(Canonical(RequireBody(recoverBeforeIntrinsic)),
            "if(spec.IsEip2780Enabled&&tx.IsMessageCall&&tx.Signatureisnotnull&&(tx.SenderAddressisnull||!WorldState.AccountExists(tx.SenderAddress))){tx.SenderAddress=Ecdsa.RecoverAddress(tx,!spec.ValidateChainId);}",
            "RecoverSenderBeforeIntrinsicGas must retain the EIP-2780 sender replacement before intrinsic gas.");
        RequireCanonicalContains(Canonical(BodyOrExpression(calculateIntrinsic)),
            "TGasPolicy.CalculateIntrinsicGas(tx,spec,blockGasLimit)",
            "CalculateIntrinsicGas no longer preserves the admitted TGasPolicy projection.");
        RequireCanonicalContains(Canonical(executeCore),
            "SystemTransactionRoutingKernel.UseSystemProcessor(tx.IsSystem(),opts)",
            "The ordinary entrypoint no longer consults the admitted routing kernel.");
        SourceBinding processBinding = Bind(processor, "TransactionProcessorBase<TGasPolicy>", "Process", process);
        SourceBinding executeCoreBinding = Bind(processor, "TransactionProcessorBase<TGasPolicy>", "ExecuteCore", executeCore);
        SourceBinding executeThreeBinding = Bind(processor, "TransactionProcessorBase<TGasPolicy>", "Execute", executeThree);
        SourceBinding recoveryBeforeBinding = Bind(processor, "TransactionProcessorBase<TGasPolicy>", "RecoverSenderBeforeIntrinsicGas", recoverBeforeIntrinsic);
        SourceBinding intrinsicBinding = Bind(processor, "TransactionProcessorBase<TGasPolicy>", "CalculateIntrinsicGas", calculateIntrinsic);
        bindings.Add(processBinding);
        bindings.Add(executeCoreBinding);
        bindings.Add(executeThreeBinding);
        bindings.Add(recoveryBeforeBinding);
        bindings.Add(intrinsicBinding);

        MethodDeclarationSyntax worldCreate = RequireMethod(worldState, "WorldState", "CreateAccount", 3,
            "Addressaddress,inUInt256balance,inulongnonce=default");
        MethodDeclarationSyntax providerCreate = RequireMethod(stateProvider, "StateProvider", "CreateAccount", 3,
            "Addressaddress,inUInt256balance,inulongnonce=default");
        RequireDirectInvocation(worldCreate, "CreateAccount", "_stateProvider", "address,balance,nonce");
        RequireCanonicalContains(Canonical(RequireBody(providerCreate)),
            "Accountaccount=(balance.IsZero&&nonce==0)?Account.TotallyEmpty:newAccount(nonce,balance);PushNew(address,account);",
            "StateProvider.CreateAccount must retain zero-account construction for the standard mainnet route.");
        bindings.Add(Bind(worldState, "WorldState", "CreateAccount", worldCreate));
        bindings.Add(Bind(stateProvider, "StateProvider", "CreateAccount", providerCreate));

        RequireCanonicalContains(Canonical(system.Root), "overrideTransactionResultBuyGas",
            "SystemTransactionProcessor no longer exposes its BuyGas override exclusion.");
        RequireCanonicalContains(Canonical(xdc.Root), "overrideUInt256CalculateEffectiveGasPrice",
            "XdcTransactionProcessor no longer exposes its effective-price override exclusion.");
        RequireCanonicalContains(Canonical(taiko.Root), "classTaikoTransactionProcessor",
            "TaikoTransactionProcessor exclusion source changed shape.");

        return new RouteShape(
            "BlockProcessingModule.AddScoped<ITransactionProcessor, EthereumTransactionProcessor>",
            "BlockProcessingModule.AddScoped<ITransactionProcessor.IBlobBaseFeeCalculator, BlobBaseFeeCalculator>",
            "BlockProcessingModule.AddScoped<IWorldState, WorldState>",
            "EthereumTransactionProcessor",
            "TransactionProcessorBase<EthereumGasPolicy>",
            [
                "SystemTransactionProcessor<TGasPolicy>",
                "XdcTransactionProcessor",
                "TaikoTransactionProcessor",
                "plugin or alternate DI replacement",
            ],
            [
                "Process -> ExecuteCore -> Execute(Transaction, ITxTracer, ExecutionOptions)",
                "RecoverSenderBeforeIntrinsicGas -> CalculateIntrinsicGas -> Execute(..., IntrinsicGas)",
                "standard IWorldState -> WorldState -> StateProvider CreateAccount",
            ],
            [
                Bind(processor, "TransactionProcessorBase<TGasPolicy>", "Process->ExecuteCore", processToCore),
                Bind(processor, "TransactionProcessorBase<TGasPolicy>", "ExecuteCore->Execute", coreToExecute),
                Bind(processor, "TransactionProcessorBase<TGasPolicy>", "RecoverSenderBeforeIntrinsicGas", recoverBefore),
                Bind(processor, "TransactionProcessorBase<TGasPolicy>", "CalculateIntrinsicGas", intrinsic),
                Bind(processor, "TransactionProcessorBase<TGasPolicy>", "Execute->sixParameterExecute", enterSix),
                Bind(registration, "BlockProcessingModule.Load", "ITransactionProcessor registration", registrationInvocation),
                Bind(registration, "BlockProcessingModule.Load", "blob calculator registration", blobCalculatorRegistration),
                Bind(registration, "BlockProcessingModule.Load", "world state registration", worldStateRegistration),
                Bind(worldState, "WorldState", "CreateAccount->StateProvider", RequireDirectInvocation(worldCreate, "CreateAccount", "_stateProvider", "address,balance,nonce")),
                Bind(processor, "TransactionProcessorBase<TGasPolicy>", "sixParameterExecute", executeSix),
            ]);
    }

    private static PrefixValidation ValidatePrefix(
        SourceFile processor,
        SourceFile transactionExtensions,
        List<SourceBinding> bindings)
    {
        MethodDeclarationSyntax execute = RequireExecuteOverload(processor);
        MethodDeclarationSyntax validateStatic = RequireMethod(processor, "TransactionProcessorBase", "ValidateStatic", 5,
            "Transactiontx,BlockHeaderheader,IReleaseSpecspec,ExecutionOptionsopts,inIntrinsicGas<TGasPolicy>intrinsicGas");
        MethodDeclarationSyntax effectivePrice = RequireMethod(processor, "TransactionProcessorBase", "CalculateEffectiveGasPrice", 4,
            "Transactiontx,booleip1559Enabled,inUInt256baseFee,outUInt256opcodeGasPrice");
        MethodDeclarationSyntax updateMetrics = RequireMethod(processor, "TransactionProcessorBase", "UpdateMetrics", 2,
            "ExecutionOptionsopts,UInt256effectiveGasPrice");
        MethodDeclarationSyntax recover = RequireMethod(processor, "TransactionProcessorBase", "RecoverSenderIfNeeded", 4,
            "Transactiontx,IReleaseSpecspec,ExecutionOptionsopts,inUInt256effectiveGasPrice");
        MethodDeclarationSyntax validateSender = RequireMethod(processor, "TransactionProcessorBase", "ValidateSender", 5,
            "Transactiontx,BlockHeaderheader,IReleaseSpecspec,ITxTracertracer,ExecutionOptionsopts");
        MethodDeclarationSyntax buyGas = RequireMethod(processor, "TransactionProcessorBase", "BuyGas", 8,
            "Transactiontx,IReleaseSpecspec,ITxTracertracer,ExecutionOptionsopts,inUInt256effectiveGasPrice,outUInt256premiumPerGas,outUInt256senderReservedGasPayment,outUInt256blobBaseFee");
        MethodDeclarationSyntax incrementNonce = RequireMethod(processor, "TransactionProcessorBase", "IncrementNonce", 5,
            "Transactiontx,BlockHeaderheader,IReleaseSpecspec,ITxTracertracer,ExecutionOptionsopts");
        MethodDeclarationSyntax shouldValidateGas = RequireMethod(processor, "TransactionProcessorBase", "ShouldValidateGas", 2,
            "Transactiontx,ExecutionOptionsopts");
        MethodDeclarationSyntax extensionEffectivePrice =
            RequireMethod(transactionExtensions, "TransactionExtensions", "CalculateEffectiveGasPrice", 2,
                "booleip1559Enabled,inUInt256baseFee");
        MethodDeclarationSyntax extensionPremium =
            RequireMethod(transactionExtensions, "TransactionExtensions", "TryCalculatePremiumPerGas", 2,
                "inUInt256baseFeePerGas,outUInt256premiumPerGas");
        MethodDeclarationSyntax extensionIsFree =
            RequireMethod(transactionExtensions, "TransactionExtensions", "IsFree", 0, "");
        MethodDeclarationSyntax extensionIsSystem =
            RequireMethod(transactionExtensions, "TransactionExtensions", "IsSystem", 0, "");
        MethodDeclarationSyntax tryCalculateBlobFees =
            RequireMethod(processor, "BlobBaseFeeCalculator", "TryCalculateBlobFees", 5,
                "BlockHeaderheader,Transactiontransaction,ulongblobGasPriceUpdateFraction,outUInt256feePerBlobGas,outUInt256totalBlobBaseFee");

        InvocationExpressionSyntax validateStaticCall = RequireDirectInvocation(execute, "ValidateStatic", "",
            "tx,header,spec,opts,inintrinsicGas");
        InvocationExpressionSyntax effectivePriceCall = RequireDirectInvocation(execute, "CalculateEffectiveGasPrice", "",
            "tx,spec.IsEip1559Enabled,header.BaseFeePerGas,outUInt256opcodeGasPrice");
        InvocationExpressionSyntax updateMetricsCall = RequireDirectInvocation(execute, "UpdateMetrics", "",
            "opts,effectiveGasPrice");
        InvocationExpressionSyntax recoverCall = RequireDirectInvocation(execute, "RecoverSenderIfNeeded", "",
            "tx,spec,opts,effectiveGasPrice");
        InvocationExpressionSyntax validateSenderCall = RequireDirectInvocation(execute, "ValidateSender", "",
            "tx,header,spec,tracer,opts");
        InvocationExpressionSyntax buyGasCall = RequireDirectInvocation(execute, "BuyGas", "",
            "tx,spec,tracer,opts,effectiveGasPrice,outUInt256premiumPerGas,outUInt256senderReservedGasPayment,outUInt256blobBaseFee");
        InvocationExpressionSyntax incrementNonceCall = RequireDirectInvocation(execute, "IncrementNonce", "",
            "tx,header,spec,tracer,opts");
        InvocationExpressionSyntax prepareFastPathCall = RequireDirectInvocation(execute, "PrepareSimpleTransferFastPath", "",
            "tx,spec,outCodeInfo?preloadedCodeInfo,outAddress?preloadedDelegationAddress");
        RequireOrder(
            "ordinary stateful admission prefix",
            validateStaticCall,
            effectivePriceCall,
            updateMetricsCall,
            recoverCall,
            validateSenderCall,
            buyGasCall,
            incrementNonceCall,
            prepareFastPathCall);

        string executeBody = Canonical(RequireBody(execute));
        RequireCanonicalContains(executeBody,
            "if(!(result=ValidateStatic(tx,header,spec,opts,inintrinsicGas)))returnresult;",
            "ValidateStatic must remain the prefix first terminal gate.");
        RequireCanonicalContains(executeBody,
            "UInt256effectiveGasPrice=CalculateEffectiveGasPrice(tx,spec.IsEip1559Enabled,header.BaseFeePerGas,outUInt256opcodeGasPrice);",
            "The effective-price result and opcode-price out value changed.");
        RequireCanonicalContains(executeBody,
            "UpdateMetrics(opts,effectiveGasPrice);",
            "Metrics must remain after price calculation and before recovery.");
        RequireCanonicalContains(executeBody,
            "booldeleteCallerAccount=RecoverSenderIfNeeded(tx,spec,opts,effectiveGasPrice);",
            "Sender recovery must remain after metrics and before sender validation.");
        RequireCanonicalContains(executeBody,
            "if(!(result=ValidateSender(tx,header,spec,tracer,opts))||!(result=BuyGas(tx,spec,tracer,opts,effectiveGasPrice,outUInt256premiumPerGas,outUInt256senderReservedGasPayment,outUInt256blobBaseFee))||!(result=IncrementNonce(tx,header,spec,tracer,opts)))",
            "Sender validation, BuyGas, and IncrementNonce must remain one left-to-right short-circuit gate.");
        RequireCanonicalContains(executeBody,
            "if(restore){WorldState.Reset(resetBlockChanges:false);}returnresult;",
            "Restore must remain attached only to the combined sender/gas/nonce rejection gate.");
        EnsureNoEarlyReset(execute, prepareFastPathCall.SpanStart);

        ValidateStatic(validateStatic);
        ValidateEffectivePrice(effectivePrice, extensionEffectivePrice);
        string metricsEqualityGate = ValidateMetrics(updateMetrics);
        ValidateRecovery(recover);
        ValidateSender(validateSender);
        ValidateBuyGas(buyGas, shouldValidateGas, extensionPremium, extensionIsFree, extensionIsSystem);
        ValidateBlobCalculator(tryCalculateBlobFees);
        ValidateIncrementNonce(incrementNonce);

        MethodDeclarationSyntax[] methods =
        [
            execute, validateStatic, effectivePrice, updateMetrics, recover, validateSender, buyGas,
            incrementNonce, shouldValidateGas, extensionEffectivePrice, extensionPremium, extensionIsFree,
            extensionIsSystem, tryCalculateBlobFees,
        ];
        string[] owners =
        [
            "TransactionProcessorBase<TGasPolicy>", "TransactionProcessorBase<TGasPolicy>",
            "TransactionProcessorBase<TGasPolicy>", "TransactionProcessorBase<TGasPolicy>",
            "TransactionProcessorBase<TGasPolicy>", "TransactionProcessorBase<TGasPolicy>",
            "TransactionProcessorBase<TGasPolicy>", "TransactionProcessorBase<TGasPolicy>",
            "TransactionProcessorBase<TGasPolicy>", "TransactionExtensions", "TransactionExtensions",
            "TransactionExtensions", "TransactionExtensions", "BlobBaseFeeCalculator",
        ];
        SourceFile[] methodSources =
        [
            processor, processor, processor, processor, processor, processor, processor, processor, processor,
            transactionExtensions, transactionExtensions, transactionExtensions, transactionExtensions, processor,
        ];
        if (methods.Length != owners.Length || methods.Length != methodSources.Length)
        {
            throw new ExtractionException("Internal source-binding table lengths are inconsistent.");
        }

        for (int index = 0; index < methods.Length; index++)
        {
            bindings.Add(Bind(methodSources[index], owners[index], methods[index].Identifier.ValueText, methods[index]));
        }

        StageAnchor[] stages =
        [
            Anchor(processor, "validateStatic", 1, validateStaticCall),
            Anchor(processor, "calculateEffectiveGasPrice", 2, effectivePriceCall),
            Anchor(processor, "updateMetrics", 3, updateMetricsCall),
            Anchor(processor, "recoverSenderIfNeeded", 4, recoverCall),
            Anchor(processor, "validateSender", 5, validateSenderCall),
            Anchor(processor, "buyGas", 6, buyGasCall),
            Anchor(processor, "incrementNonce", 7, incrementNonceCall),
        ];
        return new PrefixValidation(
            stages,
            ExtractBranchShapes(processor, execute, recover, validateSender, buyGas, incrementNonce),
            ExtractEffects(processor, execute, updateMetrics, recover, validateSender, buyGas, incrementNonce),
            metricsEqualityGate,
            execute,
            effectivePrice,
            updateMetrics,
            recover,
            buyGas,
            incrementNonce,
            extensionEffectivePrice,
            extensionPremium,
            tryCalculateBlobFees);
    }

    private static void ValidateEffectivePrice(
        MethodDeclarationSyntax processorMethod,
        MethodDeclarationSyntax extensionMethod)
    {
        RequireCanonicalContains(Canonical(RequireBody(processorMethod)),
            "opcodeGasPrice=tx.CalculateEffectiveGasPrice(eip1559Enabled,inbaseFee);returnopcodeGasPrice;",
            "The standard processor must return the extension price as effective and opcode price.");
        RequireCanonicalContains(Canonical(BodyOrExpression(extensionMethod)),
            "!eip1559Enabled?tx.MaxPriorityFeePerGas:UInt256.AddOverflow(tx.MaxPriorityFeePerGas,baseFee,outUInt256effectiveFee)?tx.MaxFeePerGas:UInt256.Min(tx.MaxFeePerGas,effectiveFee)",
            "Transaction.CalculateEffectiveGasPrice must retain checked UInt256 addition and min cap.");
    }

    private static void ValidateStatic(MethodDeclarationSyntax method) =>
        RequireCanonicalContains(Canonical(RequireBody(method)),
            "if(tx.SenderAddressisnull){TraceLogInvalidTx(tx,\"SENDER_NOT_SPECIFIED\");returnTransactionResult.SenderNotSpecified;}",
            "ValidateStatic must retain the null supplied-sender rejection used by the production corollary.");

    private static string ValidateMetrics(MethodDeclarationSyntax method)
    {
        SourceExpression body = LowerSourceSyntax(RequireBody(method));
        return DescribeMetricsGate(ExtractMetricsCondition(body));
    }

    /// <summary>
    /// Extracts the complete admitted <c>UpdateMetrics</c> program rather than just its
    /// condition. A second metric call, an added statement, or an alternate branch therefore
    /// cannot silently retain the old Lean effect when test-only lowering bypasses the reviewed
    /// source-file closure.
    /// </summary>
    private static SourceExpression ExtractMetricsCondition(SourceExpression body)
    {
        ValidateSourceExpression(body);
        if (body.Kind != SourceExpressionKind.Block || body.Children.Length != 1 ||
            body.Children[0].Kind != SourceExpressionKind.If || body.Children[0].Children.Length != 2)
        {
            throw new ExtractionException("UpdateMetrics is outside the admitted one-branch source grammar.");
        }

        SourceExpression conditional = body.Children[0];
        SourceExpression statement = conditional.Children[1];
        SourceExpression[] calls = Descendants(statement)
            .Where(static expression => expression.Kind == SourceExpressionKind.Invocation)
            .ToArray();
        if (calls.Length != 1 || calls[0].Symbol != "UpdateBlockGasPrice" ||
            calls[0].SymbolId != "invocation:Metrics.UpdateBlockGasPrice#UpdateBlockGasPrice" ||
            calls[0].TypeName != "void" || calls[0].Children.Length != 2 ||
            calls[0].Children[1].Kind != SourceExpressionKind.Argument ||
            calls[0].Children[1].Children.Length != 1 ||
            calls[0].Children[1].Children[0].SymbolId != "identifier:effectiveGasPrice")
        {
            throw new ExtractionException("UpdateMetrics lost its admitted Metrics.UpdateBlockGasPrice effect.");
        }

        return conditional.Children[0];
    }

    private static string DescribeMetricsGate(SourceExpression condition)
    {
        if (condition.Kind == SourceExpressionKind.Binary && condition.Symbol == "is" && condition.Children.Length == 2 &&
            condition.Children[0].SymbolId == "identifier:opts" && IsExecutionOption(condition.Children[1], "Commit"))
        {
            return "opts is ExecutionOptions.Commit";
        }

        if (condition.Kind != SourceExpressionKind.IsPattern || condition.Children.Length != 2 ||
            condition.Children[0].SymbolId != "identifier:opts")
        {
            throw new ExtractionException("UpdateMetrics uses an unlowered execution-options equality gate: " +
                condition.Kind + "|" + condition.Normalized + ".");
        }

        SourceExpression pattern = condition.Children[1];
        return pattern switch
        {
            { Kind: SourceExpressionKind.PatternConstant, Children.Length: 1 }
                when IsExecutionOption(pattern.Children[0], "Commit") => "opts is ExecutionOptions.Commit",
            { Kind: SourceExpressionKind.PatternBinary, Symbol: "or", Children.Length: 2 }
                when IsExecutionOption(pattern.Children[0], "Commit") && IsExecutionOption(pattern.Children[1], "None") =>
                "opts is ExecutionOptions.Commit or ExecutionOptions.None",
            _ => throw new ExtractionException("UpdateMetrics uses an unlowered execution-options equality gate: " +
                condition.Kind + "|" + condition.Normalized + "."),
        };
    }

    private static bool IsExecutionOption(SourceExpression expression, string option) =>
        expression.Kind == SourceExpressionKind.PatternConstant && expression.Children.Length == 1
            ? IsExecutionOption(expression.Children[0], option)
            : expression.Kind == SourceExpressionKind.MemberAccess && expression.Symbol == option &&
                expression.SymbolId == "member:ExecutionOptions." + option && expression.TypeName == "ExecutionOptions" &&
                expression.Children.Length == 1 && expression.Children[0].SymbolId == "identifier:ExecutionOptions";

    private static void ValidateRecovery(MethodDeclarationSyntax method)
    {
        string body = Canonical(RequireBody(method));
        RequireCanonicalContains(body,
            "booldeleteCallerAccount=false;Address?sender=tx.SenderAddress;if(senderisnull||!WorldState.AccountExists(sender))",
            "RecoverSenderIfNeeded must retain absent-or-nonexistent account gate.");
        RequireCanonicalContains(body,
            "boolcommit=opts.HasFlag(ExecutionOptions.Commit)||!spec.IsEip658Enabled;",
            "Recovery commit calculation changed.");
        RequireCanonicalContains(body,
            "boolrestore=opts.HasFlag(ExecutionOptions.Restore);boolnoValidation=opts.HasFlag(ExecutionOptions.SkipValidation);",
            "Recovery option projections changed.");
        RequireCanonicalContains(body,
            "if(tx.Signatureisnotnull&&(!spec.IsEip2780Enabled||!tx.IsMessageCall))tx.SenderAddress=Ecdsa.RecoverAddress(tx,!spec.ValidateChainId);",
            "Recovery must retain EIP-2780 message-call exception and ECDSA call.");
        RequireCanonicalContains(body, "if(sender!=tx.SenderAddress)",
            "Recovery must retain original-versus-recovered sender comparison.");
        RequireCanonicalContains(body,
            "if(!commit||noValidation||effectiveGasPrice.IsZero){deleteCallerAccount=!commit||restore;WorldState.CreateAccount(sender!,inUInt256.Zero);}",
            "Recovery account creation, delete flag, or price-zero condition changed.");
        RequireCanonicalContains(body,
            "else{TraceLogInvalidTx(tx,$\"SENDER_ACCOUNT_DOES_NOT_EXIST{sender}\");if(!commit||noValidation||effectiveGasPrice.IsZero){deleteCallerAccount=!commit||restore;WorldState.CreateAccount(sender!,inUInt256.Zero);}}if(senderisnull){ThrowInvalidDataException(",
            "Recovery trace, account-creation attempt, and terminal null-sender ordering changed.");
        RequireCanonicalContains(body, "if(senderisnull){ThrowInvalidDataException(",
            "Recovery failure must remain an explicit throw after the sender path.");
    }

    private static void ValidateSender(MethodDeclarationSyntax method)
    {
        string body = Canonical(RequireBody(method));
        RequireCanonicalContains(body,
            "boolvalidate=!opts.HasFlag(ExecutionOptions.SkipValidation);if(validate&&!SkipSenderCodeCheck&&WorldState.IsInvalidContractSender(spec,tx.SenderAddress!))",
            "ValidateSender must retain SkipValidation, SkipSenderCodeCheck, and invalid-contract-sender conjunction.");
        RequireCanonicalContains(body, "returnTransactionResult.SenderHasDeployedCode;",
            "ValidateSender must retain SenderHasDeployedCode result.");
    }

    private static void ValidateBuyGas(
        MethodDeclarationSyntax method,
        MethodDeclarationSyntax shouldValidateGas,
        MethodDeclarationSyntax tryCalculatePremium,
        MethodDeclarationSyntax isFree,
        MethodDeclarationSyntax isSystem)
    {
        RequireCanonicalContains(Canonical(BodyOrExpression(shouldValidateGas)),
            "!opts.HasFlag(ExecutionOptions.SkipValidation)||tx.MaxFeePerGas!=0UL||tx.MaxPriorityFeePerGas!=0UL",
            "ShouldValidateGas must retain SkipValidation and nonzero-fee disjunction.");
        RequireCanonicalContains(Canonical(RequireBody(tryCalculatePremium)),
            "boolfreeTransaction=tx.IsFree();UInt256feeCap=tx.Supports1559?tx.MaxFeePerGas:tx.MaxPriorityFeePerGas;if(baseFeePerGas>feeCap){premiumPerGas=UInt256.Zero;returnfreeTransaction;}premiumPerGas=UInt256.Min(tx.MaxPriorityFeePerGas,feeCap-baseFeePerGas);returntrue;",
            "TryCalculatePremiumPerGas changed from admitted fee-cap branch shape.");
        RequireCanonicalContains(Canonical(BodyOrExpression(isFree)),
            "tx.IsSystem()||tx.IsServiceTransaction",
            "Transaction.IsFree changed from its system-or-service definition.");
        RequireCanonicalContains(Canonical(BodyOrExpression(isSystem)),
            "txisSystemTransaction||tx.SenderAddress==Address.SystemUser||tx.IsOPSystemTransaction",
            "Transaction.IsSystem changed from its admitted definition.");

        string body = Canonical(RequireBody(method));
        RequireCanonicalContains(body,
            "premiumPerGas=UInt256.Zero;senderReservedGasPayment=UInt256.Zero;blobBaseFee=UInt256.Zero;UInt256balance=WorldState.GetBalance(sender);",
            "BuyGas output defaults or balance read changed.");
        RequireCanonicalContains(body, "boolvalidate=ShouldValidateGas(tx,opts);",
            "BuyGas must use ShouldValidateGas.");
        RequireCanonicalContains(body,
            "if(validate&&!TryCalculatePremiumPerGas(tx,header.BaseFeePerGas,outpremiumPerGas))",
            "BuyGas premium validation changed.");
        RequireCanonicalContains(body,
            "if(UInt256.MultiplyOverflow((UInt256)tx.GasLimit,effectiveGasPrice,outsenderReservedGasPayment))",
            "BuyGas reserved-payment checked UInt256 multiplication changed.");
        RequireCanonicalContains(body, "if(spec.IsEip1559Enabled&&!tx.IsFree())",
            "BuyGas EIP-1559 maximum-fee branch changed.");
        RequireCanonicalContains(body,
            "if(UInt256.MultiplyOverflow((UInt256)tx.GasLimit,tx.MaxFeePerGas,outbalanceCheck))",
            "BuyGas EIP-1559 maximum-fee checked multiplication changed.");
        RequireCanonicalContains(body, "if(UInt256.AddOverflow(balanceCheck,tx.ValueRef,outbalanceCheck))",
            "BuyGas value checked addition changed.");
        RequireCanonicalContains(body, "if(tx.SupportsBlobs)", "BuyGas blob gate changed.");
        RequireCanonicalContains(body, "UInt256blobGas=BlobGasCalculator.CalculateBlobGas(tx);",
            "BuyGas blob gas must retain its source UInt64 calculator widening.");
        RequireCanonicalContains(body,
            "if(UInt256.MultiplyOverflow(blobGas,(UInt256)tx.MaxFeePerBlobGas!,outUInt256maxBlobGasFee)||UInt256.AddOverflow(balanceCheck,maxBlobGasFee,outbalanceCheck))",
            "BuyGas blob-cap checked multiplication/addition changed.");
        RequireCanonicalContains(body,
            "if(!_blobBaseFeeCalculator.TryCalculateBlobFees(header,tx,spec.BlobBaseFeeUpdateFraction,outUInt256feePerBlobGas,outblobBaseFee))",
            "BuyGas blob base-fee calculation ordering changed.");
        RequireCanonicalContains(body, "if(tx.MaxFeePerBlobGas<feePerBlobGas)",
            "BuyGas blob fee-cap comparison changed.");
        RequireCanonicalContains(body,
            "if(UInt256.AddOverflow(senderReservedGasPayment,blobBaseFee,outsenderReservedGasPayment))",
            "BuyGas actual blob payment checked addition changed.");
        RequireCanonicalContains(body, "if(balance<balanceCheck)", "BuyGas balance check changed.");
        RequireCanonicalContains(body,
            "if(opts.HasFlag(ExecutionOptions.Warmup)){UInt256warmCharge=UInt256.Min(senderReservedGasPayment,balance);if(!warmCharge.IsZero)WorldState.SubtractFromBalance(sender,warmCharge,spec);returnTransactionResult.Ok;}",
            "BuyGas warmup partial-charge path changed.");
        RequireCanonicalContains(body,
            "if(!senderReservedGasPayment.IsZero)WorldState.SubtractFromBalance(sender,senderReservedGasPayment,spec);returnTransactionResult.Ok;",
            "BuyGas normal debit path changed.");
    }

    private static void ValidateBlobCalculator(MethodDeclarationSyntax method) =>
        RequireCanonicalContains(Canonical(RequireBody(method)),
            "if(!BlobGasCalculator.TryCalculateFeePerBlobGas(header,blobGasPriceUpdateFraction,outfeePerBlobGas)){totalBlobBaseFee=UInt256.Zero;returnfalse;}returnBlobGasCalculator.TryCalculateBlobBaseFee(header,transaction,blobGasPriceUpdateFraction,outtotalBlobBaseFee);",
            "The standard blob-fee calculator route or false-result output changed.");

    private static void ValidateIncrementNonce(MethodDeclarationSyntax method)
    {
        string body = Canonical(RequireBody(method));
        RequireCanonicalContains(body,
            "boolvalidate=!opts.HasFlag(ExecutionOptions.SkipValidation);ulongnonce=WorldState.GetNonce(senderAddress);if(validate&&tx.Nonce!=nonce)",
            "IncrementNonce validation gate changed.");
        RequireCanonicalContains(body,
            "returntx.Nonce>nonce?TransactionResult.ErrorType.TransactionNonceTooHigh.WithDetail(",
            "IncrementNonce high/low ordering changed.");
        RequireCanonicalContains(body,
            "ulongnewNonce=validate||nonce<ulong.MaxValue?nonce+1:0;WorldState.SetNonce(senderAddress,newNonce);",
            "IncrementNonce must retain source conditional unchecked UInt64 increment.");
    }

    private static void ValidateInputProjections(SourceFile[] sources, List<SourceBinding> bindings)
    {
        SourceFile transaction = FindSource(sources, TransactionPath);
        SourceFile txType = FindSource(sources, TxTypePath);
        SourceFile txTypeExtensions = FindSource(sources, TxTypeExtensionsPath);
        SourceFile transactionExtensions = FindSource(sources, TransactionExtensionsPath);
        SourceFile blobGasCalculator = FindSource(sources, BlobGasCalculatorPath);
        SourceFile eip4844Constants = FindSource(sources, Eip4844ConstantsPath);
        SourceFile worldState = FindSource(sources, WorldStatePath);
        SourceFile stateProvider = FindSource(sources, StateProviderPath);

        PropertyDeclarationSyntax supports1559 = RequireProperty(transaction, "Transaction", "Supports1559");
        PropertyDeclarationSyntax supportsBlobs = RequireProperty(transaction, "Transaction", "SupportsBlobs");
        PropertyDeclarationSyntax isMessageCall = RequireProperty(transaction, "Transaction", "IsMessageCall");
        PropertyDeclarationSyntax senderAddress = RequireProperty(transaction, "Transaction", "SenderAddress");
        PropertyDeclarationSyntax signature = RequireProperty(transaction, "Transaction", "Signature");
        PropertyDeclarationSyntax maxFeePerBlobGas = RequireProperty(transaction, "Transaction", "MaxFeePerBlobGas");
        PropertyDeclarationSyntax blobVersionedHashes = RequireProperty(transaction, "Transaction", "BlobVersionedHashes");
        PropertyDeclarationSyntax gasLimit = RequireProperty(transaction, "Transaction", "GasLimit");
        PropertyDeclarationSyntax nonce = RequireProperty(transaction, "Transaction", "Nonce");
        RequireCanonicalContains(Canonical(supports1559), "publicboolSupports1559=>Type.Supports1559();",
            "Transaction.Supports1559 projection changed.");
        RequireCanonicalContains(Canonical(supportsBlobs), "publicboolSupportsBlobs=>Type.SupportsBlobs();",
            "Transaction.SupportsBlobs projection changed.");
        RequireCanonicalContains(Canonical(isMessageCall), "publicboolIsMessageCall=>Toisnotnull;",
            "Transaction.IsMessageCall projection changed.");
        RequireCanonicalContains(Canonical(senderAddress), "publicAddress?SenderAddress{get;set;}",
            "Transaction.SenderAddress projection changed.");
        RequireCanonicalContains(Canonical(signature), "publicSignature?Signature{get;set;}",
            "Transaction.Signature nullability projection changed.");
        RequireCanonicalContains(Canonical(maxFeePerBlobGas), "publicUInt256?MaxFeePerBlobGas{get;set;}",
            "Transaction.MaxFeePerBlobGas nullability projection changed.");
        RequireCanonicalContains(Canonical(blobVersionedHashes), "publicbyte[]?[]?BlobVersionedHashes{get;set;}",
            "Transaction.BlobVersionedHashes nullability projection changed.");
        RequireCanonicalContains(Canonical(gasLimit), "publiculongGasLimit{get;set;}",
            "Transaction.GasLimit UInt64 projection changed.");
        RequireCanonicalContains(Canonical(nonce), "publiculongNonce{get;set;}",
            "Transaction.Nonce UInt64 projection changed.");
        bindings.Add(Bind(transaction, "Transaction", "Supports1559", supports1559));
        bindings.Add(Bind(transaction, "Transaction", "SupportsBlobs", supportsBlobs));
        bindings.Add(Bind(transaction, "Transaction", "IsMessageCall", isMessageCall));
        bindings.Add(Bind(transaction, "Transaction", "SenderAddress", senderAddress));
        bindings.Add(Bind(transaction, "Transaction", "Signature", signature));
        bindings.Add(Bind(transaction, "Transaction", "MaxFeePerBlobGas", maxFeePerBlobGas));
        bindings.Add(Bind(transaction, "Transaction", "BlobVersionedHashes", blobVersionedHashes));
        bindings.Add(Bind(transaction, "Transaction", "GasLimit", gasLimit));
        bindings.Add(Bind(transaction, "Transaction", "Nonce", nonce));

        EnumDeclarationSyntax txTypeDeclaration = RequireSingleEnum(txType, "TxType");
        RequireCanonicalContains(Canonical(txTypeDeclaration), "publicenumTxType:byte{Legacy=0,AccessList=1,EIP1559=2,Blob=3,SetCode=4,DepositTx=0x7E,}",
            "TxType values or source width changed.");
        bindings.Add(Bind(txType, "TxType", "enum", txTypeDeclaration));

        MethodDeclarationSyntax supports1559Type = RequireMethod(txTypeExtensions, "TxTypeExtensions", "Supports1559", 1,
            "thisTxTypetxType");
        MethodDeclarationSyntax supportsBlobsType = RequireMethod(txTypeExtensions, "TxTypeExtensions", "SupportsBlobs", 1,
            "thisTxTypetxType");
        RequireCanonicalContains(Canonical(BodyOrExpression(supports1559Type)),
            "txType>=TxType.EIP1559&&txType!=TxType.DepositTx",
            "TxType.Supports1559 projection changed.");
        RequireCanonicalContains(Canonical(BodyOrExpression(supportsBlobsType)), "txType==TxType.Blob",
            "TxType.SupportsBlobs projection changed.");
        bindings.Add(Bind(txTypeExtensions, "TxTypeExtensions", "Supports1559", supports1559Type));
        bindings.Add(Bind(txTypeExtensions, "TxTypeExtensions", "SupportsBlobs", supportsBlobsType));

        MethodDeclarationSyntax getBlobCount = RequireMethod(transactionExtensions, "TransactionExtensions", "GetBlobCount", 0, "");
        RequireCanonicalContains(Canonical(BodyOrExpression(getBlobCount)), "tx.BlobVersionedHashes?.Length??0",
            "Transaction.GetBlobCount projection changed.");
        bindings.Add(Bind(transactionExtensions, "TransactionExtensions.TransactionAccessor", "GetBlobCount", getBlobCount));

        MethodDeclarationSyntax calculateBlobGas = RequireMethod(blobGasCalculator, "BlobGasCalculator", "CalculateBlobGas", 1,
            "Transactiontransaction");
        RequireCanonicalContains(Canonical(BodyOrExpression(calculateBlobGas)), "CalculateBlobGas(transaction.GetBlobCount())",
            "BlobGasCalculator.CalculateBlobGas(Transaction) projection changed.");
        MethodDeclarationSyntax calculateBlobGasInt = RequireMethod(blobGasCalculator, "BlobGasCalculator", "CalculateBlobGas", 1,
            "intblobCount");
        RequireCanonicalContains(Canonical(BodyOrExpression(calculateBlobGasInt)), "CalculateBlobGas((ulong)blobCount)",
            "BlobGasCalculator.CalculateBlobGas(int) cast changed.");
        MethodDeclarationSyntax calculateBlobGasUlong = RequireMethod(blobGasCalculator, "BlobGasCalculator", "CalculateBlobGas", 1,
            "ulongblobCount");
        RequireCanonicalContains(Canonical(BodyOrExpression(calculateBlobGasUlong)), "blobCount*Eip4844Constants.GasPerBlob",
            "BlobGasCalculator.CalculateBlobGas(ulong) formula changed.");
        FieldDeclarationSyntax gasPerBlob = RequireConstField(eip4844Constants, "Eip4844Constants", "GasPerBlob", "ulong", "131072");
        bindings.Add(Bind(blobGasCalculator, "BlobGasCalculator", "CalculateBlobGas", calculateBlobGas));
        bindings.Add(Bind(blobGasCalculator, "BlobGasCalculator", "CalculateBlobGas", calculateBlobGasInt));
        bindings.Add(Bind(blobGasCalculator, "BlobGasCalculator", "CalculateBlobGas", calculateBlobGasUlong));
        bindings.Add(Bind(eip4844Constants, "Eip4844Constants", "GasPerBlob", gasPerBlob));

        MethodDeclarationSyntax worldSetNonce = RequireMethod(worldState, "WorldState", "SetNonce", 2,
            "Addressaddress,inulongnonce");
        RequireCanonicalContains(Canonical(RequireBody(worldSetNonce)), "_stateProvider.SetNonce(address,nonce);",
            "WorldState.SetNonce must delegate to StateProvider.SetNonce.");
        MethodDeclarationSyntax providerSetNonce = RequireMethod(stateProvider, "StateProvider", "SetNonce", 2,
            "Addressaddress,inulongnonce");
        RequireCanonicalContains(Canonical(RequireBody(providerSetNonce)),
            "Accountaccount=GetThroughCache(address)??ThrowNullAccount(address);",
            "StateProvider.SetNonce must throw before writing an absent account.");
        bindings.Add(Bind(worldState, "WorldState", "SetNonce", worldSetNonce));
        bindings.Add(Bind(stateProvider, "StateProvider", "SetNonce", providerSetNonce));
    }

    /// <summary>
    /// Lowers only the supported, source-admitted AST subset. The schema IDs are a deliberately
    /// small grammar; every formula, width, effect, expression, owner, and adapter premise below
    /// is attached to an exact Roslyn node whose complete containing member is admission-pinned.
    /// </summary>
    private static SemanticLowering ExtractSemanticLowering(SourceFile[] sources, PrefixValidation prefix)
    {
        SourceFile transaction = FindSource(sources, TransactionPath);
        SourceFile options = FindSource(sources, OptionsPath);
        SourceFile txType = FindSource(sources, TxTypePath);
        SourceFile txTypeExtensions = FindSource(sources, TxTypeExtensionsPath);
        SourceFile extensions = FindSource(sources, TransactionExtensionsPath);
        SourceFile blobGas = FindSource(sources, BlobGasCalculatorPath);
        SourceFile eip4844Constants = FindSource(sources, Eip4844ConstantsPath);
        SourceFile worldState = FindSource(sources, WorldStatePath);
        SourceFile stateProvider = FindSource(sources, StateProviderPath);
        SourceFile processor = FindSource(sources, TransactionProcessorPath);

        PropertyDeclarationSyntax gasLimit = RequireProperty(transaction, "Transaction", "GasLimit");
        PropertyDeclarationSyntax nonce = RequireProperty(transaction, "Transaction", "Nonce");
        PropertyDeclarationSyntax maxFee = RequireProperty(transaction, "Transaction", "MaxFeePerBlobGas");
        PropertyDeclarationSyntax hashes = RequireProperty(transaction, "Transaction", "BlobVersionedHashes");
        PropertyDeclarationSyntax sender = RequireProperty(transaction, "Transaction", "SenderAddress");
        PropertyDeclarationSyntax supports1559 = RequireProperty(transaction, "Transaction", "Supports1559");
        PropertyDeclarationSyntax supportsBlobs = RequireProperty(transaction, "Transaction", "SupportsBlobs");
        EnumDeclarationSyntax executionOptions = RequireSingleEnum(options, "ExecutionOptions");
        MethodDeclarationSyntax extensionEffectivePrice = prefix.ExtensionEffectivePrice;
        MethodDeclarationSyntax premium = prefix.Premium;
        MethodDeclarationSyntax recovery = prefix.Recovery;
        MethodDeclarationSyntax buyGas = prefix.BuyGas;
        MethodDeclarationSyntax incrementNonce = prefix.IncrementNonce;
        MethodDeclarationSyntax execute = prefix.Execute;
        MethodDeclarationSyntax getBlobCount = RequireMethod(extensions, "TransactionExtensions", "GetBlobCount", 0, "");
        MethodDeclarationSyntax calculateBlobGas = RequireMethod(blobGas, "BlobGasCalculator", "CalculateBlobGas", 1, "Transactiontransaction");
        MethodDeclarationSyntax calculateBlobGasInt = RequireMethod(blobGas, "BlobGasCalculator", "CalculateBlobGas", 1, "intblobCount");
        MethodDeclarationSyntax calculateBlobGasUlong = RequireMethod(blobGas, "BlobGasCalculator", "CalculateBlobGas", 1, "ulongblobCount");
        FieldDeclarationSyntax gasPerBlob = RequireConstField(eip4844Constants, "Eip4844Constants", "GasPerBlob", "ulong", "131072");
        MethodDeclarationSyntax supports1559Type = RequireMethod(txTypeExtensions, "TxTypeExtensions", "Supports1559", 1, "thisTxTypetxType");
        MethodDeclarationSyntax supportsBlobsType = RequireMethod(txTypeExtensions, "TxTypeExtensions", "SupportsBlobs", 1, "thisTxTypetxType");
        MethodDeclarationSyntax worldCreate = RequireMethod(worldState, "WorldState", "CreateAccount", 3,
            "Addressaddress,inUInt256balance,inulongnonce=default");
        MethodDeclarationSyntax providerCreate = RequireMethod(stateProvider, "StateProvider", "CreateAccount", 3,
            "Addressaddress,inUInt256balance,inulongnonce=default");
        MethodDeclarationSyntax worldSetNonce = RequireMethod(worldState, "WorldState", "SetNonce", 2,
            "Addressaddress,inulongnonce");
        MethodDeclarationSyntax providerSetNonce = RequireMethod(stateProvider, "StateProvider", "SetNonce", 2,
            "Addressaddress,inulongnonce");

        InvocationExpressionSyntax checkedAdd = RequireInvocationWithArguments(
            buyGas, "AddOverflow", "UInt256", "balanceCheck,tx.ValueRef,outbalanceCheck");
        InvocationExpressionSyntax checkedMultiply = RequireInvocationWithArguments(
            buyGas, "MultiplyOverflow", "UInt256", "(UInt256)tx.GasLimit,effectiveGasPrice,outsenderReservedGasPayment");
        InvocationExpressionSyntax reset = RequireDirectInvocation(execute, "Reset", "WorldState", "resetBlockChanges:false");
        IfStatementSyntax recoveryOuterGate = RequireIf(recovery, "senderisnull||!WorldState.AccountExists");
        IfStatementSyntax recoveryCreateGate = RequireIf(recovery, "!commit||noValidation||effectiveGasPrice.IsZero");
        IfStatementSyntax combinedAdmissionGate = RequireIf(execute, "!(result=ValidateSender");
        VariableDeclaratorSyntax newNonce = RequireVariable(incrementNonce, "newNonce");

        SourceBinding gasLimitBinding = Bind(transaction, "Transaction", "GasLimit", gasLimit);
        SourceBinding maxFeeBinding = Bind(transaction, "Transaction", "MaxFeePerBlobGas", maxFee);
        SourceBinding optionsBinding = Bind(options, "ExecutionOptions", "enum", executionOptions);
        SourceBinding addBinding = Bind(processor, "TransactionProcessorBase<TGasPolicy>", "UInt256.AddOverflow", checkedAdd);
        SourceBinding multiplyBinding = Bind(processor, "TransactionProcessorBase<TGasPolicy>", "UInt256.MultiplyOverflow", checkedMultiply);
        SourceBinding effectivePriceBinding = Bind(extensions, "TransactionExtensions", "CalculateEffectiveGasPrice", BodyOrExpression(extensionEffectivePrice));
        SourceBinding premiumBinding = Bind(extensions, "TransactionExtensions", "TryCalculatePremiumPerGas", BodyOrExpression(premium));
        SourceBinding metricsBinding = Bind(processor, "TransactionProcessorBase<TGasPolicy>", "UpdateMetrics", RequireBody(prefix.UpdateMetrics));
        SourceBinding recoveryBinding = Bind(processor, "TransactionProcessorBase<TGasPolicy>", "RecoverSenderIfNeeded", RequireBody(recovery));
        SourceBinding buyGasBinding = Bind(processor, "TransactionProcessorBase<TGasPolicy>", "BuyGas", RequireBody(buyGas));
        SourceBinding incrementNonceBinding = Bind(processor, "TransactionProcessorBase<TGasPolicy>", "IncrementNonce", RequireBody(incrementNonce));
        SourceBinding executeBinding = Bind(processor, "TransactionProcessorBase<TGasPolicy>", "Execute", RequireBody(execute));
        SourceBinding gasPerBlobBinding = Bind(eip4844Constants, "Eip4844Constants", "GasPerBlob", gasPerBlob);
        SourceBinding blobGasBinding = Bind(blobGas, "BlobGasCalculator", "CalculateBlobGas", BodyOrExpression(calculateBlobGasUlong));

        return new SemanticLowering(
            [
                new("uint64", NumericWidth.UInt64, 64, Canonical(gasLimit), gasLimitBinding),
                new("uint256", NumericWidth.UInt256, 256, Canonical(maxFee), maxFeeBinding),
                new("executionOptions", NumericWidth.Int32, 32, Canonical(executionOptions), optionsBinding),
            ],
            [
                Operation("normalizeUInt64", 1, SemanticFormula.NormalizeUnsigned, [NumericWidth.UInt64], NumericWidth.UInt64,
                    [SemanticEffect.NormalizeInput], gasLimitBinding, gasLimit),
                Operation("normalizeUInt256", 2, SemanticFormula.NormalizeUnsigned, [NumericWidth.UInt256], NumericWidth.UInt256,
                    [SemanticEffect.NormalizeInput], maxFeeBinding, maxFee),
                Operation("checkedAdd256", 3, SemanticFormula.CheckedAdd, [NumericWidth.UInt256, NumericWidth.UInt256], NumericWidth.UInt256,
                    [SemanticEffect.PreserveWrappedOutValue], addBinding, checkedAdd),
                Operation("checkedMultiply256", 4, SemanticFormula.CheckedMultiply, [NumericWidth.UInt256, NumericWidth.UInt256], NumericWidth.UInt256,
                    [SemanticEffect.PreserveWrappedOutValue], multiplyBinding, checkedMultiply),
                Operation("effectiveGasPrice", 5, SemanticFormula.EffectiveGasPrice, [NumericWidth.UInt256, NumericWidth.UInt256], NumericWidth.UInt256,
                    [], effectivePriceBinding, BodyOrExpression(extensionEffectivePrice)),
                Operation("premiumPerGas", 6, SemanticFormula.PremiumPerGas, [NumericWidth.UInt256, NumericWidth.UInt256], NumericWidth.UInt256,
                    [], premiumBinding, BodyOrExpression(premium)),
                Operation("metricsGate", 7, MetricFormula(prefix.MetricsEqualityGate), [NumericWidth.Int32, NumericWidth.UInt256], NumericWidth.UInt256,
                    [SemanticEffect.AppendMetric], metricsBinding, RequireBody(prefix.UpdateMetrics)),
                Operation("recoveryDecision", 8, SemanticFormula.RecoveryDecision, [NumericWidth.Int32, NumericWidth.UInt256], NumericWidth.UInt256,
                    [], recoveryBinding, recoveryOuterGate.Condition),
                Operation("recoveryApplication", 9, SemanticFormula.RecoveryApplication, [NumericWidth.Int32, NumericWidth.UInt256], NumericWidth.UInt256,
                    [SemanticEffect.AppendRecoveryLog, SemanticEffect.ReplaceTransactionSender, SemanticEffect.CreateZeroAccount, SemanticEffect.PreserveThrowState],
                    recoveryBinding, recoveryCreateGate.Condition),
                Operation("feeReservation", 10, SemanticFormula.FeeReservation, [NumericWidth.UInt64, NumericWidth.UInt256], NumericWidth.UInt256,
                    [SemanticEffect.PreserveWrappedOutValue, SemanticEffect.DebitEffectiveSender],
                    buyGasBinding, checkedMultiply),
                Operation("nonceAdvance", 11, SemanticFormula.NonceAdvance, [NumericWidth.UInt64], NumericWidth.UInt64,
                    [SemanticEffect.AppendValidationLog, SemanticEffect.SetEffectiveSenderNonce],
                    incrementNonceBinding, newNonce.Initializer?.Value ?? throw new ExtractionException("IncrementNonce lost its source newNonce initializer.")),
                Operation("combinedFailureReset", 12, SemanticFormula.CombinedFailureReset, [NumericWidth.Int32], NumericWidth.Int32,
                    [SemanticEffect.ResetJournalAfterCombinedFailure],
                    Bind(FindSource(sources, TransactionProcessorPath), "TransactionProcessorBase<TGasPolicy>", "WorldState.Reset", reset), reset),
                Operation("admissionPrefix", 13, SemanticFormula.AdmissionPrefix, [], NumericWidth.UInt256,
                    [SemanticEffect.ContinueAfterPrefix],
                    executeBinding, combinedAdmissionGate.Condition),
                Operation("gasPerBlobConstant", 14, SemanticFormula.SourceConstant, [], NumericWidth.UInt64,
                    [SemanticEffect.NormalizeInput], gasPerBlobBinding,
                    gasPerBlob.Declaration.Variables[0].Initializer?.Value ?? throw new ExtractionException("GasPerBlob lost its source initializer.")),
                Operation("blobGas", 15, SemanticFormula.BlobGas, [NumericWidth.UInt64], NumericWidth.UInt64,
                    [SemanticEffect.NormalizeInput], blobGasBinding, BodyOrExpression(calculateBlobGasUlong)),
            ],
            [
                TrustedPremise("sourceGrammarSemantics", SourceGrammarAuditPath, SourceGrammarAuditMember,
                    AdapterKind.SourceGrammarSemanticsAssumption,
                    "the finite grammar-assigned identity/category tags agree with an error-free compiler-resolution audit of the reviewed source closure.",
                    "the extractor parses and structurally lowers syntax but does not resolve Roslyn ISymbol/ITypeSymbol bindings; the external audit is a blocking composition obligation.",
                    "sourceGrammarSemanticsCoherent"),
                Premise("transactionFields", TransactionPath, "Transaction", AdapterKind.TransactionProjection,
                    [Bind(transaction, "Transaction", "SenderAddress", sender), Bind(transaction, "Transaction", "GasLimit", gasLimit),
                     Bind(transaction, "Transaction", "Nonce", nonce), Bind(transaction, "Transaction", "MaxFeePerBlobGas", maxFee),
                     Bind(transaction, "Transaction", "BlobVersionedHashes", hashes)],
                    "sender/signature/message-call/fees/value are an input snapshot; MaxFeePerBlobGas and BlobVersionedHashes retain their source nullable shape.",
                    "object identity, transaction serialization, and malformed blob fields are excluded unless inputAdapterCoherent holds.",
                    "transactionSnapshotCoherent"),
                Premise("transactionType", TxTypePath, "TxType and TxTypeExtensions", AdapterKind.TransactionTypeProjection,
                    [Bind(transaction, "Transaction", "Supports1559", supports1559), Bind(transaction, "Transaction", "SupportsBlobs", supportsBlobs),
                     Bind(txType, "TxType", "enum", RequireSingleEnum(txType, "TxType")), Bind(txTypeExtensions, "TxTypeExtensions", "Supports1559", supports1559Type),
                     Bind(txTypeExtensions, "TxTypeExtensions", "SupportsBlobs", supportsBlobsType)],
                    "supports1559 and supportsBlobs are byte-backed TxType projections, not free booleans in the production corollary.",
                    "arbitrary transaction-type dispatch remains outside the model.",
                    "transactionTypeCoherent"),
                Premise("blobGas", BlobGasCalculatorPath, "BlobGasCalculator.CalculateBlobGas(Transaction)", AdapterKind.BlobProjection,
                    [Bind(extensions, "TransactionExtensions.TransactionAccessor", "GetBlobCount", getBlobCount), Bind(blobGas, "BlobGasCalculator", "CalculateBlobGas", calculateBlobGas),
                     Bind(blobGas, "BlobGasCalculator", "CalculateBlobGas", calculateBlobGasInt), Bind(blobGas, "BlobGasCalculator", "CalculateBlobGas", calculateBlobGasUlong),
                     gasPerBlobBinding],
                    "BlobVersionedHashes outer null maps to count zero; blob gas is source-derived as unchecked UInt64 blobCount * GasPerBlob before UInt256 arithmetic.",
                    "fee-per-blob and total-base-fee outcomes are typed oracle observations; the latter false path retains its out value.",
                    "blobProjectionCoherent"),
                Premise("worldAndRecovery", TransactionProcessorPath, "WorldState/Ecdsa/logging calls", AdapterKind.WorldStateProjection,
                    [Bind(FindSource(sources, TransactionProcessorPath), "TransactionProcessorBase<TGasPolicy>", "RecoverSenderIfNeeded", recovery),
                     Bind(worldState, "WorldState", "CreateAccount", worldCreate), Bind(stateProvider, "StateProvider", "CreateAccount", providerCreate),
                     Bind(worldState, "WorldState", "SetNonce", worldSetNonce), Bind(stateProvider, "StateProvider", "SetNonce", providerSetNonce)],
                    "world facts belong to the supplied or effective sender exactly as modeled; standard WorldState delegates CreateAccount to StateProvider, whose zero balance/nonce creates Account.TotallyEmpty.",
                    "ECDSA, account reads, invalid-code checks, logging, journal internals, and adapter exceptions are supplied observations; SetNonce absent-account failure is represented explicitly.",
                    "standardMainnetWorldStateCoherent"),
                Premise("headerSpecAndStatic", TransactionProcessorPath, "Execute/ValidateStatic/BuyGas", AdapterKind.HeaderAndSpecProjection,
                    [Bind(FindSource(sources, TransactionProcessorPath), "TransactionProcessorBase<TGasPolicy>", "Execute", execute),
                     Bind(FindSource(sources, TransactionProcessorPath), "TransactionProcessorBase<TGasPolicy>", "BuyGas", buyGas)],
                    "header base fee, spec flags, and blob-fee oracle inputs are a coherent read-only snapshot; static success is an explicit adapter premise.",
                    "ValidateStatic and blob-fee calculation implementation are not proven by this package; static success supplies a non-null sender and a gas limit of at least 21,000.",
                    "headerSpecAndStaticCoherent"),
                Premise("preIntrinsicRecovery", TransactionProcessorPath, "RecoverSenderBeforeIntrinsicGas", AdapterKind.RouteProjection,
                    [Bind(FindSource(sources, TransactionProcessorPath), "TransactionProcessorBase<TGasPolicy>", "Execute", execute),
                     Bind(FindSource(sources, TransactionProcessorPath), "TransactionProcessorBase<TGasPolicy>", "RecoverSenderBeforeIntrinsicGas",
                         RequireMethod(FindSource(sources, TransactionProcessorPath), "TransactionProcessorBase", "RecoverSenderBeforeIntrinsicGas", 2,
                             "Transactiontx,IReleaseSpecspec"))],
                    "preIntrinsicSender and preIntrinsicRecoveredSender are the observed pre-EIP-2780 state and recovery result.",
                    "ECDSA recovery and account-existence reads are adapter observations; their relation to the post-recovery transaction sender is explicit.",
                    "preIntrinsicEip2780Coherent"),
            ]);
    }

    private static SemanticOperation Operation(
        string id,
        int ordinal,
        SemanticFormula formula,
        NumericWidth[] inputWidths,
        NumericWidth outputWidth,
        SemanticEffect[] effects,
        SourceBinding binding,
        SyntaxNode expression)
    {
        SourceExpression expressionAst;
        try
        {
            expressionAst = LowerSourceSyntax(expression);
        }
        catch (ExtractionException exception)
        {
            throw new ExtractionException($"Semantic operation {id} could not lower its source tree: {exception.Message}");
        }
        if (!MatchesFormulaGrammar(formula, expressionAst))
        {
            throw new ExtractionException($"Semantic operation {id} is outside its admitted source expression grammar.");
        }

        return new SemanticOperation(id, ordinal, binding.Owner + "." + binding.Member, formula, inputWidths, outputWidth,
            effects, binding, Canonical(expression), expressionAst);
    }

    internal static bool MatchesFormulaGrammar(SemanticFormula formula, SourceExpression expression) => formula switch
    {
        SemanticFormula.NormalizeUnsigned => expression.Kind == SourceExpressionKind.Projection,
        SemanticFormula.CheckedAdd => IsInvocation(expression, "AddOverflow", "bool"),
        SemanticFormula.CheckedMultiply => IsInvocation(expression, "MultiplyOverflow", "bool"),
        SemanticFormula.EffectiveGasPrice => expression.Kind == SourceExpressionKind.Conditional,
        SemanticFormula.PremiumPerGas => expression.Kind == SourceExpressionKind.Block,
        SemanticFormula.MetricsCommitOrNone => DescribeMetricsGate(ExtractMetricsCondition(expression)) ==
            "opts is ExecutionOptions.Commit or ExecutionOptions.None",
        SemanticFormula.MetricsCommitOnly => DescribeMetricsGate(ExtractMetricsCondition(expression)) ==
            "opts is ExecutionOptions.Commit",
        SemanticFormula.RecoveryDecision or SemanticFormula.RecoveryApplication or SemanticFormula.AdmissionPrefix =>
            expression.Kind == SourceExpressionKind.Binary,
        SemanticFormula.FeeReservation => IsInvocation(expression, "MultiplyOverflow", "bool"),
        SemanticFormula.NonceAdvance => expression.Kind == SourceExpressionKind.Conditional,
        SemanticFormula.CombinedFailureReset => IsInvocation(expression, "Reset", "void"),
        SemanticFormula.SourceConstant => expression.Kind == SourceExpressionKind.Literal && expression.TypeName == "numeric",
        SemanticFormula.BlobGas => expression.Kind == SourceExpressionKind.Binary && expression.Symbol == "*" &&
            expression.Children.Length == 2 && expression.TypeName != "unresolved",
        _ => false,
    };

    private static bool IsInvocation(SourceExpression expression, string name, string typeName) =>
        expression.Kind == SourceExpressionKind.Invocation && expression.Symbol == name && expression.TypeName == typeName;

    private static AdapterPremise Premise(
        string id,
        string sourcePath,
        string sourceMember,
        AdapterKind kind,
        SourceBinding[] bindings,
        string projection,
        string assumption,
        string domainPredicate) =>
        new(id, sourcePath, sourceMember, projection, assumption, kind, bindings,
            bindings.Select(BindingProjection).ToArray(), domainPredicate);

    private static AdapterPremise TrustedPremise(
        string id,
        string sourcePath,
        string sourceMember,
        AdapterKind kind,
        string projection,
        string assumption,
        string domainPredicate) =>
        new(id, sourcePath, sourceMember, projection, assumption, kind, [], [], domainPredicate);

    private static SourceExpression BindingProjection(SourceBinding binding)
    {
        foreach (SyntaxNode root in ParseBoundSyntax(binding.SourceSyntax))
        {
            if (Canonical(root) == binding.CanonicalSyntax)
            {
                return LowerSourceSyntax(root);
            }
        }

        throw new ExtractionException("An adapter binding has no complete parseable source projection: " +
            binding.Path + "|" + binding.Owner + "|" + binding.Member + "|" + binding.Signature + ": " +
            binding.CanonicalSyntax + ".");
    }

    private static SemanticFormula MetricFormula(string metricsEqualityGate) => metricsEqualityGate switch
    {
        "opts is ExecutionOptions.Commit or ExecutionOptions.None" => SemanticFormula.MetricsCommitOrNone,
        "opts is ExecutionOptions.Commit" => SemanticFormula.MetricsCommitOnly,
        _ => throw new ExtractionException("UpdateMetrics uses an unlowered execution-options equality gate."),
    };

    private static BranchShape[] ExtractBranchShapes(
        SourceFile source,
        MethodDeclarationSyntax execute,
        MethodDeclarationSyntax recover,
        MethodDeclarationSyntax validateSender,
        MethodDeclarationSyntax buyGas,
        MethodDeclarationSyntax incrementNonce) =>
    [
        Branch(source, 1, RequireIf(execute, "!(result=ValidateStatic")),
        Branch(source, 2, RequireIf(recover, "!commit||noValidation||effectiveGasPrice.IsZero")),
        Branch(source, 3, RequireIfExact(recover, "senderisnull")),
        Branch(source, 4, RequireIf(validateSender, "validate&&!SkipSenderCodeCheck")),
        Branch(source, 5, RequireIf(buyGas, "validate&&!TryCalculatePremiumPerGas")),
        Branch(source, 6, RequireIf(buyGas, "MultiplyOverflow((UInt256)tx.GasLimit,effectiveGasPrice")),
        Branch(source, 7, RequireIf(buyGas, "MultiplyOverflow((UInt256)tx.GasLimit,tx.MaxFeePerGas")),
        Branch(source, 8, RequireIf(buyGas, "AddOverflow(balanceCheck,tx.ValueRef")),
        Branch(source, 9, RequireIf(buyGas, "MultiplyOverflow(blobGas,(UInt256)tx.MaxFeePerBlobGas!")),
        Branch(source, 10, RequireIf(buyGas, "!_blobBaseFeeCalculator.TryCalculateBlobFees")),
        Branch(source, 11, RequireIf(buyGas, "tx.MaxFeePerBlobGas<feePerBlobGas")),
        Branch(source, 12, RequireIf(buyGas, "AddOverflow(senderReservedGasPayment,blobBaseFee")),
        Branch(source, 13, RequireIf(buyGas, "opts.HasFlag(ExecutionOptions.Warmup)")),
        Branch(source, 14, RequireIf(buyGas, "balance<balanceCheck")),
        Branch(source, 15, RequireIf(buyGas, "!senderReservedGasPayment.IsZero")),
        Branch(source, 16, RequireIf(incrementNonce, "validate&&tx.Nonce!=nonce")),
        Branch(source, 17, RequireDirectInvocation(incrementNonce, "SetNonce", "WorldState", "senderAddress,newNonce")),
        Branch(source, 18, RequireIfExact(execute, "restore")),
        Branch(source, 19, RequireDirectInvocation(execute, "PrepareSimpleTransferFastPath", "", "tx,spec,outCodeInfo?preloadedCodeInfo,outAddress?preloadedDelegationAddress")),
    ];

    private static EffectShape[] ExtractEffects(
        SourceFile source,
        MethodDeclarationSyntax execute,
        MethodDeclarationSyntax updateMetrics,
        MethodDeclarationSyntax recover,
        MethodDeclarationSyntax validateSender,
        MethodDeclarationSyntax buyGas,
        MethodDeclarationSyntax incrementNonce) =>
    [
        Effect(source, "metrics", updateMetrics, ["raw ExecutionOptions", "effectiveGasPrice"], ["Metrics.UpdateBlockGasPrice"],
            "Persists after static success."),
        Effect(source, "senderRecovery", recover, ["tx sender", "account exists", "signature", "EIP-2780", "options", "price", "CreateAccount adapter result"],
            ["tx sender", "TraceLogInvalidTx", "CreateAccount", "deleteCallerAccount", "log"],
            "Persists through a recovery throw; standard-world CreateAccount is an admitted adapter boundary."),
        Effect(source, "senderValidation", validateSender, ["SkipValidation", "SkipSenderCodeCheck", "invalid contract sender"], ["log"],
            "No modeled world mutation."),
        Effect(source, "gasPurchase", buyGas, ["balance", "UInt256 fees", "blob oracle", "Warmup"],
            ["premium/reserved/blob out fields including wrapped checked-operation outputs", "SubtractFromBalance", "log"],
            "Warmup insufficient funds performs a partial debit."),
        Effect(source, "nonce", incrementNonce, ["SkipValidation", "tx nonce", "world nonce"], ["SetNonce", "log"],
            "Mismatch has no nonce write."),
        Effect(source, "combinedReset", execute, ["Restore", "combined gate result"], ["Reset(resetBlockChanges:false)"],
            "Never occurs after static return or recovery throw."),
    ];

    private static BranchShape Branch(
        SourceFile source,
        int ordinal,
        SyntaxNode evidence)
    {
        string id = BranchId(ordinal);
        SourceBinding binding = Bind(source, "TransactionProcessorBase<TGasPolicy>", id, evidence);
        BranchTerminalKind terminal = DeriveBranchTerminal(evidence);
        SemanticEffect[] effects = DeriveEffects(evidence, binding.ContainingMember);
        SourceExpression condition = LowerSourceSyntax(evidence is IfStatementSyntax branch ? branch.Condition : evidence);
        return new BranchShape(
            id,
            ordinal,
            binding.ContainingMember,
            Canonical(evidence),
            terminal.ToString(),
            effects.Select(static effect => effect.ToString()).ToArray(),
            binding,
            terminal,
            effects,
            condition);
    }

    private static string BranchId(int ordinal) => ordinal switch
    {
        1 => "validateStaticReturn",
        2 => "recoverSenderCreatesAccount",
        3 => "recoverSenderThrows",
        4 => "validateSenderContract",
        5 => "buyGasPremiumBelowBaseFee",
        6 => "buyGasReservedPaymentOverflow",
        7 => "buyGasMaximumFeeOverflow",
        8 => "buyGasValueOverflow",
        9 => "buyGasBlobMaximumFeeOverflow",
        10 => "buyGasBlobFeeCalculationOverflow",
        11 => "buyGasBlobFeeCapBelowBaseFee",
        12 => "buyGasBlobPaymentOverflow",
        13 => "buyGasInsufficientBalanceWarmup",
        14 => "buyGasInsufficientBalanceReturn",
        15 => "buyGasDebit",
        16 => "incrementNonceMismatch",
        17 => "incrementNonceSet",
        18 => "combinedAdmissionFailureRestore",
        19 => "continueAfterAdmissionPrefix",
        _ => throw new ExtractionException("The source branch ordinal is outside the admitted vocabulary."),
    };

    private static BranchTerminalKind DeriveBranchTerminal(SyntaxNode evidence)
    {
        if (evidence is InvocationExpressionSyntax invocation)
        {
            return InvocationName(invocation) switch
            {
                "Reset" => BranchTerminalKind.Reset,
                "SetNonce" or "PrepareSimpleTransferFastPath" => BranchTerminalKind.Continue,
                _ => throw new ExtractionException("An admitted direct branch action has no terminal lowering."),
            };
        }

        if (evidence is not IfStatementSyntax conditional)
        {
            throw new ExtractionException("An admitted branch has neither a conditional nor a direct action.");
        }

        if (conditional.Statement.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
            .Any(static invocation => InvocationName(invocation) == "ThrowInvalidDataException"))
        {
            return BranchTerminalKind.Throw;
        }

        if (conditional.Statement.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
            .Any(static invocation => InvocationName(invocation) == "Reset"))
        {
            return BranchTerminalKind.Reset;
        }

        ReturnStatementSyntax[] returns = conditional.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().ToArray();
        if (returns.Length == 0)
        {
            return BranchTerminalKind.Continue;
        }

        ReturnStatementSyntax terminalReturn = returns[^1];
        return terminalReturn.Expression is not null && Canonical(terminalReturn.Expression).Contains("TransactionResult.Ok", StringComparison.Ordinal)
            ? BranchTerminalKind.Continue
            : BranchTerminalKind.Return;
    }

    private static SemanticEffect[] DeriveEffects(SyntaxNode evidence, string containingMember)
    {
        HashSet<SemanticEffect> effects = [];
        foreach (SyntaxNode node in evidence.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case InvocationExpressionSyntax invocation:
                    AddInvocationEffects(effects, InvocationName(invocation), containingMember);
                    break;
                case AssignmentExpressionSyntax assignment when
                    Canonical(assignment.Left) == "tx.SenderAddress":
                    effects.Add(SemanticEffect.ReplaceTransactionSender);
                    break;
                case ArgumentSyntax { RefKindKeyword.ValueText: "out" } argument when
                    argument.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault() is { } checkedCall &&
                    InvocationName(checkedCall) is "AddOverflow" or "MultiplyOverflow":
                    effects.Add(SemanticEffect.PreserveWrappedOutValue);
                    break;
            }
        }

        return Enum.GetValues<SemanticEffect>().Where(effects.Contains).ToArray();
    }

    private static void AddInvocationEffects(HashSet<SemanticEffect> effects, string invocation, string containingMember)
    {
        switch (invocation)
        {
            case "UpdateBlockGasPrice":
                effects.Add(SemanticEffect.AppendMetric);
                break;
            case "TraceLogInvalidTx":
                effects.Add(containingMember.StartsWith("RecoverSenderIfNeeded", StringComparison.Ordinal)
                    ? SemanticEffect.AppendRecoveryLog
                    : SemanticEffect.AppendValidationLog);
                break;
            case "CreateAccount":
                effects.Add(SemanticEffect.CreateZeroAccount);
                break;
            case "SubtractFromBalance":
                effects.Add(SemanticEffect.DebitEffectiveSender);
                break;
            case "SetNonce":
                effects.Add(SemanticEffect.SetEffectiveSenderNonce);
                break;
            case "Reset":
                effects.Add(SemanticEffect.ResetJournalAfterCombinedFailure);
                break;
            case "PrepareSimpleTransferFastPath":
                effects.Add(SemanticEffect.ContinueAfterPrefix);
                break;
            case "ThrowInvalidDataException":
                effects.Add(SemanticEffect.PreserveThrowState);
                break;
        }
    }

    private static EffectShape Effect(
        SourceFile source,
        string id,
        MethodDeclarationSyntax evidence,
        string[] reads,
        string[] writes,
        string failureVisibility)
    {
        SourceBinding binding = Bind(source, "TransactionProcessorBase<TGasPolicy>", id, evidence);
        return new EffectShape(id, binding.ContainingMember, reads, writes, failureVisibility, binding,
            DeriveEffects(evidence, binding.ContainingMember));
    }

    private static IfStatementSyntax RequireIf(MethodDeclarationSyntax method, string canonicalConditionFragment)
    {
        IfStatementSyntax? found = null;
        foreach (IfStatementSyntax candidate in method.DescendantNodes().OfType<IfStatementSyntax>())
        {
            if (!Canonical(candidate.Condition).Contains(canonicalConditionFragment, StringComparison.Ordinal))
            {
                continue;
            }

            if (found is not null)
            {
                throw new ExtractionException($"Expected one admitted branch {canonicalConditionFragment} in {method.Identifier.ValueText}.");
            }

            found = candidate;
        }

        return found ?? throw new ExtractionException($"Missing admitted branch {canonicalConditionFragment} in {method.Identifier.ValueText}.");
    }

    private static IfStatementSyntax RequireIfExact(MethodDeclarationSyntax method, string canonicalCondition)
    {
        IfStatementSyntax? found = null;
        foreach (IfStatementSyntax candidate in method.DescendantNodes().OfType<IfStatementSyntax>())
        {
            if (Canonical(candidate.Condition) != canonicalCondition)
            {
                continue;
            }

            if (found is not null)
            {
                throw new ExtractionException($"Expected one exact admitted branch {canonicalCondition} in {method.Identifier.ValueText}.");
            }

            found = candidate;
        }

        return found ?? throw new ExtractionException($"Missing exact admitted branch {canonicalCondition} in {method.Identifier.ValueText}.");
    }

    private static VariableDeclaratorSyntax RequireVariable(MethodDeclarationSyntax method, string name)
    {
        VariableDeclaratorSyntax? found = null;
        foreach (VariableDeclaratorSyntax candidate in method.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            if (candidate.Identifier.ValueText != name || candidate.Initializer is null ||
                candidate.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() != method ||
                candidate.Ancestors().Any(static ancestor => ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
            {
                continue;
            }

            if (found is not null)
            {
                throw new ExtractionException($"Expected exactly one source local {name} in {method.Identifier.ValueText}.");
            }

            found = candidate;
        }

        return found ?? throw new ExtractionException($"Missing source local {name} in {method.Identifier.ValueText}.");
    }

    private static void EnsureNoEarlyReset(MethodDeclarationSyntax execute, int exclusiveEnd)
    {
        int count = 0;
        foreach (SyntaxNode node in execute.DescendantNodes())
        {
            if (node is InvocationExpressionSyntax invocation &&
                InvocationName(invocation) == "Reset" &&
                invocation.SpanStart < exclusiveEnd)
            {
                if (Canonical(invocation) != "WorldState.Reset(resetBlockChanges:false)")
                {
                    throw new ExtractionException("Unexpected Reset invocation appeared in the admitted prefix.");
                }

                count++;
            }
        }

        if (count != 1)
        {
            throw new ExtractionException("The admitted prefix must contain exactly one pre-fast-path Reset invocation.");
        }
    }

    private static MethodDeclarationSyntax RequireExecuteOverload(SourceFile source)
    {
        MethodDeclarationSyntax? found = null;
        foreach (SyntaxNode node in source.Root.DescendantNodes())
        {
            if (node is not MethodDeclarationSyntax method || method.Identifier.ValueText != "Execute" ||
                method.ParameterList.Parameters.Count != 6 ||
                !IsOwnedBy(method, "TransactionProcessorBase"))
            {
                continue;
            }

            if (!Canonical(method.ParameterList).Contains("inIntrinsicGas<TGasPolicy>intrinsicGas", StringComparison.Ordinal))
            {
                continue;
            }

            if (found is not null)
            {
                throw new ExtractionException("Expected exactly one admitted six-parameter Execute overload.");
            }

            found = method;
        }

        return found ?? throw new ExtractionException("Missing admitted Execute overload.");
    }

    private static MethodDeclarationSyntax RequireMethod(
        SourceFile source,
        string owner,
        string name,
        int parameterCount,
        string canonicalParameters)
    {
        MethodDeclarationSyntax? found = null;
        foreach (SyntaxNode node in source.Root.DescendantNodes())
        {
            if (node is not MethodDeclarationSyntax method || method.Identifier.ValueText != name ||
                method.ParameterList.Parameters.Count != parameterCount ||
                Canonical(method.ParameterList) != "(" + canonicalParameters + ")" ||
                !IsOwnedBy(method, owner))
            {
                continue;
            }

            if (found is not null)
            {
                throw new ExtractionException($"Expected exactly one {owner}.{name} overload in {source.RelativePath}.");
            }

            found = method;
        }

        return found ?? throw new ExtractionException($"Missing admitted {owner}.{name} overload in {source.RelativePath}.");
    }

    private static PropertyDeclarationSyntax RequireProperty(SourceFile source, string owner, string name)
    {
        PropertyDeclarationSyntax? found = null;
        foreach (SyntaxNode node in source.Root.DescendantNodes())
        {
            if (node is not PropertyDeclarationSyntax property || property.Identifier.ValueText != name ||
                !IsOwnedBy(property, owner))
            {
                continue;
            }

            if (found is not null)
            {
                throw new ExtractionException($"Expected exactly one {owner}.{name} property in {source.RelativePath}.");
            }

            found = property;
        }

        return found ?? throw new ExtractionException($"Missing {owner}.{name} property in {source.RelativePath}.");
    }

    private static FieldDeclarationSyntax RequireConstField(
        SourceFile source,
        string owner,
        string name,
        string canonicalType,
        string canonicalValue)
    {
        FieldDeclarationSyntax? found = null;
        foreach (FieldDeclarationSyntax field in source.Root.DescendantNodes().OfType<FieldDeclarationSyntax>())
        {
            if (!IsOwnedBy(field, owner) || !field.Modifiers.Any(static token => token.IsKind(SyntaxKind.ConstKeyword)) ||
                Canonical(field.Declaration.Type) != canonicalType || field.Declaration.Variables.Count != 1)
            {
                continue;
            }

            VariableDeclaratorSyntax variable = field.Declaration.Variables[0];
            if (variable.Identifier.ValueText != name || variable.Initializer is null ||
                Canonical(variable.Initializer.Value) != canonicalValue)
            {
                continue;
            }

            if (found is not null)
            {
                throw new ExtractionException($"Expected exactly one {owner}.{name} constant in {source.RelativePath}.");
            }

            found = field;
        }

        return found ?? throw new ExtractionException($"Missing admitted {owner}.{name} constant in {source.RelativePath}.");
    }

    /// <summary>
    /// Requires the direct containing type, except for C# extension-block members whose public
    /// owner is the enclosing extension container rather than Roslyn's extension-block node.
    /// This prevents a same-named nested module method from satisfying an outer-member binding.
    /// </summary>
    private static bool IsOwnedBy(SyntaxNode node, string owner)
    {
        TypeDeclarationSyntax[] owners = node.Ancestors().OfType<TypeDeclarationSyntax>().ToArray();
        if (owners.Length == 0)
        {
            return false;
        }

        if (owners[0].Identifier.ValueText == owner)
        {
            return true;
        }

        return owner == "TransactionExtensions" &&
            owners.Any(static type => type.Identifier.ValueText == "TransactionExtensions");
    }

    private static EnumDeclarationSyntax RequireSingleEnum(SourceFile source, string name)
    {
        EnumDeclarationSyntax? found = null;
        foreach (SyntaxNode node in source.Root.DescendantNodes())
        {
            if (node is EnumDeclarationSyntax declaration && declaration.Identifier.ValueText == name)
            {
                if (found is not null)
                {
                    throw new ExtractionException($"Expected exactly one {name} enum.");
                }

                found = declaration;
            }
        }

        return found ?? throw new ExtractionException($"Missing {name} enum.");
    }

    private static void RequireEnumValue(EnumDeclarationSyntax declaration, string name, string expected)
    {
        foreach (EnumMemberDeclarationSyntax member in declaration.Members)
        {
            if (member.Identifier.ValueText != name)
            {
                continue;
            }

            string actual = member.EqualsValue is null ? string.Empty : Canonical(member.EqualsValue.Value);
            if (actual != expected)
            {
                throw new ExtractionException($"ExecutionOptions.{name} changed from {expected} to {actual}.");
            }

            return;
        }

        throw new ExtractionException($"Missing ExecutionOptions.{name}.");
    }

    /// <summary>
    /// Finds one direct call in the specified admitted member. The receiver and argument token shape
    /// are part of the boundary so a same-named call in a nested callback cannot satisfy a route edge.
    /// </summary>
    private static InvocationExpressionSyntax RequireDirectInvocation(
        MethodDeclarationSyntax method,
        string name,
        string canonicalReceiver,
        string canonicalArguments)
    {
        InvocationExpressionSyntax? found = null;
        foreach (SyntaxNode node in method.DescendantNodes())
        {
            if (node is not InvocationExpressionSyntax invocation || InvocationName(invocation) != name ||
                InvocationReceiver(invocation) != canonicalReceiver ||
                Canonical(invocation.ArgumentList) != "(" + canonicalArguments + ")" ||
                invocation.Ancestors().Any(static ancestor => ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
            {
                continue;
            }

            if (invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() != method)
            {
                continue;
            }

            if (found is not null)
            {
                throw new ExtractionException($"Expected one direct {name} invocation in {method.Identifier.ValueText}.");
            }

            found = invocation;
        }

        return found ?? throw new ExtractionException($"Missing direct {name} invocation in {method.Identifier.ValueText}.");
    }

    private static InvocationExpressionSyntax RequireInvocationWithArguments(
        MethodDeclarationSyntax method,
        string name,
        string canonicalReceiver,
        string canonicalArguments)
    {
        InvocationExpressionSyntax? found = null;
        foreach (InvocationExpressionSyntax invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (InvocationName(invocation) != name || InvocationReceiver(invocation) != canonicalReceiver ||
                Canonical(invocation.ArgumentList) != "(" + canonicalArguments + ")" ||
                invocation.Ancestors().Any(static ancestor => ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax) ||
                invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() != method)
            {
                continue;
            }

            if (found is not null)
            {
                throw new ExtractionException($"Expected one admitted {name} argument shape in {method.Identifier.ValueText}.");
            }

            found = invocation;
        }

        return found ?? throw new ExtractionException($"Missing admitted {name} argument shape in {method.Identifier.ValueText}.");
    }

    /// <summary>Finds an exact top-level registration chained from the admitted Load builder.</summary>
    private static InvocationExpressionSyntax RequireRegistrationInvocation(
        SourceFile source,
        MethodDeclarationSyntax load,
        string canonicalTypeArguments)
    {
        InvocationExpressionSyntax? found = null;
        foreach (SyntaxNode node in load.DescendantNodes())
        {
            ExpressionStatementSyntax? statement = node.AncestorsAndSelf().OfType<ExpressionStatementSyntax>().FirstOrDefault();
            if (node is not InvocationExpressionSyntax invocation || InvocationName(invocation) != "AddScoped" ||
                !HasTypeArguments(invocation, canonicalTypeArguments) ||
                invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() != load ||
                invocation.Ancestors().Any(static ancestor => ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax) ||
                statement?.Parent != load.Body || !IsBuilderRegistrationChain(invocation, "builder"))
            {
                continue;
            }

            if (found is not null)
            {
                throw new ExtractionException($"Expected one admitted AddScoped<{canonicalTypeArguments}> registration in {source.RelativePath}.");
            }

            found = invocation;
        }

        return found ?? throw new ExtractionException($"Missing admitted AddScoped<{canonicalTypeArguments}> registration in {source.RelativePath}.");
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
            MemberBindingExpressionSyntax { Name: GenericNameSyntax generic } => generic,
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

    private static string InvocationName(InvocationExpressionSyntax invocation) =>
        invocation.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax member => member.Name.Identifier.ValueText,
            _ => string.Empty,
        };

    private static string InvocationReceiver(InvocationExpressionSyntax invocation) =>
        invocation.Expression switch
        {
            MemberAccessExpressionSyntax member => Canonical(member.Expression),
            MemberBindingExpressionSyntax => "?",
            _ => string.Empty,
        };

    private static BlockSyntax RequireBody(MethodDeclarationSyntax method) =>
        method.Body ?? throw new ExtractionException($"{method.Identifier.ValueText} must retain a block body.");

    private static SyntaxNode BodyOrExpression(MethodDeclarationSyntax method) =>
        method.ExpressionBody?.Expression ?? (SyntaxNode)RequireBody(method);

    private static StageAnchor Anchor(SourceFile source, string id, int ordinal, SyntaxNode node) =>
        new(id, ordinal, Bind(source, "TransactionProcessorBase<TGasPolicy>", id, node));

    private static SourceBinding Bind(SourceFile source, string owner, string member, SyntaxNode node)
    {
        FileLinePositionSpan span = node.GetLocation().GetLineSpan();
        string canonical = Canonical(node);
        string sourceSyntax = node.NormalizeWhitespace().ToFullString();
        return new SourceBinding(
            source.RelativePath,
            owner,
            member,
            SignatureOf(node),
            TokenFingerprint(node),
            Sha256(Encoding.UTF8.GetBytes(canonical)),
            canonical,
            sourceSyntax,
            ContainingMember(node),
            node is InvocationExpressionSyntax invocation ? InvocationReceiver(invocation) : string.Empty,
            StatementOrdinal(node),
            ControlFlowPath(node),
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            span.EndLinePosition.Line + 1,
            span.EndLinePosition.Character + 1);
    }

    /// <summary>
    /// Hashes every non-trivia token kind, length, and text. Unlike a source-file hash this is the
    /// reviewed full-member fingerprint; unlike a canonical string it cannot silently merge token
    /// boundaries such as <c>a b</c> and <c>ab</c>.
    /// </summary>
    private static string TokenFingerprint(SyntaxNode node)
    {
        StringBuilder builder = new();
        foreach (SyntaxToken token in node.DescendantTokens(descendIntoTrivia: false))
        {
            builder.Append(token.RawKind).Append(':').Append(token.Text.Length).Append(':')
                .Append(token.Text).Append('\n');
        }

        return Sha256(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static string ContainingMember(SyntaxNode node)
    {
        SyntaxNode? member = node.AncestorsAndSelf().FirstOrDefault(static candidate => candidate is MethodDeclarationSyntax or
            PropertyDeclarationSyntax or EnumDeclarationSyntax or FieldDeclarationSyntax);
        return member is null ? string.Empty : SignatureOf(member);
    }

    private static int StatementOrdinal(SyntaxNode node)
    {
        StatementSyntax? statement = node.AncestorsAndSelf().OfType<StatementSyntax>().LastOrDefault();
        if (statement is null)
        {
            return 0;
        }

        if (statement.Parent is not BlockSyntax block)
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

    private static string SignatureOf(SyntaxNode node) =>
        node switch
        {
            MethodDeclarationSyntax method => method.Identifier.ValueText + Canonical(method.ParameterList),
            EnumDeclarationSyntax declaration => declaration.Identifier.ValueText,
            PropertyDeclarationSyntax property => property.Identifier.ValueText + ":" + Canonical(property.Type),
            FieldDeclarationSyntax field when field.Declaration.Variables.Count == 1 =>
                field.Declaration.Variables[0].Identifier.ValueText + ":" + Canonical(field.Declaration.Type),
            _ => node.Kind().ToString(),
        };

    private static void RequireCanonicalContains(string actual, string expected, string message)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
        {
            throw new ExtractionException(message);
        }
    }

    private static void RejectErrors(SourceFile source)
    {
        foreach (Diagnostic diagnostic in source.Tree.GetDiagnostics())
        {
            if (diagnostic.Severity == DiagnosticSeverity.Error)
            {
                throw new ExtractionException($"Cannot extract {source.RelativePath}: {diagnostic.GetMessage()}.");
            }
        }
    }

    private static SourceFile Read(string root, string relativePath, string role)
    {
        string path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!IsChildOf(root, path))
        {
            throw new ExtractionException($"Source path escaped repository root: {relativePath}.");
        }

        if (!File.Exists(path))
        {
            throw new ExtractionException($"Missing {role} source: {relativePath}.");
        }

        byte[] bytes = File.ReadAllBytes(path);
        SourceText text = SourceText.From(bytes, bytes.Length, Encoding.UTF8, canBeEmbedded: true);
        SyntaxTree tree = CSharpSyntaxTree.ParseText(text, ParseOptions, relativePath);
        return new SourceFile(relativePath, role, Sha256(bytes), tree, tree.GetCompilationUnitRoot());
    }

    private static SourceFile FindSource(SourceFile[] sources, string path)
    {
        for (int index = 0; index < sources.Length; index++)
        {
            if (sources[index].RelativePath == path)
            {
                return sources[index];
            }
        }

        throw new ExtractionException($"Expected source was not loaded: {path}.");
    }

    private static byte[] Serialize<T>(T value)
    {
        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        string normalized = Encoding.UTF8.GetString(serialized)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .TrimEnd('\n') + "\n";
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(normalized);
    }

    private static IrDocument DeserializeIr(byte[] bytes)
    {
        try
        {
            IrDocument document = JsonSerializer.Deserialize<IrDocument>(bytes, JsonOptions)
                ?? throw new ExtractionException("The serialized stateful-admission IR was empty.");
            ValidateIr(document);
            return document;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"The serialized stateful-admission IR is not valid JSON: {exception.Message}");
        }
    }

    private static Manifest DeserializeManifest(byte[] bytes)
    {
        try
        {
            Manifest manifest = JsonSerializer.Deserialize<Manifest>(bytes, JsonOptions)
                ?? throw new ExtractionException("The serialized stateful-admission manifest was empty.");
            ValidateManifest(manifest);
            return manifest;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"The serialized stateful-admission manifest is not valid JSON: {exception.Message}");
        }
    }

    private static void ValidateIr(IrDocument document)
    {
        if (document.SchemaVersion != SchemaVersion ||
            document.ExtractorVersion != ExtractorVersion ||
            document.Kernel != Kernel ||
            document.AcceptanceState != AcceptanceState)
        {
            throw new ExtractionException("The stateful-admission IR header changed.");
        }

        RequireSha256(document.AdmissionClosureSha256, "IR reviewed admission closure SHA-256");

        if (document.Route is null || document.Options is null || document.Sources is null ||
            document.Stages is null || document.Branches is null || document.Effects is null || document.Semantics is null ||
            document.ArithmeticRules is null || document.ExternalObligations is null)
        {
            throw new ExtractionException("The stateful-admission IR contains a null collection.");
        }

        if (document.Sources.Length != 15 || document.Stages.Length != ExpectedStageIds.Length ||
            document.Branches.Length != ExpectedBranchIds.Length || document.Effects.Length != 6)
        {
            throw new ExtractionException("The stateful-admission IR cardinality changed.");
        }

        for (int index = 0; index < document.Stages.Length; index++)
        {
            StageAnchor? stage = document.Stages[index];
            if (stage is null || stage.Id != ExpectedStageIds[index] || stage.Ordinal != index + 1)
            {
                throw new ExtractionException("The source-bound admission stage order changed.");
            }

            ValidateBinding(stage.Binding);
        }

        for (int index = 0; index < document.Branches.Length; index++)
        {
            BranchShape? branch = document.Branches[index];
            if (branch is null || branch.Id != ExpectedBranchIds[index] || branch.Ordinal != index + 1 ||
                branch.TerminalKind != ExpectedBranchTerminalKinds[index] || branch.Terminal != branch.TerminalKind.ToString() ||
                branch.PartialEffects is null || branch.Effects is null || string.IsNullOrWhiteSpace(branch.Condition) ||
                string.IsNullOrWhiteSpace(branch.Terminal) ||
                !SourceDerivedBranchMatches(branch) ||
                !SourceExpressionMatchesBinding(branch.Binding, branch.ConditionAst.Normalized, branch.ConditionAst))
            {
                throw new ExtractionException("The source-bound branch order or effects changed: " + (branch?.Id ?? "null") + ".");
            }

            if (branch.PartialEffects.Length != branch.Effects.Length ||
                !branch.PartialEffects.SequenceEqual(branch.Effects.Select(static effect => effect.ToString())))
            {
                throw new ExtractionException("A branch effect no longer matches its typed source lowering.");
            }

            ValidateBinding(branch.Binding);
            ValidateSourceExpression(branch.ConditionAst);
        }

        for (int index = 0; index < document.Effects.Length; index++)
        {
            EffectShape? effect = document.Effects[index];
            if (effect is null || effect.Reads is null || effect.Writes is null || effect.Effects is null ||
                string.IsNullOrWhiteSpace(effect.Id) || string.IsNullOrWhiteSpace(effect.Member) ||
                string.IsNullOrWhiteSpace(effect.FailureVisibility))
            {
                throw new ExtractionException("A source-bound effect is incomplete.");
            }

            ValidateBinding(effect.Binding);
        }

        if (document.Options.None != 0 || document.Options.Commit != 1 || document.Options.Restore != 2 ||
            document.Options.SkipValidation != 4 || document.Options.Warmup != 8 || document.Options.BuildUp != 16 ||
            document.Options.MetricsEqualityGate is not ("opts is ExecutionOptions.Commit or ExecutionOptions.None" or
                "opts is ExecutionOptions.Commit"))
        {
            throw new ExtractionException("The source-bound execution option shape changed.");
        }

        if (document.Route.Registration !=
                "BlockProcessingModule.AddScoped<ITransactionProcessor, EthereumTransactionProcessor>" ||
            document.Route.BlobBaseFeeCalculatorRegistration !=
                "BlockProcessingModule.AddScoped<ITransactionProcessor.IBlobBaseFeeCalculator, BlobBaseFeeCalculator>" ||
            document.Route.WorldStateRegistration !=
                "BlockProcessingModule.AddScoped<IWorldState, WorldState>" ||
            document.Route.ClosedGenericBase != "TransactionProcessorBase<EthereumGasPolicy>" ||
            document.Route.ExcludedOverrides is null || document.Route.ExcludedOverrides.Length != 4 ||
            document.Route.Reachability is null || document.Route.Reachability.Length != 3 ||
            document.Route.Bindings is null || document.Route.Bindings.Length != 10)
        {
            throw new ExtractionException("The standard route shape changed.");
        }

        foreach (SourceBinding binding in document.Route.Bindings)
        {
            ValidateBinding(binding);
        }

        ValidateSemanticLowering(document.Semantics);

        for (int index = 0; index < document.Sources.Length; index++)
        {
            SourceIdentity? source = document.Sources[index];
            if (source is null || string.IsNullOrWhiteSpace(source.Path) || string.IsNullOrWhiteSpace(source.Role))
            {
                throw new ExtractionException("The IR has an empty source identity.");
            }

            RequireSha256(source.Sha256, "IR source SHA-256");
        }
    }

    internal static bool SourceDerivedBranchMatches(BranchShape branch)
    {
        SyntaxNode evidence;
        if (branch.Binding.CanonicalSyntax.StartsWith("if", StringComparison.Ordinal))
        {
            StatementSyntax statement = SyntaxFactory.ParseStatement(branch.Binding.SourceSyntax, options: ParseOptions);
            if (statement.ContainsDiagnostics || statement is not IfStatementSyntax ||
                Canonical(statement) != branch.Binding.CanonicalSyntax)
            {
                return false;
            }

            evidence = statement;
        }
        else
        {
            ExpressionSyntax expression = SyntaxFactory.ParseExpression(branch.Binding.SourceSyntax, options: ParseOptions);
            if (expression.ContainsDiagnostics || expression is not InvocationExpressionSyntax ||
                Canonical(expression) != branch.Binding.CanonicalSyntax)
            {
                return false;
            }

            evidence = expression;
        }

        return DeriveBranchTerminal(evidence) == branch.TerminalKind &&
            DeriveEffects(evidence, branch.Member).SequenceEqual(branch.Effects);
    }

    /// <summary>
    /// Rebuilds a typed expression tree from the exact bound source node and compares it
    /// structurally. This makes a coordinated JSON edit to <c>Symbol</c>, <c>SymbolId</c>, a
    /// type category, or an appended conjunct fail before Lean emission.
    /// </summary>
    internal static bool SourceExpressionMatchesBinding(
        SourceBinding binding,
        string normalized,
        SourceExpression candidate)
    {
        if (binding is null || string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        foreach (SyntaxNode root in ParseBoundSyntax(binding.SourceSyntax))
        {
            SyntaxNode[] matches = root.DescendantNodesAndSelf()
                .Where(node => Canonical(node) == normalized)
                .ToArray();
            if (matches.Length == 1 && SourceExpressionsEqual(LowerSourceSyntax(matches[0]), candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<SyntaxNode> ParseBoundSyntax(string sourceSyntax)
    {
        ExpressionSyntax expression = SyntaxFactory.ParseExpression(sourceSyntax, options: ParseOptions);
        if (!expression.ContainsDiagnostics)
        {
            yield return expression;
        }

        StatementSyntax statement = SyntaxFactory.ParseStatement(sourceSyntax, options: ParseOptions);
        if (!statement.ContainsDiagnostics)
        {
            yield return statement;
        }

        MemberDeclarationSyntax? member = SyntaxFactory.ParseMemberDeclaration(sourceSyntax, options: ParseOptions);
        if (member is not null && !member.ContainsDiagnostics)
        {
            yield return member;
        }

        CompilationUnitSyntax wrappedMember = SyntaxFactory.ParseCompilationUnit("class C{" + sourceSyntax + "}", options: ParseOptions);
        if (!wrappedMember.ContainsDiagnostics)
        {
            foreach (MemberDeclarationSyntax candidate in wrappedMember.DescendantNodes().OfType<MemberDeclarationSyntax>())
            {
                yield return candidate;
            }
        }

        CompilationUnitSyntax wrappedBlock = SyntaxFactory.ParseCompilationUnit("class C{void M()" + sourceSyntax + "}", options: ParseOptions);
        if (!wrappedBlock.ContainsDiagnostics)
        {
            foreach (BlockSyntax block in wrappedBlock.DescendantNodes().OfType<BlockSyntax>())
            {
                yield return block;
            }
        }
    }

    private static bool SourceExpressionsEqual(SourceExpression left, SourceExpression right)
    {
        if (left.Kind != right.Kind || left.Normalized != right.Normalized || left.Symbol != right.Symbol ||
            left.SymbolId != right.SymbolId || left.TypeName != right.TypeName ||
            left.Children.Length != right.Children.Length)
        {
            return false;
        }

        for (int index = 0; index < left.Children.Length; index++)
        {
            if (!SourceExpressionsEqual(left.Children[index], right.Children[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateManifest(Manifest manifest)
    {
        if (manifest.SchemaVersion != SchemaVersion ||
            manifest.ExtractorVersion != ExtractorVersion ||
            manifest.Kernel != Kernel ||
            manifest.AcceptanceState != AcceptanceState ||
            manifest.LanguageVersion != LanguageVersion.CSharp14.ToDisplayString())
        {
            throw new ExtractionException("The stateful-admission manifest header changed.");
        }

        RequireSha256(manifest.AdmissionClosureSha256, "Manifest reviewed admission closure SHA-256");

        if (manifest.Sources is null || manifest.Bindings is null || manifest.Ir is null || manifest.Lean is null ||
            manifest.Sources.Length != 15 || manifest.Bindings.Length < RequiredCompleteBindingKeys.Length)
        {
            throw new ExtractionException("The manifest source or binding shape changed.");
        }

        for (int index = 0; index < manifest.Sources.Length; index++)
        {
            SourceIdentity? source = manifest.Sources[index];
            if (source is null || string.IsNullOrWhiteSpace(source.Path) || string.IsNullOrWhiteSpace(source.Role))
            {
                throw new ExtractionException("The manifest contains a null source identity.");
            }

            RequireSha256(source.Sha256, "Manifest source SHA-256");
        }

        for (int index = 0; index < manifest.Bindings.Length; index++)
        {
            ValidateBinding(manifest.Bindings[index]);
        }

        if (manifest.Ir.Path != IrFileName || manifest.Lean.Path != Normalize(DefaultLeanPath))
        {
            throw new ExtractionException("The manifest artifact path changed.");
        }

        RequireSha256(manifest.Ir.Sha256, "Manifest IR SHA-256");
        RequireSha256(manifest.Lean.Sha256, "Manifest Lean SHA-256");
        RequireSha256(manifest.CombinedSourceSha256, "Manifest combined source SHA-256");
        RequireSha256(manifest.SemanticIrSha256, "Manifest semantic IR SHA-256");
        if (manifest.CombinedSourceSha256 != CombinedHash(manifest.Sources))
        {
            throw new ExtractionException("The combined source SHA-256 changed.");
        }
    }

    private static void ValidateBinding(SourceBinding? binding)
    {
        if (binding is null || string.IsNullOrWhiteSpace(binding.Path) || string.IsNullOrWhiteSpace(binding.Owner) ||
            string.IsNullOrWhiteSpace(binding.Member) || string.IsNullOrWhiteSpace(binding.Signature) ||
            string.IsNullOrWhiteSpace(binding.CanonicalSyntax) || string.IsNullOrWhiteSpace(binding.SourceSyntax) ||
            string.IsNullOrWhiteSpace(binding.ContainingMember) ||
            binding.Receiver is null || binding.ControlFlowPath is null || binding.StatementOrdinal < 0 ||
            binding.StartLine <= 0 || binding.StartColumn <= 0 ||
            binding.EndLine <= 0 || binding.EndColumn <= 0)
        {
            throw new ExtractionException("A source binding is incomplete.");
        }

        RequireSha256(binding.TokenSha256, "Source binding token SHA-256");
        RequireSha256(binding.CanonicalSyntaxSha256, "Source binding canonical syntax SHA-256");
        if (binding.CanonicalSyntaxSha256 != Sha256(Encoding.UTF8.GetBytes(binding.CanonicalSyntax)))
        {
            throw new ExtractionException("A source binding canonical syntax SHA-256 does not match syntax.");
        }
    }

    private static void ValidateSemanticLowering(SemanticLowering semantics)
    {
        if (semantics.Widths is null || semantics.Operations is null || semantics.AdapterPremises is null ||
            semantics.Widths.Length != 3 || semantics.Operations.Length != 15 || semantics.AdapterPremises.Length != 7)
        {
            throw new ExtractionException("The typed semantic lowering is incomplete.");
        }

        (string Id, NumericWidth Width, int Bits)[] expectedWidths =
        [
            ("uint64", NumericWidth.UInt64, 64),
            ("uint256", NumericWidth.UInt256, 256),
            ("executionOptions", NumericWidth.Int32, 32),
        ];
        for (int index = 0; index < expectedWidths.Length; index++)
        {
            WidthShape? width = semantics.Widths[index];
            (string id, NumericWidth numericWidth, int bits) = expectedWidths[index];
            if (width is null || width.Id != id || width.Width != numericWidth || width.Bits != bits ||
                string.IsNullOrWhiteSpace(width.SourceDefinition))
            {
                throw new ExtractionException("A typed source-width rule changed.");
            }

            ValidateBinding(width.Binding);
        }

        (string Id, SemanticFormula Formula, NumericWidth[] Inputs, NumericWidth Output, SemanticEffect[] Effects)[] expectedOperations =
        [
            ("normalizeUInt64", SemanticFormula.NormalizeUnsigned, [NumericWidth.UInt64], NumericWidth.UInt64, [SemanticEffect.NormalizeInput]),
            ("normalizeUInt256", SemanticFormula.NormalizeUnsigned, [NumericWidth.UInt256], NumericWidth.UInt256, [SemanticEffect.NormalizeInput]),
            ("checkedAdd256", SemanticFormula.CheckedAdd, [NumericWidth.UInt256, NumericWidth.UInt256], NumericWidth.UInt256, [SemanticEffect.PreserveWrappedOutValue]),
            ("checkedMultiply256", SemanticFormula.CheckedMultiply, [NumericWidth.UInt256, NumericWidth.UInt256], NumericWidth.UInt256, [SemanticEffect.PreserveWrappedOutValue]),
            ("effectiveGasPrice", SemanticFormula.EffectiveGasPrice, [NumericWidth.UInt256, NumericWidth.UInt256], NumericWidth.UInt256, []),
            ("premiumPerGas", SemanticFormula.PremiumPerGas, [NumericWidth.UInt256, NumericWidth.UInt256], NumericWidth.UInt256, []),
            ("metricsGate", SemanticFormula.MetricsCommitOrNone, [NumericWidth.Int32, NumericWidth.UInt256], NumericWidth.UInt256, [SemanticEffect.AppendMetric]),
            ("recoveryDecision", SemanticFormula.RecoveryDecision, [NumericWidth.Int32, NumericWidth.UInt256], NumericWidth.UInt256, []),
            ("recoveryApplication", SemanticFormula.RecoveryApplication, [NumericWidth.Int32, NumericWidth.UInt256], NumericWidth.UInt256,
                [SemanticEffect.AppendRecoveryLog, SemanticEffect.ReplaceTransactionSender,
                 SemanticEffect.CreateZeroAccount, SemanticEffect.PreserveThrowState]),
            ("feeReservation", SemanticFormula.FeeReservation, [NumericWidth.UInt64, NumericWidth.UInt256], NumericWidth.UInt256,
                [SemanticEffect.PreserveWrappedOutValue, SemanticEffect.DebitEffectiveSender]),
            ("nonceAdvance", SemanticFormula.NonceAdvance, [NumericWidth.UInt64], NumericWidth.UInt64,
                [SemanticEffect.AppendValidationLog, SemanticEffect.SetEffectiveSenderNonce]),
            ("combinedFailureReset", SemanticFormula.CombinedFailureReset, [NumericWidth.Int32], NumericWidth.Int32,
                [SemanticEffect.ResetJournalAfterCombinedFailure]),
            ("admissionPrefix", SemanticFormula.AdmissionPrefix, [], NumericWidth.UInt256,
                [SemanticEffect.ContinueAfterPrefix]),
            ("gasPerBlobConstant", SemanticFormula.SourceConstant, [], NumericWidth.UInt64,
                [SemanticEffect.NormalizeInput]),
            ("blobGas", SemanticFormula.BlobGas, [NumericWidth.UInt64], NumericWidth.UInt64,
                [SemanticEffect.NormalizeInput]),
        ];
        for (int index = 0; index < expectedOperations.Length; index++)
        {
            SemanticOperation? operation = semantics.Operations[index];
            (string id, SemanticFormula formula, NumericWidth[] inputs, NumericWidth output, SemanticEffect[] effects) = expectedOperations[index];
            bool acceptedFormula = operation is not null &&
                (id != "metricsGate" ? operation.Formula == formula :
                    operation.Formula is SemanticFormula.MetricsCommitOrNone or SemanticFormula.MetricsCommitOnly);
            if (operation is null || operation.Id != id || operation.Ordinal != index + 1 ||
                !acceptedFormula || operation.OutputWidth != output ||
                string.IsNullOrWhiteSpace(operation.SourceMember) || operation.InputWidths is null ||
                !operation.InputWidths.SequenceEqual(inputs) ||
                operation.Effects is null || !operation.Effects.SequenceEqual(effects) ||
                string.IsNullOrWhiteSpace(operation.Expression) || operation.ExpressionAst is null ||
                operation.Expression != operation.ExpressionAst.Normalized ||
                !SourceExpressionMatchesBinding(operation.Binding, operation.Expression, operation.ExpressionAst) ||
                !MatchesFormulaGrammar(operation.Formula, operation.ExpressionAst))
            {
                throw new ExtractionException("A typed semantic operation, formula, width, or effect changed.");
            }

            ValidateBinding(operation.Binding);
            ValidateSourceExpression(operation.ExpressionAst);
        }

        (string Id, string Path, string Member, AdapterKind Kind, string Predicate, string Projection, string Assumption)[] expectedPremises =
        [
            ("sourceGrammarSemantics", SourceGrammarAuditPath, SourceGrammarAuditMember, AdapterKind.SourceGrammarSemanticsAssumption,
                "sourceGrammarSemanticsCoherent",
                "the finite grammar-assigned identity/category tags agree with an error-free compiler-resolution audit of the reviewed source closure.",
                "the extractor parses and structurally lowers syntax but does not resolve Roslyn ISymbol/ITypeSymbol bindings; the external audit is a blocking composition obligation."),
            ("transactionFields", TransactionPath, "Transaction", AdapterKind.TransactionProjection, "transactionSnapshotCoherent",
                "sender/signature/message-call/fees/value are an input snapshot; MaxFeePerBlobGas and BlobVersionedHashes retain their source nullable shape.",
                "object identity, transaction serialization, and malformed blob fields are excluded unless inputAdapterCoherent holds."),
            ("transactionType", TxTypePath, "TxType and TxTypeExtensions", AdapterKind.TransactionTypeProjection, "transactionTypeCoherent",
                "supports1559 and supportsBlobs are byte-backed TxType projections, not free booleans in the production corollary.",
                "arbitrary transaction-type dispatch remains outside the model."),
            ("blobGas", BlobGasCalculatorPath, "BlobGasCalculator.CalculateBlobGas(Transaction)", AdapterKind.BlobProjection, "blobProjectionCoherent",
                "BlobVersionedHashes outer null maps to count zero; blob gas is source-derived as unchecked UInt64 blobCount * GasPerBlob before UInt256 arithmetic.",
                "fee-per-blob and total-base-fee outcomes are typed oracle observations; the latter false path retains its out value."),
            ("worldAndRecovery", TransactionProcessorPath, "WorldState/Ecdsa/logging calls", AdapterKind.WorldStateProjection, "standardMainnetWorldStateCoherent",
                "world facts belong to the supplied or effective sender exactly as modeled; standard WorldState delegates CreateAccount to StateProvider, whose zero balance/nonce creates Account.TotallyEmpty.",
                "ECDSA, account reads, invalid-code checks, logging, journal internals, and adapter exceptions are supplied observations; SetNonce absent-account failure is represented explicitly."),
            ("headerSpecAndStatic", TransactionProcessorPath, "Execute/ValidateStatic/BuyGas", AdapterKind.HeaderAndSpecProjection, "headerSpecAndStaticCoherent",
                "header base fee, spec flags, and blob-fee oracle inputs are a coherent read-only snapshot; static success is an explicit adapter premise.",
                "ValidateStatic and blob-fee calculation implementation are not proven by this package; static success supplies a non-null sender and a gas limit of at least 21,000."),
            ("preIntrinsicRecovery", TransactionProcessorPath, "RecoverSenderBeforeIntrinsicGas", AdapterKind.RouteProjection, "preIntrinsicEip2780Coherent",
                "preIntrinsicSender and preIntrinsicRecoveredSender are the observed pre-EIP-2780 state and recovery result.",
                "ECDSA recovery and account-existence reads are adapter observations; their relation to the post-recovery transaction sender is explicit."),
        ];
        for (int index = 0; index < expectedPremises.Length; index++)
        {
            AdapterPremise? premise = semantics.AdapterPremises[index];
            (string id, string path, string member, AdapterKind kind, string predicate, string projection, string assumption) = expectedPremises[index];
            bool isGrammarAssumption = kind == AdapterKind.SourceGrammarSemanticsAssumption;
            if (premise is null || premise.Id != id || premise.SourcePath != path || premise.SourceMember != member ||
                premise.Kind != kind || premise.DomainPredicate != predicate || premise.Projection != projection || premise.Assumption != assumption ||
                string.IsNullOrWhiteSpace(premise.SourceMember) || string.IsNullOrWhiteSpace(premise.Projection) ||
                string.IsNullOrWhiteSpace(premise.Assumption) || string.IsNullOrWhiteSpace(premise.DomainPredicate) ||
                premise.Bindings is null || premise.ProjectionAsts is null ||
                (isGrammarAssumption
                    ? premise.Bindings.Length != 0 || premise.ProjectionAsts.Length != 0
                    : premise.Bindings.Length == 0 || premise.Bindings.Length != premise.ProjectionAsts.Length))
            {
                throw new ExtractionException("A typed adapter premise changed.");
            }

            if (isGrammarAssumption)
            {
                continue;
            }

            for (int bindingIndex = 0; bindingIndex < premise.Bindings.Length; bindingIndex++)
            {
                SourceBinding binding = premise.Bindings[bindingIndex];
                ValidateBinding(binding);
                SourceExpression projectionAst = premise.ProjectionAsts[bindingIndex];
                if (!SourceExpressionMatchesBinding(binding, projectionAst.Normalized, projectionAst))
                {
                    throw new ExtractionException("An adapter projection no longer matches its source binding.");
                }

                ValidateSourceExpression(projectionAst);
            }
        }
    }

    private static void AdmitCompleteBindings(List<SourceBinding> bindings, AdmissionClosure admission)
    {
        string[] admittedCandidateKeys = bindings.Select(BindingKey)
            .Where(key => RequiredCompleteBindingKeys.Contains(key, StringComparer.Ordinal)).ToArray();
        if (admittedCandidateKeys.Length != RequiredCompleteBindingKeys.Length ||
            admittedCandidateKeys.Distinct(StringComparer.Ordinal).Count() != RequiredCompleteBindingKeys.Length)
        {
            string[] actualKeys = bindings.Select(BindingKey).Distinct(StringComparer.Ordinal).ToArray();
            string[] missingKeys = RequiredCompleteBindingKeys.Except(actualKeys, StringComparer.Ordinal).ToArray();
            string[] duplicateKeys = admittedCandidateKeys.GroupBy(static key => key, StringComparer.Ordinal)
                .Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
            throw new ExtractionException("The complete source-admission binding set is incomplete or duplicated. Missing:\n" +
                string.Join('\n', missingKeys) + "\nDuplicated:\n" + string.Join('\n', duplicateKeys));
        }

        Dictionary<string, AdmissionMember> admitted = new(StringComparer.Ordinal);
        foreach (AdmissionMember member in admission.Members)
        {
            if (!admitted.TryAdd(member.Key, member))
            {
                throw new ExtractionException($"The reviewed admission closure duplicates {member.Key}.");
            }
        }

        List<string> missing = [];
        foreach (string required in RequiredCompleteBindingKeys)
        {
            SourceBinding binding = bindings.SingleOrDefault(binding => BindingKey(binding) == required)
                ?? throw new ExtractionException($"Missing complete source-admission binding: {required}.");
            if (!admitted.TryGetValue(required, out AdmissionMember? member) || member.TokenSha256 != binding.TokenSha256)
            {
                missing.Add("member|" + binding.Path + "|" + binding.Owner + "|" + binding.Member + "|" +
                    binding.Signature + "|" + binding.TokenSha256);
            }
        }

        string[] extras = admitted.Keys.Except(RequiredCompleteBindingKeys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (extras.Length > 0)
        {
            missing.AddRange(extras.Select(static extra => "unexpected member closure key|" + extra));
        }

        if (missing.Count > 0)
        {
            throw new ExtractionException("A complete modeled source member changed; deliberately review and update " +
                AdmissionClosurePath + " before regeneration. Actual identities:\n" + string.Join("\n", missing));
        }
    }

    private static string BindingKey(SourceBinding binding) =>
        binding.Path + "|" + binding.Owner + "|" + binding.Member + "|" + binding.Signature;

    private static AdmissionClosure ReadAdmissionClosure(string root)
    {
        string path = Path.GetFullPath(Path.Combine(root, AdmissionClosurePath));
        if (!IsChildOf(root, path) || !File.Exists(path))
        {
            throw new ExtractionException("Missing reviewed source-admission closure: " + AdmissionClosurePath + ".");
        }

        byte[] bytes = File.ReadAllBytes(path);
        string[] lines;
        try
        {
            lines = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        }
        catch (DecoderFallbackException exception)
        {
            throw new ExtractionException("The reviewed source-admission closure is not strict UTF-8: " + exception.Message);
        }

        List<AdmissionSource> sources = [];
        List<AdmissionMember> members = [];
        foreach (string raw in lines)
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
                    if (string.IsNullOrWhiteSpace(role) || string.IsNullOrWhiteSpace(sourcePath))
                    {
                        throw new ExtractionException("The reviewed source closure has an empty source field.");
                    }
                    sources.Add(new AdmissionSource(role, sourcePath, sha256));
                    break;
                case ["member", string memberPath, string owner, string member, string signature, string tokenSha256]:
                    RequireSha256(tokenSha256, "Reviewed member closure SHA-256");
                    if (string.IsNullOrWhiteSpace(memberPath) || string.IsNullOrWhiteSpace(owner) ||
                        string.IsNullOrWhiteSpace(member) || string.IsNullOrWhiteSpace(signature))
                    {
                        throw new ExtractionException("The reviewed source closure has an empty member field.");
                    }
                    members.Add(new AdmissionMember(memberPath, owner, member, signature, tokenSha256));
                    break;
                default:
                    throw new ExtractionException("The reviewed source-admission closure contains an unrecognized line: " + line);
            }
        }

        if (sources.Count != 15)
        {
            throw new ExtractionException("The reviewed source-admission closure must list exactly fifteen source files.");
        }

        return new AdmissionClosure(Sha256(bytes), sources.ToArray(), members.ToArray());
    }

    private static void ValidateSourceClosure(SourceFile[] sources, AdmissionClosure admission)
    {
        if (admission.Sources.Length != sources.Length)
        {
            throw new ExtractionException("The reviewed source-admission closure source count changed.");
        }

        for (int index = 0; index < sources.Length; index++)
        {
            SourceFile source = sources[index];
            AdmissionSource admitted = admission.Sources[index];
            if (admitted.Path != source.RelativePath || admitted.Role != source.Role || admitted.Sha256 != source.Sha256)
            {
                throw new ExtractionException("A reviewed source file changed; deliberately review and update " +
                    AdmissionClosurePath + " before regeneration: " + source.RelativePath + ".");
            }
        }
    }

    private static void ValidateRoundTrip(IrDocument sourceDerived, IrDocument candidate)
    {
        ValidateIr(sourceDerived);
        ValidateIr(candidate);
        if (!Serialize(sourceDerived).AsSpan().SequenceEqual(Serialize(candidate)))
        {
            throw new ExtractionException("Candidate IR differs from source-derived IR.");
        }
    }

    private static void ValidateRoundTrip(Manifest sourceDerived, Manifest candidate)
    {
        ValidateManifest(sourceDerived);
        ValidateManifest(candidate);
        if (!Serialize(sourceDerived).AsSpan().SequenceEqual(Serialize(candidate)))
        {
            throw new ExtractionException("Candidate manifest differs from source-derived manifest.");
        }
    }

    private static void CompareFiles(string expectedPath, string actualPath)
    {
        if (!File.Exists(expectedPath) || !File.Exists(actualPath))
        {
            throw new ExtractionException($"Missing checked-in or fresh artifact for {Path.GetFileName(expectedPath)}.");
        }

        byte[] expected = File.ReadAllBytes(expectedPath);
        byte[] actual = File.ReadAllBytes(actualPath);
        if (!expected.AsSpan().SequenceEqual(actual))
        {
            throw new ExtractionException($"Generated artifact drift: {Path.GetFileName(expectedPath)}.");
        }
    }

    private static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static string Canonical(SyntaxNode node)
    {
        string text = node.WithoutTrivia().ToFullString();
        StringBuilder builder = new(text.Length);
        for (int index = 0; index < text.Length; index++)
        {
            if (!char.IsWhiteSpace(text[index]))
            {
                builder.Append(text[index]);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Converts the limited, admitted Roslyn syntax into a typed and recursively complete tree.
    /// The extractor intentionally stops at the first unfamiliar node: source syntax is never
    /// smuggled into Lean as an opaque string that the emitter might ignore.
    /// </summary>
    private static SourceExpression LowerSourceSyntax(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax method => SourceTree(SourceExpressionKind.Projection, method,
            method.Identifier.ValueText, []),
        EnumDeclarationSyntax declaration => SourceTree(SourceExpressionKind.Projection, declaration,
            declaration.Identifier.ValueText, []),
        FieldDeclarationSyntax field when field.Declaration.Variables.Count == 1 =>
            SourceTree(SourceExpressionKind.Projection, field, field.Declaration.Variables[0].Identifier.ValueText, []),
        BlockSyntax block => SourceTree(SourceExpressionKind.Block, block, "block",
            block.Statements.Select(LowerSourceSyntax).ToArray()),
        LocalDeclarationStatementSyntax declaration => SourceTree(SourceExpressionKind.LocalDeclaration, declaration,
            "local", [LowerSourceSyntax(declaration.Declaration)]),
        VariableDeclarationSyntax declaration => SourceTree(SourceExpressionKind.LocalDeclaration, declaration,
            Canonical(declaration.Type), declaration.Variables.Select(LowerSourceSyntax).ToArray()),
        VariableDeclaratorSyntax declarator => SourceTree(SourceExpressionKind.VariableDeclarator, declarator,
            declarator.Identifier.ValueText,
            declarator.Initializer is null ? [] : [LowerSourceSyntax(declarator.Initializer.Value)]),
        IfStatementSyntax conditional => SourceTree(SourceExpressionKind.If, conditional, "if",
            conditional.Else is null
                ? [LowerSourceSyntax(conditional.Condition), LowerSourceSyntax(conditional.Statement)]
                : [LowerSourceSyntax(conditional.Condition), LowerSourceSyntax(conditional.Statement), LowerSourceSyntax(conditional.Else.Statement)]),
        ReturnStatementSyntax returned => SourceTree(SourceExpressionKind.Return, returned, "return",
            returned.Expression is null ? [] : [LowerSourceSyntax(returned.Expression)]),
        ExpressionStatementSyntax statement => SourceTree(SourceExpressionKind.ExpressionStatement, statement,
            "expression", [LowerSourceSyntax(statement.Expression)]),
        ArgumentSyntax argument => SourceTree(SourceExpressionKind.Argument, argument,
            ArgumentKind(argument), [LowerSourceSyntax(argument.Expression)]),
        IdentifierNameSyntax identifier => SourceTree(SourceExpressionKind.Identifier, identifier,
            identifier.Identifier.ValueText, []),
        PredefinedTypeSyntax type => SourceTree(SourceExpressionKind.Identifier, type,
            Canonical(type), []),
        LiteralExpressionSyntax literal => SourceTree(SourceExpressionKind.Literal, literal,
            literal.Token.ValueText, []),
        ThisExpressionSyntax self => SourceTree(SourceExpressionKind.Identifier, self, "this", []),
        MemberAccessExpressionSyntax access => SourceTree(SourceExpressionKind.MemberAccess, access,
            access.Name.Identifier.ValueText, [LowerSourceSyntax(access.Expression)]),
        QualifiedNameSyntax name => SourceTree(SourceExpressionKind.MemberAccess, name,
            name.Right.Identifier.ValueText, [LowerSourceSyntax(name.Left)]),
        InvocationExpressionSyntax invocation => SourceTree(SourceExpressionKind.Invocation, invocation,
            InvocationName(invocation), [LowerSourceSyntax(invocation.Expression), .. invocation.ArgumentList.Arguments.Select(LowerSourceSyntax)]),
        PrefixUnaryExpressionSyntax unary => SourceTree(SourceExpressionKind.Unary, unary,
            unary.OperatorToken.ValueText, [LowerSourceSyntax(unary.Operand)]),
        PostfixUnaryExpressionSyntax unary => SourceTree(SourceExpressionKind.PostfixUnary, unary,
            unary.OperatorToken.ValueText, [LowerSourceSyntax(unary.Operand)]),
        BinaryExpressionSyntax binary => SourceTree(SourceExpressionKind.Binary, binary,
            binary.OperatorToken.ValueText, [LowerSourceSyntax(binary.Left), LowerSourceSyntax(binary.Right)]),
        ConditionalExpressionSyntax conditional => SourceTree(SourceExpressionKind.Conditional, conditional, "?:",
            [LowerSourceSyntax(conditional.Condition), LowerSourceSyntax(conditional.WhenTrue), LowerSourceSyntax(conditional.WhenFalse)]),
        CastExpressionSyntax cast => SourceTree(SourceExpressionKind.Cast, cast, Canonical(cast.Type),
            [LowerSourceSyntax(cast.Expression)]),
        IsPatternExpressionSyntax pattern => SourceTree(SourceExpressionKind.IsPattern, pattern,
            "is", [LowerSourceSyntax(pattern.Expression), LowerPattern(pattern.Pattern)]),
        AssignmentExpressionSyntax assignment => SourceTree(SourceExpressionKind.Assignment, assignment,
            assignment.OperatorToken.ValueText, [LowerSourceSyntax(assignment.Left), LowerSourceSyntax(assignment.Right)]),
        ParenthesizedExpressionSyntax parenthesized => SourceTree(SourceExpressionKind.Parenthesized, parenthesized,
            "()", [LowerSourceSyntax(parenthesized.Expression)]),
        DeclarationExpressionSyntax declaration => SourceTree(SourceExpressionKind.Projection, declaration,
            Canonical(declaration), []),
        SingleVariableDesignationSyntax designation => SourceTree(SourceExpressionKind.Projection, designation,
            designation.Identifier.ValueText, []),
        PropertyDeclarationSyntax property => SourceTree(SourceExpressionKind.Projection, property,
            property.Identifier.ValueText, []),
        _ => throw new ExtractionException("The source contains syntax outside the admitted typed expression grammar: " +
            node.Kind() + " (" + Canonical(node) + ")."),
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

        return new SourceExpression(
            kind,
            Canonical(node),
            symbol,
            GrammarIdentityTag(node, kind, symbol),
            GrammarTypeCategory(node, kind, symbol),
            children);
    }

    private static string ArgumentKind(ArgumentSyntax argument) =>
        string.IsNullOrEmpty(argument.RefKindKeyword.ValueText) ? "value" : argument.RefKindKeyword.ValueText;

    /// <summary>
    /// Supplies a stable finite-grammar identity for every lowered executable node. It is kept
    /// separate from the display spelling so a deserialized IR cannot relabel a receiver, member,
    /// invocation, or operator and still select the same Lean lowering. It is not a Roslyn symbol
    /// lookup; the external compiler-resolution audit is the corresponding composition obligation.
    /// </summary>
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
        CastExpressionSyntax cast => "cast:" + Canonical(cast.Type),
        IsPatternExpressionSyntax => "operator:is-pattern:is",
        AssignmentExpressionSyntax assignment => "operator:" + assignment.Kind() + ":" + symbol,
        ParenthesizedExpressionSyntax => "operator:parenthesized:()",
        ArgumentSyntax argument => "argument:" + ArgumentKind(argument),
        _ => kind + ":" + symbol,
    };

    /// <summary>
    /// Carries the finite grammar category used by the closed Lean environment. The extractor
    /// refuses a category which no lowering owns; it never falls back to a string fragment or an
    /// implicit natural-number coercion. It is not a compiler-resolved type.
    /// </summary>
    private static string GrammarTypeCategory(SyntaxNode node, SourceExpressionKind kind, string symbol) => node switch
    {
        BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalAndExpression) ||
            binary.IsKind(SyntaxKind.LogicalOrExpression) || binary.IsKind(SyntaxKind.EqualsExpression) ||
            binary.IsKind(SyntaxKind.NotEqualsExpression) || binary.IsKind(SyntaxKind.LessThanExpression) ||
            binary.IsKind(SyntaxKind.GreaterThanExpression) || binary.IsKind(SyntaxKind.LessThanOrEqualExpression) ||
            binary.IsKind(SyntaxKind.GreaterThanOrEqualExpression) || binary.IsKind(SyntaxKind.IsExpression) => "bool",
        PrefixUnaryExpressionSyntax unary when unary.IsKind(SyntaxKind.LogicalNotExpression) => "bool",
        PostfixUnaryExpressionSyntax unary when unary.IsKind(SyntaxKind.SuppressNullableWarningExpression) =>
            GrammarTypeCategory(unary.Operand, kind, symbol),
        IsPatternExpressionSyntax => "bool",
        InvocationExpressionSyntax invocation => InvocationResultType(invocation),
        CastExpressionSyntax cast => Canonical(cast.Type),
        LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.TrueLiteralExpression) ||
            literal.IsKind(SyntaxKind.FalseLiteralExpression) => "bool",
        LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.NullLiteralExpression) => "null",
        LiteralExpressionSyntax => "numeric",
        ConditionalExpressionSyntax => "conditional",
        MemberAccessExpressionSyntax access => MemberAccessType(access),
        QualifiedNameSyntax name => QualifiedNameType(name),
        IdentifierNameSyntax identifier => IdentifierType(identifier),
        PredefinedTypeSyntax type => Canonical(type),
        _ => kind switch
        {
            SourceExpressionKind.MemberAccess or SourceExpressionKind.Identifier => "projection",
            SourceExpressionKind.Argument => "argument",
            SourceExpressionKind.Assignment => "assignment",
            SourceExpressionKind.Block or SourceExpressionKind.If or SourceExpressionKind.Return or
                SourceExpressionKind.ExpressionStatement or SourceExpressionKind.LocalDeclaration or
                SourceExpressionKind.VariableDeclarator => "statement",
            SourceExpressionKind.PatternConstant or SourceExpressionKind.PatternUnary or SourceExpressionKind.PatternBinary => "pattern",
            _ => "syntax",
        },
    };

    private static string InvocationResultType(InvocationExpressionSyntax invocation) => InvocationName(invocation) switch
    {
        "ValidateStatic" or "ValidateSender" or "BuyGas" or "IncrementNonce" => "TransactionResult",
        "CalculateEffectiveGasPrice" => "UInt256",
        "TryCalculatePremiumPerGas" or "MultiplyOverflow" or "AddOverflow" or "HasFlag" or "IsFree" or
            "IsInvalidContractSender" or "TryCalculateBlobFees" or "AccountExists" => "bool",
        "GetBlobCount" => "int",
        "CalculateBlobGas" => "ulong",
        "Min" => "UInt256",
        "SetNonce" or "Reset" or "PrepareSimpleTransferFastPath" or "SubtractFromBalance" or "CreateAccount" or
            "UpdateBlockGasPrice" => "void",
        _ => "unresolved",
    };

    /// <summary>
    /// The extractor does not build Nethermind's complete compilation, so these are the source
    /// declaration categories admitted by this deliberately bounded grammar. Every entry is
    /// subsequently tied to the complete token fingerprint of the containing source member;
    /// unknown source types fail closed instead of becoming an implicit natural-number carrier.
    /// </summary>
    private static string IdentifierType(IdentifierNameSyntax identifier) => identifier.Identifier.ValueText switch
    {
        "tx" => "Transaction",
        "spec" => "IReleaseSpec",
        "opts" => "ExecutionOptions",
        "header" => "BlockHeader",
        "tracer" => "ITxTracer",
        "sender" or "senderAddress" => "Address?",
        "balance" or "balanceCheck" or "effectiveGasPrice" or "effectiveFee" or "feeCap" or "baseFee" or
            "baseFeePerGas" or
            "feePerBlobGas" or "blobBaseFee" or "maxBlobGasFee" or "senderReservedGasPayment" or "premiumPerGas" or
            "opcodeGasPrice" => "UInt256",
        "nonce" or "newNonce" or "blobCount" or "blobGas" => "ulong",
        "validate" or "commit" or "restore" or "noValidation" or "freeTransaction" or "eip1559Enabled" or
            "SkipSenderCodeCheck" => "bool",
        "result" => "TransactionResult",
        "intrinsicGas" => "IntrinsicGas<TGasPolicy>",
        "ValidateStatic" or "ValidateSender" or "BuyGas" or "IncrementNonce" => "method:TransactionResult",
        "CalculateEffectiveGasPrice" => "method:UInt256",
        "TryCalculatePremiumPerGas" => "method:bool",
        "PrepareSimpleTransferFastPath" => "method:void",
        "ExecutionOptions" => "ExecutionOptions",
        "Eip4844Constants" => "Eip4844Constants",
        "UInt256" => "UInt256",
        "ulong" => "ulong",
        "WorldState" => "IWorldState",
        "Metrics" => "Metrics",
        "_blobBaseFeeCalculator" => "IBlobBaseFeeCalculator",
        _ => "unresolved",
    };

    private static string MemberAccessType(MemberAccessExpressionSyntax access)
    {
        string receiver = access.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            PredefinedTypeSyntax type => Canonical(type),
            _ => string.Empty,
        };
        return MemberAccessType(receiver, access.Name.Identifier.ValueText);
    }

    private static string QualifiedNameType(QualifiedNameSyntax name) =>
        MemberAccessType(Canonical(name.Left), name.Right.Identifier.ValueText);

    private static string MemberAccessType(string receiver, string member) =>
        (receiver, member) switch
        {
            ("tx", "MaxPriorityFeePerGas") or ("tx", "MaxFeePerGas") or ("tx", "MaxFeePerBlobGas") or
                ("tx", "ValueRef") or ("header", "BaseFeePerGas") => "UInt256",
            ("tx", "GasLimit") or ("tx", "Nonce") or ("spec", "BlobBaseFeeUpdateFraction") => "ulong",
            ("tx", "SenderAddress") => "Address?",
            ("tx", "Supports1559") or ("tx", "SupportsBlobs") or ("spec", "IsEip1559Enabled") or
                ("effectiveGasPrice", "IsZero") or ("senderReservedGasPayment", "IsZero") => "bool",
            ("Eip4844Constants", "GasPerBlob") or ("ulong", "MaxValue") => "ulong",
            ("UInt256", "Zero") => "UInt256",
            ("ExecutionOptions", "Commit") or ("ExecutionOptions", "None") or ("ExecutionOptions", "Restore") or
                ("ExecutionOptions", "SkipValidation") or ("ExecutionOptions", "Warmup") or ("ExecutionOptions", "BuildUp") =>
                "ExecutionOptions",
            ("tx", "CalculateEffectiveGasPrice") => "UInt256",
            ("tx", "TryCalculatePremiumPerGas") or ("tx", "IsFree") or ("opts", "HasFlag") or
                ("WorldState", "AccountExists") or ("WorldState", "IsInvalidContractSender") or
                ("UInt256", "AddOverflow") or ("UInt256", "MultiplyOverflow") or
                ("_blobBaseFeeCalculator", "TryCalculateBlobFees") => "bool",
            ("UInt256", "Min") => "UInt256",
            ("Metrics", "UpdateBlockGasPrice") or ("WorldState", "SetNonce") or ("WorldState", "Reset") or
                ("WorldState", "CreateAccount") => "void",
            ("BlobGasCalculator", "CalculateBlobGas") => "ulong",
            _ => "unresolved",
        };

    private static IEnumerable<SourceExpression> Descendants(SourceExpression expression)
    {
        yield return expression;
        foreach (SourceExpression child in expression.Children)
        {
            foreach (SourceExpression descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static SourceExpression LowerPattern(PatternSyntax pattern) => pattern switch
    {
        ConstantPatternSyntax constant => SourceTree(SourceExpressionKind.PatternConstant, constant, "constant",
            [LowerSourceSyntax(constant.Expression)]),
        UnaryPatternSyntax unary => SourceTree(SourceExpressionKind.PatternUnary, unary, unary.OperatorToken.ValueText,
            [LowerPattern(unary.Pattern)]),
        BinaryPatternSyntax binary => SourceTree(SourceExpressionKind.PatternBinary, binary, binary.OperatorToken.ValueText,
            [LowerPattern(binary.Left), LowerPattern(binary.Right)]),
        _ => throw new ExtractionException("The source contains a pattern outside the admitted typed expression grammar: " +
            pattern.Kind() + "."),
    };

    private static void ValidateSourceExpression(SourceExpression? expression)
    {
        if (expression is null || string.IsNullOrWhiteSpace(expression.Normalized) ||
            string.IsNullOrWhiteSpace(expression.Symbol) || string.IsNullOrWhiteSpace(expression.SymbolId) ||
            string.IsNullOrWhiteSpace(expression.TypeName) || expression.Children is null)
        {
            throw new ExtractionException("A typed source expression is incomplete.");
        }

        int expectedChildren = expression.Kind switch
        {
            SourceExpressionKind.Projection or SourceExpressionKind.Identifier or SourceExpressionKind.Literal => 0,
            SourceExpressionKind.MemberAccess or SourceExpressionKind.Argument or SourceExpressionKind.Unary or
                SourceExpressionKind.PostfixUnary or
                SourceExpressionKind.Cast or SourceExpressionKind.PatternConstant or SourceExpressionKind.PatternUnary or
                SourceExpressionKind.Parenthesized or
                SourceExpressionKind.ExpressionStatement => 1,
            SourceExpressionKind.Binary or SourceExpressionKind.Assignment or SourceExpressionKind.IsPattern or
                SourceExpressionKind.PatternBinary => 2,
            SourceExpressionKind.Conditional => 3,
            SourceExpressionKind.If => expression.Children.Length is 2 or 3 ? expression.Children.Length : -1,
            SourceExpressionKind.VariableDeclarator or SourceExpressionKind.Return => expression.Children.Length is 0 or 1 ? expression.Children.Length : -1,
            SourceExpressionKind.Invocation or SourceExpressionKind.Block or SourceExpressionKind.LocalDeclaration => expression.Children.Length,
            _ => -1,
        };
        if (expectedChildren < 0 || expectedChildren != expression.Children.Length)
        {
            throw new ExtractionException("A typed source expression has an unsupported child shape.");
        }

        if (!HasExpectedSourceIdentity(expression))
        {
            throw new ExtractionException("A typed source expression has an unadmitted symbol or result category: " +
                expression.Kind + "|" + expression.Symbol + "|" + expression.SymbolId + "|" + expression.TypeName + ".");
        }

        foreach (SourceExpression child in expression.Children)
        {
            ValidateSourceExpression(child);
        }
    }

    private static bool HasExpectedSourceIdentity(SourceExpression expression) => expression.Kind switch
    {
        SourceExpressionKind.Identifier => expression.SymbolId == "identifier:" + expression.Symbol &&
            expression.TypeName != "unresolved",
        SourceExpressionKind.Literal => expression.SymbolId.StartsWith("literal:", StringComparison.Ordinal) &&
            expression.TypeName != "unresolved",
        SourceExpressionKind.MemberAccess => expression.SymbolId.StartsWith("member:", StringComparison.Ordinal) &&
            expression.SymbolId.EndsWith("." + expression.Symbol, StringComparison.Ordinal) &&
            expression.TypeName != "unresolved",
        SourceExpressionKind.Invocation => expression.SymbolId.StartsWith("invocation:", StringComparison.Ordinal) &&
            expression.TypeName != "unresolved",
        SourceExpressionKind.Unary or SourceExpressionKind.PostfixUnary or SourceExpressionKind.Binary or SourceExpressionKind.Assignment =>
            expression.SymbolId.StartsWith("operator:", StringComparison.Ordinal),
        SourceExpressionKind.Conditional or SourceExpressionKind.IsPattern or SourceExpressionKind.Parenthesized =>
            expression.SymbolId.StartsWith("operator:", StringComparison.Ordinal),
        SourceExpressionKind.Cast => expression.SymbolId == "cast:" + expression.Symbol,
        SourceExpressionKind.Argument => expression.SymbolId == "argument:" + expression.Symbol,
        SourceExpressionKind.Projection or SourceExpressionKind.Block or SourceExpressionKind.LocalDeclaration or
            SourceExpressionKind.VariableDeclarator or SourceExpressionKind.If or SourceExpressionKind.Return or
            SourceExpressionKind.ExpressionStatement or SourceExpressionKind.PatternConstant or
            SourceExpressionKind.PatternUnary or SourceExpressionKind.PatternBinary =>
            expression.SymbolId.StartsWith(expression.Kind + ":", StringComparison.Ordinal),
        _ => false,
    };

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string CombinedHash(SourceIdentity[] sources)
    {
        StringBuilder builder = new();
        for (int index = 0; index < sources.Length; index++)
        {
            builder.Append(sources[index].Path).Append('\n')
                .Append(sources[index].Role).Append('\n')
                .Append(sources[index].Sha256).Append('\n');
        }

        return Sha256(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static void RequireSha256(string? value, string field)
    {
        if (value is null || value.Length != 64)
        {
            throw new ExtractionException($"{field} is not a SHA-256 identity.");
        }

        for (int index = 0; index < value.Length; index++)
        {
            if (!Uri.IsHexDigit(value[index]))
            {
                throw new ExtractionException($"{field} is not a SHA-256 identity.");
            }
        }
    }

    private static bool IsChildOf(string parent, string child)
    {
        string normalizedParent = Path.GetFullPath(parent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string normalizedChild = Path.GetFullPath(child);
        return normalizedChild.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private sealed record SourceFile(
        string RelativePath,
        string Role,
        string Sha256,
        SyntaxTree Tree,
        CompilationUnitSyntax Root);

    private sealed record AdmissionSource(string Role, string Path, string Sha256);

    private sealed record AdmissionMember(
        string Path,
        string Owner,
        string Member,
        string Signature,
        string TokenSha256)
    {
        internal string Key => Path + "|" + Owner + "|" + Member + "|" + Signature;
    }

    private sealed record AdmissionClosure(
        string Sha256,
        AdmissionSource[] Sources,
        AdmissionMember[] Members);

    private sealed record PrefixValidation(
        StageAnchor[] Stages,
        BranchShape[] Branches,
        EffectShape[] Effects,
        string MetricsEqualityGate,
        MethodDeclarationSyntax Execute,
        MethodDeclarationSyntax EffectivePrice,
        MethodDeclarationSyntax UpdateMetrics,
        MethodDeclarationSyntax Recovery,
        MethodDeclarationSyntax BuyGas,
        MethodDeclarationSyntax IncrementNonce,
        MethodDeclarationSyntax ExtensionEffectivePrice,
        MethodDeclarationSyntax Premium,
        MethodDeclarationSyntax TryCalculateBlobFees);
}
