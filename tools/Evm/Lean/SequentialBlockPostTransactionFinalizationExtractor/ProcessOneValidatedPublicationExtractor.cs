// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nethermind.Evm.Lean.SequentialBlockPostTransactionFinalizationExtractor;

/// <summary>Audits and (in the serialized build lane) emits the bounded ProcessOne suffix.</summary>
/// <remarks>
/// This is an extension of the post-transaction finalization package, not a second block
/// processor extractor.  The suffix consumes a normal <c>ProcessBlock</c> result and stops at
/// the returned <c>(block, receipts)</c> tuple.  It deliberately does not execute C# or infer
/// validator, virtual dispatch, disposal, or receipt-storage implementations.
/// </remarks>
internal static partial class ProcessOneValidatedPublicationExtractor
{
    internal const string ArtifactName = "ProcessOneValidatedPublication";
    internal const string IrFileName = ArtifactName + ".ir.json";
    internal const string ManifestFileName = ArtifactName + ".source-manifest.json";
    internal const string LeanFileName = ArtifactName + ".lean";
    internal const string PinsPath =
        "tools/Evm/Lean/SequentialBlockPostTransactionFinalizationExtractor/PROCESS_ONE_PUBLICATION_SOURCE_PINS.json";
    internal const string DefaultOutputPath =
        "tools/Evm/Lean/SequentialBlockPostTransactionFinalizationExtractor/Generated";
    internal const string DefaultLeanPath =
        "tools/Evm/Lean/SequentialBlockPostTransactionFinalizationExtractor/Generated/ProcessOneValidatedPublication.lean";

    private const int SchemaVersion = 2;
    private const string ExtractorVersion = "1.0.0";
    private const string AcceptanceState = "source-admitted";
    private const string StaticDraftState = "static-draft";
    private const string Kernel =
        "standard exact-base sequential BAL-disabled ProcessOne validated-publication suffix";

    private const string BlockProcessorPath =
        "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs";
    private const string BlockValidatorPath =
        "src/Nethermind/Nethermind.Consensus/Validators/BlockValidator.cs";
    private const string BlockValidatorInterfacePath =
        "src/Nethermind/Nethermind.Consensus/Validators/IBlockValidator.cs";
    private const string ProcessingOptionsPath =
        "src/Nethermind/Nethermind.Consensus/Processing/ProcessingOptions.cs";

    private const string ReceiptManifestPath =
        "tools/Evm/Lean/ReceiptTerminalFoldExtractor/Generated/ReceiptTerminalFoldKernel.source-manifest.json";
    private const string ReceiptIrPath =
        "tools/Evm/Lean/ReceiptTerminalFoldExtractor/Generated/ReceiptTerminalFoldKernel.ir.json";
    private const string ReceiptLeanPath =
        "tools/Evm/Lean/ReceiptTerminalFoldExtractor/Generated/ReceiptTerminalFoldKernel.lean";
    private const string ReceiptPinsPath = "tools/Evm/Lean/ReceiptTerminalFoldExtractor/SOURCE_PINS.json";
    private const string FinalizationManifestPath =
        "tools/Evm/Lean/SequentialBlockPostTransactionFinalizationExtractor/Generated/SequentialBlockPostTransactionFinalization.source-manifest.json";
    private const string FinalizationIrPath =
        "tools/Evm/Lean/SequentialBlockPostTransactionFinalizationExtractor/Generated/SequentialBlockPostTransactionFinalization.ir.json";
    private const string FinalizationLeanPath =
        "tools/Evm/Lean/SequentialBlockPostTransactionFinalizationExtractor/Generated/SequentialBlockPostTransactionFinalization.lean";

    private const string ProcessOneFqn =
        "global::Nethermind.Consensus.Processing.BlockProcessor.ProcessOne(Nethermind.Core.Block,Nethermind.Consensus.Processing.ProcessingOptions,Nethermind.Evm.Tracing.IBlockTracer,Nethermind.Core.Specs.IReleaseSpec,System.Threading.CancellationToken)";
    private const string ValidateProcessedBlockFqn =
        "global::Nethermind.Consensus.Processing.BlockProcessor.ValidateProcessedBlock(Nethermind.Core.Block,Nethermind.Consensus.Processing.ProcessingOptions,Nethermind.Core.Block,Nethermind.Core.TxReceipt[])";
    private const string PostValidationFqn =
        "global::Nethermind.Consensus.Processing.BlockProcessor.PostValidation(Nethermind.Core.Block,Nethermind.Core.Block,Nethermind.Core.TxReceipt[],Nethermind.Consensus.Processing.ProcessingOptions)";
    private const string StoreReceiptsFqn =
        "global::Nethermind.Consensus.Processing.BlockProcessor.StoreTxReceipts(Nethermind.Core.Block,Nethermind.Core.TxReceipt[],Nethermind.Core.Specs.IReleaseSpec)";
    private const string ValidatorCallFqn =
        "global::Nethermind.Consensus.Validators.IBlockValidator.ValidateProcessedBlock(Nethermind.Core.Block,Nethermind.Core.TxReceipt[],Nethermind.Core.Block,System.String&)";
    private const string ValidatorImplementationFqn =
        "global::Nethermind.Consensus.Validators.BlockValidator.ValidateProcessedBlock(Nethermind.Core.Block,Nethermind.Core.TxReceipt[],Nethermind.Core.Block,System.String&)";
    private const string InsertDeferredFqn =
        "global::Nethermind.Blockchain.Receipts.IReceiptStorage.InsertDeferred(Nethermind.Core.Block,Nethermind.Core.TxReceipt[],Nethermind.Core.Specs.IReleaseSpec)";

    private static readonly string[] SourcePaths =
    [BlockProcessorPath, BlockValidatorPath, BlockValidatorInterfacePath, ProcessingOptionsPath,
     Extractor.BlockProcessorStandardPath, Extractor.BlockProcessorInterfacePath,
     Extractor.TransactionExecutorPath, "src/Nethermind/Nethermind.Blockchain/Receipts/IReceiptStorage.cs"];

    private static readonly string[] SourceRoles =
    ["ProcessOne caller and suffix", "validator side-effect observation", "validator contract", "option guard closure",
     "partial standard closure", "processor interface closure", "standard executor closure", "receipt storage contract"];

    private static readonly string[] AnchorIds =
    [
        "processOne.processBlock-return",
        "processOne.processed-commit",
        "processOne.retry-access-list-catch",
        "processOne.retry-parallel-catch",
        "processOne.disposal-finally",
        "processOne.validate-call",
        "processOne.validation-guard",
        "processOne.validator-call",
        "processOne.validation-dispose",
        "processOne.invalid-block-throw",
        "processOne.post-validation-call",
        "processOne.store-guard",
        "processOne.insert-deferred",
        "processOne.return-tuple",
        "postValidation.accountChanges",
        "postValidation.executionRequests",
        "postValidation.generatedBlockAccessList",
        "postValidation.encodedBlockAccessList-fallback",
        "validator.generatedBlockAccessList-observation",
        "options.NoValidation",
        "options.StoreReceipts",
    ];

    private static readonly string[] OrderedEvents =
    [
        "processBlockReturned",
        "validatedOrNoValidation",
        "postValidation.accountChanges",
        "postValidation.executionRequests",
        "postValidation.generatedBlockAccessList",
        "postValidation.encodedBlockAccessListCoalesce",
        "publishedExecutionArtifacts",
        "receiptStorage.InsertDeferred",
        "returnedProcessedBlockAndReceipts",
    ];

    private static readonly string[] AdapterPremiseIds =
    [
        "exactBase", "standardSequential", "balDisabled", "nonparallel", "processBlockNormalReturn",
        "validatorObservation", "rejectionCleanupNormalReturn", "postValidationNormalReturn", "storeNormalReturn", "noAdditionalEffects",
    ];

    private static readonly string[] DependencyPaths =
    [
        .. DependencyAudit.Paths,
        "tools/Evm/Lean/SequentialBlockPostTransactionFinalizationExtractor/Specification/ProcessOneValidatedPublication.lean",
        "tools/Evm/Lean/SequentialBlockPostTransactionFinalizationExtractor/Refinement/ProcessOneValidatedPublication.lean",
        "tools/Evm/Lean/SequentialBlockPostTransactionFinalizationExtractor/Vectors/ProcessOneValidatedPublicationVectors.lean",
        PinsPath,
        "tools/Evm/Lean/SequentialBlockPostTransactionFinalizationExtractor/PUBLICATION_PROMOTION_PINS.json",
        "tools/Evm/Lean/SequentialBlockPostTransactionFinalizationExtractor/PUBLICATION_EXPORTED_THEOREMS.txt",
        "tools/Evm/Lean/SequentialBlockPostTransactionFinalizationExtractor/Verify-PublicationAxioms.ps1",
        "tools/Evm/Lean/SequentialBlockPostTransactionFinalizationExtractor/Verify-PublicationMutationGates.ps1",
        "tools/Evm/Lean/SequentialBlockPostTransactionFinalizationExtractor/Schema/process-one-validated-publication.ir.schema.json",
        "tools/Evm/Lean/SequentialBlockPostTransactionFinalizationExtractor/Schema/process-one-validated-publication.source-manifest.schema.json",
        FinalizationLeanPath,
        FinalizationIrPath,
        FinalizationManifestPath,
        "tools/Evm/Lean/SequentialBlockPostTransactionFinalizationExtractor/PublicationKernel.lean.template",
    ];

    private static readonly string[] ForbiddenEffects =
    [
        "WorldState.CommitTree",
        "WorldState.Reset",
        "BlockTree head update",
        "BlockchainProcessor head publication",
    ];

    private static readonly string[] MutationChecks =
    [
        "move or remove ProcessBlock normal-result binding",
        "move validation before the ProcessBlock result or after publication",
        "remove or invert the NoValidation guard",
        "swap validator arguments or hide the out error local",
        "admit either BAL/parallel retry catch under the nonparallel premise",
        "omit finally disposal or dispose on the normal validator path",
        "replace validator rejection with a normal return or a generic escape",
        "omit, reorder, or redirect any of the four PostValidation copies",
        "replace encoded-BAL null-coalescing fallback with overwrite or reverse fallback",
        "store before publication, duplicate InsertDeferred, or change its guard/arguments",
        "return suggestedBlock or a different receipts local",
        "hide or remove the validator-side GeneratedBlockAccessList observation",
        "add CommitTree, reset, or head effects to the ProcessOne boundary",
        "change source/FQN/symbol identity, CFG region, dataflow, or source digest",
        "accept a stale upstream ReceiptTerminal or finalization artifact dependency",
    ];

    private static readonly string[] OpenObligations =
    [
        "The adapter supplies exact-base BlockProcessor.ProcessOne and exact-base PostValidation dispatch.",
        "The adapter supplies the standard BlockValidationTransactionsExecutor result as a normal ProcessBlock result.",
        "The adapter supplies standard sequential, BAL-disabled, and nonparallel premises; BAL retry catches are excluded.",
        "Validator observation includes normal return, Boolean acceptance, and the proposed GeneratedBlockAccessList post-call value; NoValidation requires before=after.",
        "The validator's root/hash/BAL-size diagnostics and all external validator implementations remain outside this suffix.",
        "Rejection disposal/diagnostics, PostValidation, and IReceiptStorage.InsertDeferred normal return are separate adapter premises.",
        "InsertDeferred is observed as synchronous visibility only; durable receipt write and canonical publication remain outside.",
        "No CommitTree, reset, or chain-head effect is admitted; BranchProcessor and BlockchainProcessor remain downstream.",
        "Opaque identifiers preserve block and receipt-array identity, not mutable receipt contents. External calls have no other modeled effect on normal return.",
        "Serialized artifact acceptance requires fresh matching ReceiptTerminal and normal-tail artifacts; stale checked outputs are rejected.",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true,
    };

    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default
        .WithLanguageVersion(LanguageVersion.CSharp14)
        .WithDocumentationMode(DocumentationMode.Parse)
        .WithKind(SourceCodeKind.Regular);

    internal static ProcessOnePublicationIrDocument AuditForTest(
        string repoRoot,
        IReadOnlyDictionary<string, byte[]>? sourceOverrides = null) =>
        BuildAudit(Path.GetFullPath(repoRoot), sourceOverrides, enforcePins: false);

    internal static void ValidateAuditForTest(string root, ProcessOnePublicationIrDocument document)
    {
        ValidateDocument(document);
        if (!Serialize(document).AsSpan().SequenceEqual(Serialize(BuildAudit(root, null, enforcePins: false))))
            throw new ExtractionException("Publication IR differs from its freshly bound source evidence.");
    }

    /// <summary>Validate current source and the checked-in suffix artifacts without writing files.</summary>
    internal static void ValidateCheckedIn(string repoRoot, string? outputDirectory) =>
        ValidateCheckedInCore(repoRoot, outputDirectory, null, null);

    internal static void ValidateCheckedInWithSourceOverridesForTest(string root, string output,
        IReadOnlyDictionary<string, byte[]> overrides, PublicationPinDocument pins) =>
        ValidateCheckedInCore(root, output, overrides, pins);

    private static void ValidateCheckedInCore(string repoRoot, string? outputDirectory,
        IReadOnlyDictionary<string, byte[]>? overrides, PublicationPinDocument? pins)
    {
        string root = Path.GetFullPath(repoRoot);
        string output = Path.GetFullPath(outputDirectory ?? Path.Combine(root, DefaultOutputPath));
        string irPath = Path.Combine(output, IrFileName);
        string manifestPath = Path.Combine(output, ManifestFileName);
        string leanPath = Path.Combine(output, LeanFileName);
        if (!File.Exists(irPath) || !File.Exists(manifestPath) || !File.Exists(leanPath))
        {
            throw new ExtractionException(
                "Checked-in ProcessOne validated-publication artifacts are incomplete; run serialized regeneration after upstream settlement.");
        }

        using JsonDocument ir = ReadJson(irPath);
        using JsonDocument manifest = ReadJson(manifestPath);
        if (IsStaticDraft(ir.RootElement) || IsStaticDraft(manifest.RootElement))
        {
            throw new ExtractionException(
                "ProcessOne validated-publication artifacts are static drafts; serialized re-emission is required before validation can pass.");
        }

        ProcessOnePublicationIrDocument sourceAudit = BuildAudit(root, overrides, enforcePins: true, pins);
        ValidateDocument(sourceAudit);

        ProcessOnePublicationIrDocument document = Deserialize<ProcessOnePublicationIrDocument>(File.ReadAllBytes(irPath));
        ValidateDocument(document);
        if (!Serialize(document).AsSpan().SequenceEqual(Serialize(sourceAudit)))
        {
            throw new ExtractionException("ProcessOne validated-publication source identities drifted.");
        }

        ProcessOnePublicationManifest artifactManifest =
            Deserialize<ProcessOnePublicationManifest>(File.ReadAllBytes(manifestPath));
        ValidateManifest(artifactManifest, document, Sha256(File.ReadAllBytes(irPath)), Sha256(File.ReadAllBytes(leanPath)));
        ValidateDependencyIdentities(root, artifactManifest.Dependencies);
        ValidateSettledDependencies(root);
        if (!File.ReadAllBytes(leanPath).AsSpan().SequenceEqual(EmitLean(root, document)))
            throw new ExtractionException("ProcessOne kernel does not match the admitted IR and template.");
        if (ContainsProofPlaceholder(File.ReadAllText(leanPath)))
        {
            throw new ExtractionException("ProcessOne validated-publication Lean contains a proof placeholder.");
        }
    }

    /// <summary>
    /// Re-emits the suffix only in the serialized build lane. The upstream receipt artifact gate
    /// runs before any output is created, so a stale dependency cannot be silently replaced.
    /// </summary>
    internal static ProcessOnePublicationExtractionResult Extract(string repoRoot, string outputDirectory)
    {
        string root = Path.GetFullPath(repoRoot);
        string output = Path.GetFullPath(outputDirectory);
        ValidateSettledDependencies(root);
        ProcessOnePublicationIrDocument document = BuildAudit(root, null, enforcePins: true);
        ValidateDocument(document);
        byte[] irBytes = Serialize(document);
        byte[] leanBytes = EmitLean(root, document);
        string irPath = Path.Combine(output, IrFileName);
        string manifestPath = Path.Combine(output, ManifestFileName);
        string leanPath = Path.Combine(output, LeanFileName);
        EnsureWithin(output, irPath);
        EnsureWithin(output, manifestPath);
        EnsureWithin(output, leanPath);
        ProcessOnePublicationManifest manifest = new(
            SchemaVersion,
            ExtractorVersion,
            Kernel,
            AcceptanceState,
            document.SourceClosureSha256,
            document.Sources,
            document.CompilerClosure,
            document.SynchronousCallables,
            document.ControlFlows,
            document.DataFlows,
            document.AdapterPremises,
            new PublicationArtifactIdentity(IrFileName, Sha256(irBytes)),
            new PublicationArtifactIdentity(DefaultLeanPath, Sha256(leanBytes)),
            DependencyPaths.Select(path => new PublicationDependencyIdentity(path, Sha256File(root, path))).ToArray(),
            MutationChecks,
            OpenObligations);
        ValidateManifest(manifest, document, Sha256(irBytes), Sha256(leanBytes));
        Directory.CreateDirectory(output);
        AtomicWrite(irPath, irBytes);
        AtomicWrite(leanPath, leanBytes);
        AtomicWrite(manifestPath, Serialize(manifest));
        return new(irPath, manifestPath, leanPath, document.Anchors.Length, document.ControlFlows.Length);
    }

    private static ProcessOnePublicationIrDocument BuildAudit(
        string root,
        IReadOnlyDictionary<string, byte[]>? sourceOverrides,
        bool enforcePins,
        PublicationPinDocument? overriddenPins = null)
    {
        PublicationPinDocument pins = overriddenPins ?? ReadPins(root);
        if (!pins.Sources.Select(static pin => pin.Path).SequenceEqual(SourcePaths) ||
            !pins.Sources.Select(static pin => pin.Role).SequenceEqual(SourceRoles))
        {
            throw new ExtractionException("ProcessOne publication source pins have the wrong ordered shape.");
        }

        PublicationSourceFile[] sources = ReadSources(root, pins.Sources, sourceOverrides, enforcePins);
        PublicationSemanticContext context = CompileSources(root, sources);

        PublicationAnchor Anchor(string id, PublicationSourceFile source, string owner, string symbol,
            SyntaxNode node, string cfgRegion, string dataFlow) =>
            TypedAnchor(context, id, source, owner, symbol, node, dataFlow);

        PublicationSourceFile blockProcessor = sources.Single(source => source.RelativePath == BlockProcessorPath);
        PublicationSourceFile blockValidator = sources.Single(source => source.RelativePath == BlockValidatorPath);
        PublicationSourceFile validatorInterface = sources.Single(source => source.RelativePath == BlockValidatorInterfacePath);
        PublicationSourceFile options = sources.Single(source => source.RelativePath == ProcessingOptionsPath);

        ClassDeclarationSyntax processorClass = FindClass(blockProcessor.Root, "BlockProcessor");
        MethodDeclarationSyntax processOne = FindMethod(processorClass, "ProcessOne", 5);
        MethodDeclarationSyntax validateProcessedBlock = FindMethod(processorClass, "ValidateProcessedBlock", 4);
        MethodDeclarationSyntax postValidation = FindMethod(processorClass, "PostValidation", 4);
        MethodDeclarationSyntax storeReceipts = FindMethod(processorClass, "StoreTxReceipts", 3);
        ClassDeclarationSyntax validatorClass = FindClass(blockValidator.Root, "BlockValidator");
        MethodDeclarationSyntax validatorMethod = FindMethod(validatorClass, "ValidateProcessedBlock", 4);
        PublicationSynchronousCallable[] synchronousCallables =
        [
            BindPublicationSynchronous(context, "validateProcessedBlock", validateProcessedBlock),
            BindPublicationSynchronous(context, "postValidation", postValidation),
            BindPublicationSynchronous(context, "storeTxReceipts", storeReceipts),
        ];
        ValidatePublicationSynchronousInventory(synchronousCallables);
        InterfaceDeclarationSyntax validatorContract = validatorInterface.Root.DescendantNodes()
            .OfType<InterfaceDeclarationSyntax>()
            .SingleOrDefault(item => item.Identifier.ValueText == "IBlockValidator")
            ?? throw new ExtractionException("Cannot bind IBlockValidator declaration.");
        EnumDeclarationSyntax optionsEnum = options.Root.DescendantNodes()
            .OfType<EnumDeclarationSyntax>()
            .SingleOrDefault(item => item.Identifier.ValueText == "ProcessingOptions")
            ?? throw new ExtractionException("Cannot bind ProcessingOptions declaration.");

        ValidateProcessOneShape(processOne);
        ValidateValidationShape(validateProcessedBlock);
        ValidatePostValidationShape(postValidation);
        ValidateStoreShape(storeReceipts);
        ValidateValidatorShape(validatorMethod);
        ValidateValidatorContract(validatorContract);
        ValidateOptionsShape(optionsEnum);
        RejectForbiddenProcessOneEffects(processOne);

        List<PublicationAnchor> anchors =
        [
            Anchor("processOne.processBlock-return", blockProcessor, ProcessOneFqn,
                "global::Nethermind.Consensus.Processing.BlockProcessor.ProcessBlock(Nethermind.Core.Block,Nethermind.Evm.Tracing.IBlockTracer,Nethermind.Consensus.Processing.ProcessingOptions,Nethermind.Core.Specs.IReleaseSpec,System.Threading.CancellationToken)",
                FindProcessBlockAssignment(processOne), "try.normal", "receipts: ProcessBlock result -> validation/store/return"),
            Anchor("processOne.processed-commit", blockProcessor, ProcessOneFqn, "System.Boolean processed",
                FindProcessedTrue(processOne), "try.normal", "processed=false -> true dominates validation; finally reads it"),
            Anchor("processOne.retry-access-list-catch", blockProcessor, ProcessOneFqn,
                "global::Nethermind.State.BlockAccessListBasedWorldState.InvalidBlockLevelAccessListException",
                FindCatch(processOne, 0), "catch.filtered", "parallel-only retry branch excluded by nonparallel premise"),
            Anchor("processOne.retry-parallel-catch", blockProcessor, ProcessOneFqn,
                "global::Nethermind.Consensus.Processing.BlockAccessListManager.ParallelExecutionException",
                FindCatch(processOne, 1), "catch.filtered", "parallel-only retry branch excluded by nonparallel premise"),
            Anchor("processOne.disposal-finally", blockProcessor, ProcessOneFqn,
                "global::Nethermind.Core.BlockExtensions.DisposeAccountChanges(Nethermind.Core.Block)",
                FindDisposal(processOne), "finally.normal-failure", "processed=false reaching definition"),
            Anchor("processOne.validate-call", blockProcessor, ProcessOneFqn,
                ValidateProcessedBlockFqn, FindInvocation(processOne, "ValidateProcessedBlock(suggestedBlock,options,block,receipts)"),
                "after.processBlock", "normal ProcessBlock result -> validation gate"),
            Anchor("processOne.validation-guard", blockProcessor, ValidateProcessedBlockFqn,
                "global::Nethermind.Consensus.Processing.ProcessingOptions.NoValidation",
                FindValidationIf(validateProcessedBlock), "if.guard", "NoValidation guard controls validator call"),
            Anchor("processOne.validator-call", blockProcessor, ValidateProcessedBlockFqn,
                ValidatorCallFqn, FindInvocation(validateProcessedBlock,
                    "blockValidator.ValidateProcessedBlock(block,receipts,suggestedBlock,outstring?error)"),
                "if.guard.false", "validator observation including proposed GeneratedBAL side effect"),
            Anchor("processOne.validation-dispose", blockProcessor, ValidateProcessedBlockFqn,
                "global::Nethermind.Core.BlockExtensions.DisposeAccountChanges(Nethermind.Core.Block)",
                FindValidationDisposal(validateProcessedBlock), "if.reject", "validator=false -> disposal before throw"),
            Anchor("processOne.invalid-block-throw", blockProcessor, ValidateProcessedBlockFqn,
                "global::Nethermind.Core.Exceptions.InvalidBlockException..ctor(Nethermind.Core.Block,System.String)",
                FindInvalidBlockThrow(validateProcessedBlock), "if.reject", "dispose -> InvalidBlockException; no publication"),
            Anchor("processOne.post-validation-call", blockProcessor, ValidateProcessedBlockFqn,
                PostValidationFqn, FindInvocation(validateProcessedBlock, "PostValidation(suggestedBlock,block,receipts,options)"),
                "after.validation-guard", "four-field publication call"),
            Anchor("processOne.store-guard", blockProcessor, ProcessOneFqn,
                "global::Nethermind.Consensus.Processing.ProcessingOptions.StoreReceipts",
                FindStoreIf(processOne), "if.guard", "StoreReceipts controls InsertDeferred"),
            Anchor("processOne.insert-deferred", blockProcessor, StoreReceiptsFqn, InsertDeferredFqn,
                FindInvocation(storeReceipts, "receiptStorage.InsertDeferred(block,txReceipts,spec)"),
                "store.normal", "publication -> InsertDeferred"),
            Anchor("processOne.return-tuple", blockProcessor, ProcessOneFqn,
                "System.ValueTuple<Nethermind.Core.Block,Nethermind.Core.TxReceipt[]>",
                FindReturn(processOne), "return.normal", "processed block and same receipts local"),
            Anchor("postValidation.accountChanges", blockProcessor, PostValidationFqn,
                "global::Nethermind.Core.Block.AccountChanges",
                FindAssignment(postValidation, "suggestedBlock.AccountChanges=processedBlock.AccountChanges"), "copy.1", "suggested <- processed"),
            Anchor("postValidation.executionRequests", blockProcessor, PostValidationFqn,
                "global::Nethermind.Core.Block.ExecutionRequests",
                FindAssignment(postValidation, "suggestedBlock.ExecutionRequests=processedBlock.ExecutionRequests"), "copy.2", "suggested <- processed"),
            Anchor("postValidation.generatedBlockAccessList", blockProcessor, PostValidationFqn,
                "global::Nethermind.Core.Block.GeneratedBlockAccessList",
                FindAssignment(postValidation, "suggestedBlock.GeneratedBlockAccessList=processedBlock.GeneratedBlockAccessList"), "copy.3", "suggested <- processed"),
            Anchor("postValidation.encodedBlockAccessList-fallback", blockProcessor, PostValidationFqn,
                "global::Nethermind.Core.Block.EncodedBlockAccessList",
                FindAssignment(postValidation, "suggestedBlock.EncodedBlockAccessList=processedBlock.EncodedBlockAccessList??suggestedBlock.EncodedBlockAccessList"), "copy.4", "processed ?? suggested"),
            Anchor("validator.generatedBlockAccessList-observation", blockValidator, ValidatorImplementationFqn,
                "global::Nethermind.Core.Block.GeneratedBlockAccessList",
                validatorMethod.DescendantNodes().OfType<AssignmentExpressionSyntax>().Single(assignment =>
                    Canonical(assignment) == "suggestedBlock.GeneratedBlockAccessList=processedBlock.GeneratedBlockAccessList"),
                "validator.mismatch", "proposed GeneratedBAL side effect"),
            Anchor("options.NoValidation", options, "global::Nethermind.Consensus.Processing.ProcessingOptions",
                "global::Nethermind.Consensus.Processing.ProcessingOptions.NoValidation",
                FindEnumMember(optionsEnum, "NoValidation"), "enum.value", "validation guard flag"),
            Anchor("options.StoreReceipts", options, "global::Nethermind.Consensus.Processing.ProcessingOptions",
                "global::Nethermind.Consensus.Processing.ProcessingOptions.StoreReceipts",
                FindEnumMember(optionsEnum, "StoreReceipts"), "enum.value", "receipt-store guard flag"),
        ];

        PublicationControlFlowIdentity[] controlFlows =
        [
            TypedFlow(context, "processOne", processOne),
            TypedFlow(context, "validateProcessedBlock", validateProcessedBlock),
            TypedFlow(context, "postValidation", postValidation),
            TypedFlow(context, "validator", validatorMethod),
            TypedFlow(context, "storeTxReceipts", storeReceipts),
        ];

        PublicationDataFlowIdentity[] dataFlows = TypedDataFlows(context, processOne, postValidation, validatorMethod);
        ValidatePublicationValueIdentities(context, processOne, validateProcessedBlock, postValidation,
            storeReceipts, validatorMethod, validatorContract);

        PublicationAdapterPremise[] adapterPremises =
        [
            new("exactBase", "runtime premise: exact BlockProcessor.ProcessOne receiver and exact-base PostValidation"),
            new("standardSequential", "runtime premise: standard BlockValidationTransactionsExecutor and sequential route"),
            new("balDisabled", "runtime premise: BAL manager disabled"),
            new("nonparallel", "runtime premise: ParallelExecutionEnabled=false; both retry catches excluded"),
            new("processBlockNormalReturn", "adapter premise: supplied normal ProcessBlock result with block and receipts"),
            new("validatorObservation", "adapter premise: validator ran iff NoValidation=false; accepted/rejected and proposed GeneratedBAL are observed"),
            new("rejectionCleanupNormalReturn", "adapter premise: rejection disposal and diagnostic logging complete before InvalidBlockException"),
            new("postValidationNormalReturn", "adapter premise: exact PostValidation returns normally"),
            new("storeNormalReturn", "adapter premise: InsertDeferred returns normally when StoreReceipts is selected"),
            new("noAdditionalEffects", "adapter premise: no other modeled field/identity mutation and no CommitTree/reset/head effects on normal return"),
        ];

        PublicationSourceIdentity[] sourceIdentities = sources.Select(static source =>
            new PublicationSourceIdentity(source.RelativePath, source.Role, source.Sha256, source.SyntaxSha256)).ToArray();
        ValidatePreservedSourceTokens(sourceIdentities);
        return new(
            SchemaVersion,
            ExtractorVersion,
            Kernel,
            AcceptanceState,
            CombinedSourceHash(sourceIdentities),
            sourceIdentities,
            context.CompilerClosure,
            synchronousCallables,
            anchors.ToArray(),
            controlFlows,
            dataFlows,
            adapterPremises,
            OrderedEvents,
            ForbiddenEffects,
            DependencyPaths,
            MutationChecks,
            OpenObligations);
    }

    private static PublicationSourceFile[] ReadSources(
        string root,
        SourcePin[] pins,
        IReadOnlyDictionary<string, byte[]>? overrides,
        bool enforcePins,
        bool enforceSourceSelection = true)
    {
        if (pins.Length != SourcePaths.Length)
            throw new ExtractionException("ProcessOne publication source pins must contain the exact eight-tree closure.");

        PublicationSourceFile[] sources = new PublicationSourceFile[pins.Length];
        for (int index = 0; index < pins.Length; index++)
        {
            SourcePin pin = pins[index];
            if (pin.Path != SourcePaths[index] || pin.Role != SourceRoles[index] || !IsSha256(pin.Sha256))
                throw new ExtractionException($"Invalid ProcessOne publication source pin at index {index}.");

            byte[] bytes;
            if (overrides is not null && overrides.TryGetValue(pin.Path, out byte[]? replacement))
            {
                bytes = replacement;
            }
            else
            {
                string path = Path.Combine(root, pin.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) throw new ExtractionException($"Missing pinned source '{pin.Path}'.");
                bytes = File.ReadAllBytes(path);
            }

            string sha = Sha256(bytes);
            if (enforcePins && !string.Equals(sha, pin.Sha256, StringComparison.Ordinal))
                throw new ExtractionException($"ProcessOne publication source pin changed for '{pin.Path}'.");

            string text;
            try
            {
                text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new ExtractionException($"ProcessOne publication source '{pin.Path}' is not UTF-8: {exception.Message}");
            }

            SyntaxTree tree = CSharpSyntaxTree.ParseText(text, ParseOptions, pin.Path);
            CompilationUnitSyntax syntaxRoot = (CompilationUnitSyntax)tree.GetRoot();
            if (enforceSourceSelection) ArtifactSafety.RequireUnconditionalSource(syntaxRoot, "Publication");
            Diagnostic[] errors = tree.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
            if (errors.Length != 0)
                throw new ExtractionException($"ProcessOne publication source '{pin.Path}' has syntax errors: {string.Join("; ", errors.Select(static error => error.ToString()))}");

            sources[index] = new(pin.Path, pin.Role, sha, Sha256(Encoding.UTF8.GetBytes(CanonicalTokens(syntaxRoot))), syntaxRoot, tree);
        }

        return sources;
    }

    private static PublicationPinDocument ReadPins(string root)
    {
        string path = Path.Combine(root, PinsPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) throw new ExtractionException($"Missing ProcessOne publication source pins '{PinsPath}'.");
        PublicationPinDocument pins = Deserialize<PublicationPinDocument>(File.ReadAllBytes(path));
        if (pins.SchemaVersion != 1 || pins.Status != "process-one-publication-source-pinned" || pins.Sources is null)
            throw new ExtractionException("ProcessOne publication source pins have an invalid status or schema.");
        return pins;
    }

    private static void ValidateProcessOneShape(MethodDeclarationSyntax method)
    {
        string[] suffix =
        [
            "TxReceipt[]receipts;",
            "boolprocessed=false;",
            "try{receipts=ProcessBlock(block,blockTracer,options,spec,token);processed=true;}" +
            "catch(BlockAccessListBasedWorldState.InvalidBlockLevelAccessListExceptionex)when(_balManager.ParallelExecutionEnabled)" +
            "{thrownewBlockAccessListSequentialRetryException(ex);}" +
            "catch(BlockAccessListManager.ParallelExecutionExceptionex)when(_balManager.ParallelExecutionEnabled&&" +
            "ex.InnerExceptionisBlockAccessListBasedWorldState.InvalidBlockLevelAccessListExceptionblockAccessListException)" +
            "{thrownewBlockAccessListSequentialRetryException(blockAccessListException);}" +
            "finally{if(!processed)block.DisposeAccountChanges();}",
            "ValidateProcessedBlock(suggestedBlock,options,block,receipts);",
            "if(options.ContainsFlag(ProcessingOptions.StoreReceipts)){StoreTxReceipts(block,receipts,spec);}",
            "return(block,receipts);",
        ];
        List<StatementSyntax> statements = method.Body?.Statements.ToList() ?? throw new ExtractionException("ProcessOne must have a block body.");
        int start = statements.FindIndex(statement => Canonical(statement) == suffix[0]);
        if (start <= 0 || !statements.Skip(start).Select(Canonical).SequenceEqual(suffix))
            throw new ExtractionException("ProcessOne normal-result suffix, guards, disposal, or exact return changed.");
        if (Canonical(statements[start - 1]) != "Blockblock=PrepareBlockForProcessing(suggestedBlock);")
            throw new ExtractionException("ProcessOne processed block identity changed.");
    }

    private static void ValidateValidationShape(MethodDeclarationSyntax method)
    {
        const string expected = "{if(!options.ContainsFlag(ProcessingOptions.NoValidation)&&" +
            "!blockValidator.ValidateProcessedBlock(block,receipts,suggestedBlock,outstring?error))" +
            "{block.DisposeAccountChanges();if(_logger.IsWarn)_logger.Warn(InvalidBlockHelper.GetMessage(suggestedBlock,\"invalid block after processing\"));" +
            "thrownewInvalidBlockException(suggestedBlock,error);}" +
            "PostValidation(suggestedBlock,block,receipts,options);}";
        if (method.Body is null || Canonical(method.Body) != expected)
            throw new ExtractionException("Validation must retain the exact short-circuit guard, rejection cleanup/throw, and PostValidation suffix.");
    }

    private static void ValidatePostValidationShape(MethodDeclarationSyntax method)
    {
        string[] expected =
        [
            "suggestedBlock.AccountChanges=processedBlock.AccountChanges",
            "suggestedBlock.ExecutionRequests=processedBlock.ExecutionRequests",
            "suggestedBlock.GeneratedBlockAccessList=processedBlock.GeneratedBlockAccessList",
            "suggestedBlock.EncodedBlockAccessList=processedBlock.EncodedBlockAccessList??suggestedBlock.EncodedBlockAccessList",
        ];
        string[] actual = method.Body?.Statements
            .Select(static statement => statement is ExpressionStatementSyntax expression ? Canonical(expression.Expression) : "unsupported-statement").ToArray()
            ?? throw new ExtractionException("PostValidation must have a block body.");
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
            throw new ExtractionException("PostValidation four-field publication order or fallback changed.");
    }

    private static void ValidateStoreShape(MethodDeclarationSyntax method)
    {
        string actual = method.ExpressionBody is not null
            ? Canonical(method.ExpressionBody.Expression)
            : method.Body?.Statements.SingleOrDefault() is ExpressionStatementSyntax statement
                ? Canonical(statement.Expression)
                : string.Empty;
        if (actual != "receiptStorage.InsertDeferred(block,txReceipts,spec)")
            throw new ExtractionException("StoreTxReceipts must be the exact InsertDeferred call.");
    }

    private static void ValidateValidatorShape(MethodDeclarationSyntax method)
    {
        AssignmentExpressionSyntax[] writes = method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(assignment => Canonical(assignment) == "suggestedBlock.GeneratedBlockAccessList=processedBlock.GeneratedBlockAccessList")
            .ToArray();
        if (writes.Length != 1)
            throw new ExtractionException("Validator GeneratedBlockAccessList observation must have exactly one source write.");
        AssignmentExpressionSyntax write = writes[0];
        IfStatementSyntax[] guards = write.Ancestors().OfType<IfStatementSyntax>().ToArray();
        if (guards.Length != 1 || Canonical(guards[0].Condition) !=
            "processedBlock.Header.BlockAccessListHash!=suggestedBlock.Header.BlockAccessListHash" ||
            guards[0].Parent != method.Body || guards[0].Else is not null ||
            write.Parent?.Parent != guards[0].Statement ||
            method.Body?.Statements.LastOrDefault() is not ReturnStatementSyntax returned || Canonical(returned) != "returnfalse;")
            throw new ExtractionException("Validator proposed GeneratedBAL write is not in the exact mismatch-to-rejection arm.");
        if (method.DescendantNodes().OfType<AssignmentExpressionSyntax>().Count(assignment =>
                Canonical(assignment.Left) == "suggestedBlock.GeneratedBlockAccessList") != 1)
            throw new ExtractionException("Validator has a competing proposed GeneratedBAL write.");
    }

    private static void ValidateValidatorContract(InterfaceDeclarationSyntax contract)
    {
        MethodDeclarationSyntax method = contract.Members.OfType<MethodDeclarationSyntax>()
            .SingleOrDefault(item => item.Identifier.ValueText == "ValidateProcessedBlock" && item.ParameterList.Parameters.Count == 4)
            ?? throw new ExtractionException("IBlockValidator ValidateProcessedBlock contract is missing.");
        if (Canonical(method) != "boolValidateProcessedBlock(BlockprocessedBlock,TxReceipt[]receipts,BlocksuggestedBlock,[NotNullWhen(false)]outstring?error);")
            throw new ExtractionException("IBlockValidator ValidateProcessedBlock symbol shape changed.");
    }

    private static void ValidateOptionsShape(EnumDeclarationSyntax options)
    {
        EnumMemberDeclarationSyntax noValidation = FindEnumMember(options, "NoValidation");
        EnumMemberDeclarationSyntax storeReceipts = FindEnumMember(options, "StoreReceipts");
        if (Canonical(noValidation) != "NoValidation=8" || Canonical(storeReceipts) != "StoreReceipts=4")
            throw new ExtractionException("ProcessingOptions NoValidation/StoreReceipts values changed.");
        MethodDeclarationSyntax? predicate = options.SyntaxTree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .SingleOrDefault(method => method.Identifier.ValueText == "ContainsFlag");
        if (predicate?.ExpressionBody is null || Canonical(predicate.ExpressionBody.Expression) != "(processingOptions&flag)==flag")
            throw new ExtractionException("ProcessingOptions ContainsFlag predicate changed.");
    }

    private static void RejectForbiddenProcessOneEffects(MethodDeclarationSyntax method)
    {
        string source = method.ToString();
        foreach (string forbidden in new[] { "CommitTree", ".Reset(", "UpdateHead", "Head =", "head =" })
        {
            if (source.Contains(forbidden, StringComparison.Ordinal))
                throw new ExtractionException($"Forbidden ProcessOne boundary effect '{forbidden}' is reachable.");
        }
    }


    private static AssignmentExpressionSyntax FindProcessBlockAssignment(MethodDeclarationSyntax method) =>
        method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .SingleOrDefault(assignment => Canonical(assignment) == "receipts=ProcessBlock(block,blockTracer,options,spec,token)")
        ?? throw new ExtractionException("Missing exact ProcessBlock result assignment.");

    private static AssignmentExpressionSyntax FindProcessedTrue(MethodDeclarationSyntax method) =>
        method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .SingleOrDefault(assignment => Canonical(assignment) == "processed=true")
        ?? throw new ExtractionException("Missing exact processed=true assignment.");

    private static CatchClauseSyntax FindCatch(MethodDeclarationSyntax method, int ordinal) =>
        method.DescendantNodes().OfType<CatchClauseSyntax>().ElementAtOrDefault(ordinal)
        ?? throw new ExtractionException($"Missing ProcessOne retry catch {ordinal}.");

    private static IfStatementSyntax FindDisposal(MethodDeclarationSyntax method) =>
        method.DescendantNodes().OfType<IfStatementSyntax>()
            .SingleOrDefault(item => Canonical(item.Condition) == "!processed" &&
                item.Statement.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
                    .Any(invocation => Canonical(invocation) == "block.DisposeAccountChanges()"))
        ?? throw new ExtractionException("Missing ProcessOne finally disposal.");

    private static IfStatementSyntax FindValidationIf(MethodDeclarationSyntax method) =>
        method.DescendantNodes().OfType<IfStatementSyntax>()
            .SingleOrDefault(item => Canonical(item.Condition).Contains("ProcessingOptions.NoValidation", StringComparison.Ordinal))
        ?? throw new ExtractionException("Missing NoValidation validator guard.");

    private static InvocationExpressionSyntax FindInvocation(SyntaxNode node, string canonical) =>
        node.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
            .SingleOrDefault(invocation => Canonical(invocation) == canonical)
        ?? throw new ExtractionException($"Missing exact invocation '{canonical}'.");

    private static InvocationExpressionSyntax FindValidationDisposal(MethodDeclarationSyntax method) =>
        FindInvocation(method, "block.DisposeAccountChanges()");

    private static ObjectCreationExpressionSyntax FindInvalidBlockThrow(MethodDeclarationSyntax method) =>
        method.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .SingleOrDefault(creation => Canonical(creation).StartsWith("newInvalidBlockException(", StringComparison.Ordinal))
        ?? throw new ExtractionException("Missing InvalidBlockException construction.");

    private static IfStatementSyntax FindStoreIf(MethodDeclarationSyntax method) =>
        method.DescendantNodes().OfType<IfStatementSyntax>()
            .SingleOrDefault(item => Canonical(item.Condition).Contains("ProcessingOptions.StoreReceipts", StringComparison.Ordinal))
        ?? throw new ExtractionException("Missing StoreReceipts guard.");

    private static ReturnStatementSyntax FindReturn(MethodDeclarationSyntax method) =>
        method.Body?.Statements.OfType<ReturnStatementSyntax>().SingleOrDefault()
        ?? throw new ExtractionException("Missing ProcessOne return.");

    private static AssignmentExpressionSyntax FindAssignment(MethodDeclarationSyntax method, string canonical) =>
        method.Body?.Statements.OfType<ExpressionStatementSyntax>()
            .Select(static statement => statement.Expression)
            .OfType<AssignmentExpressionSyntax>()
            .SingleOrDefault(assignment => Canonical(assignment) == canonical)
        ?? throw new ExtractionException($"Missing exact assignment '{canonical}'.");

    private static EnumMemberDeclarationSyntax FindEnumMember(EnumDeclarationSyntax options, string name) =>
        options.Members.SingleOrDefault(member => member.Identifier.ValueText == name)
        ?? throw new ExtractionException($"Missing ProcessingOptions member '{name}'.");

    private static ClassDeclarationSyntax FindClass(CompilationUnitSyntax root, string name) =>
        root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .SingleOrDefault(item => item.Identifier.ValueText == name)
        ?? throw new ExtractionException($"Missing source class '{name}'.");

    private static MethodDeclarationSyntax FindMethod(ClassDeclarationSyntax type, string name, int parameterCount) =>
        type.Members.OfType<MethodDeclarationSyntax>()
            .SingleOrDefault(item => item.Identifier.ValueText == name && item.ParameterList.Parameters.Count == parameterCount)
        ?? throw new ExtractionException($"Missing source method '{name}/{parameterCount}'.");

    private static void ValidateDocument(ProcessOnePublicationIrDocument document)
    {
        if (document.SchemaVersion != SchemaVersion || document.ExtractorVersion != ExtractorVersion ||
            document.Kernel != Kernel || document.AcceptanceState != AcceptanceState ||
            !IsSha256(document.SourceClosureSha256))
            throw new ExtractionException("ProcessOne publication IR header is not admitted.");
        if (!document.Sources.Select(static source => source.Path).SequenceEqual(SourcePaths) ||
            !document.Sources.Select(static source => source.Role).SequenceEqual(SourceRoles) ||
            document.Sources.Any(source => !IsSha256(source.Sha256) || !IsSha256(source.SyntaxSha256)))
            throw new ExtractionException("ProcessOne publication IR source closure is not exact.");
        if (document.SourceClosureSha256 != CombinedSourceHash(document.Sources))
            throw new ExtractionException("Publication source closure digest does not match its identities.");
        ValidatePreservedSourceTokens(document.Sources);
        ValidatePublicationCompilerClosure(document.CompilerClosure);
        ValidatePublicationSynchronousInventory(document.SynchronousCallables);
        if (!document.Anchors.Select(static anchor => anchor.Id).SequenceEqual(AnchorIds) ||
            document.Anchors.Any(anchor => string.IsNullOrWhiteSpace(anchor.OwnerFqn) ||
                string.IsNullOrWhiteSpace(anchor.SymbolFqn) || string.IsNullOrWhiteSpace(anchor.CanonicalSyntax) ||
                anchor.StartLine <= 0 || anchor.EndLine < anchor.StartLine || !anchor.IsReachable))
            throw new ExtractionException("ProcessOne publication anchors are missing, reordered, or incomplete.");
        if (document.ControlFlows.Length != 5 || document.ControlFlows.Any(flow =>
            flow.ReachableRegions.Length == 0 || flow.Edges.Length == 0 || !IsSha256(flow.ShapeSha256)))
            throw new ExtractionException("ProcessOne publication CFG records are incomplete.");
        foreach (PublicationAnchor anchor in document.Anchors)
        {
            PublicationSourceIdentity? source = document.Sources.SingleOrDefault(item => item.Path == anchor.Path);
            if (source is null || source.SyntaxSha256 != anchor.SourceSyntaxSha256 || string.IsNullOrWhiteSpace(anchor.OperationKind))
                throw new ExtractionException("Publication anchor source or operation identity changed.");
            if (anchor.ControlFlowBlock < 0)
            {
                if (!anchor.Id.StartsWith("options.", StringComparison.Ordinal))
                    throw new ExtractionException("An executable publication anchor lost its CFG binding.");
                continue;
            }
            PublicationControlFlowIdentity? flow = document.ControlFlows.SingleOrDefault(item => item.OwnerFqn == anchor.OwnerFqn);
            if (flow is null || !flow.BlockMemberships.Any(block => block.Ordinal == anchor.ControlFlowBlock &&
                block.SyntaxStarts.Contains(anchor.Position)))
                throw new ExtractionException("Publication anchor is outside its actual CFG block membership.");
        }
        if (document.DataFlows.Length != 4 || document.DataFlows.Any(flow =>
            string.IsNullOrWhiteSpace(flow.SymbolFqn) || string.IsNullOrWhiteSpace(flow.Definition) ||
            flow.Reads.Length == 0 || flow.Writes.Length == 0 || !flow.DataFlowSucceeded))
            throw new ExtractionException("ProcessOne publication data-flow records are incomplete.");
        if (!document.AdapterPremises.Select(static premise => premise.Id).SequenceEqual(AdapterPremiseIds) ||
            document.AdapterPremises.Any(static premise => string.IsNullOrWhiteSpace(premise.Statement)))
            throw new ExtractionException("ProcessOne publication adapter premises are incomplete.");
        if (!document.OrderedEvents.SequenceEqual(OrderedEvents) ||
            !document.ForbiddenEffects.SequenceEqual(ForbiddenEffects) ||
            !document.Dependencies.SequenceEqual(DependencyPaths) ||
            !document.MutationChecks.SequenceEqual(MutationChecks) ||
            !document.OpenObligations.SequenceEqual(OpenObligations))
            throw new ExtractionException("ProcessOne publication order/effect/obligation schema drifted.");
    }

    private static void ValidateManifest(
        ProcessOnePublicationManifest manifest,
        ProcessOnePublicationIrDocument document,
        string irHash,
        string leanHash)
    {
        if (manifest.SchemaVersion != SchemaVersion || manifest.ExtractorVersion != ExtractorVersion ||
            manifest.Kernel != Kernel || manifest.AcceptanceState != AcceptanceState ||
            manifest.SourceClosureSha256 != document.SourceClosureSha256 ||
            !SerializedSequenceEqual(manifest.Sources, document.Sources) ||
            !Serialize(manifest.CompilerClosure).AsSpan().SequenceEqual(Serialize(document.CompilerClosure)) ||
            !SerializedSequenceEqual(manifest.SynchronousCallables, document.SynchronousCallables) ||
            !SerializedSequenceEqual(manifest.ControlFlows, document.ControlFlows) ||
            !SerializedSequenceEqual(manifest.DataFlows, document.DataFlows) ||
            !SerializedSequenceEqual(manifest.AdapterPremises, document.AdapterPremises) ||
            manifest.Dependencies.Length != DependencyPaths.Length ||
            !manifest.Dependencies.Select(static dependency => dependency.Path).SequenceEqual(DependencyPaths) ||
            manifest.Ir.Path != IrFileName || manifest.Lean.Path != DefaultLeanPath ||
            manifest.Ir.Sha256 != irHash || manifest.Lean.Sha256 != leanHash ||
            !manifest.MutationChecks.SequenceEqual(MutationChecks) || !manifest.OpenObligations.SequenceEqual(OpenObligations))
            throw new ExtractionException("ProcessOne publication source manifest does not match its IR/artifacts.");
    }

    private static void ValidateDependencyIdentities(string root, PublicationDependencyIdentity[] dependencies)
    {
        if (dependencies is null || dependencies.Length != DependencyPaths.Length ||
            !dependencies.Select(static dependency => dependency.Path).SequenceEqual(DependencyPaths) ||
            dependencies.Any(static dependency => !IsSha256(dependency.Sha256)))
            throw new ExtractionException("ProcessOne publication dependency identities are incomplete.");

        foreach (PublicationDependencyIdentity dependency in dependencies)
        {
            string path = Path.Combine(root, dependency.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path) || !string.Equals(Sha256File(root, dependency.Path), dependency.Sha256, StringComparison.Ordinal))
                throw new ExtractionException($"ProcessOne publication dependency changed: '{dependency.Path}'.");
        }
    }

    private static void ValidateSettledDependencies(string root)
    {
        string pinsPath = Path.Combine(root, ReceiptPinsPath.Replace('/', Path.DirectorySeparatorChar));
        string manifestPath = Path.Combine(root, ReceiptManifestPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(pinsPath) || !File.Exists(manifestPath))
            throw new ExtractionException("Settled ReceiptTerminal artifacts are missing; ProcessOne regeneration is blocked.");
        ValidateReceiptDependency(File.ReadAllBytes(pinsPath), File.ReadAllBytes(manifestPath), path =>
            File.Exists(Path.Combine(root, path)) ? Sha256File(root, path) : null);
        Extractor.ValidateCheckedIn(root, null);
        ValidatePublicationUpstreamPins(root);
    }

    private static bool IsStaticDraft(JsonElement document) =>
        document.TryGetProperty("acceptanceState", out JsonElement acceptance) &&
        acceptance.ValueKind == JsonValueKind.String && acceptance.GetString() == StaticDraftState;

    private static bool ContainsProofPlaceholder(string source) =>
        ArtifactSafety.ContainsProofToken(source, generated: true);

    private static JsonDocument ReadJson(string path)
    {
        try
        {
            JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
            try { ArtifactSafety.ValidateJson(document.RootElement); return document; }
            catch { document.Dispose(); throw; }
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"Invalid JSON artifact '{path}': {exception.Message}");
        }
    }

    private static string CombinedSourceHash(IEnumerable<PublicationSourceIdentity> sources) =>
        Sha256(Encoding.UTF8.GetBytes(string.Join("\n", sources.Select(source =>
            $"{source.Path}|{source.Role}|{source.Sha256}|{source.SyntaxSha256}"))));

    private static string Canonical(SyntaxNode node) =>
        string.Concat(node.DescendantTokens().Select(static token => token.Text));

    private static string CanonicalTokens(CompilationUnitSyntax root) =>
        string.Join(";", root.DescendantTokens().Select(token => $"{token.RawKind}:{token.Text}"));

    private static string Sha256File(string root, string relativePath) =>
        Sha256(File.ReadAllBytes(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar))));

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsSha256(string value) => value.Length == 64 && value.All(static character =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static T Deserialize<T>(byte[] bytes)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            ArtifactSafety.ValidateJson(document.RootElement);
            return JsonSerializer.Deserialize<T>(bytes, JsonOptions)
                ?? throw new ExtractionException("JSON artifact deserialized to null.");
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"Publication JSON schema failure: {exception.Message}");
        }

    }

    private static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);

    private static bool SerializedSequenceEqual<T>(T[] left, T[] right) =>
        string.Equals(JsonSerializer.Serialize(left, JsonOptions), JsonSerializer.Serialize(right, JsonOptions),
            StringComparison.Ordinal);

    private static void EnsureWithin(string directory, string path)
    {
        string fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase))
            throw new ExtractionException("ProcessOne publication artifact escaped its output directory.");
    }

    private static void AtomicWrite(string path, byte[] bytes) => ArtifactSafety.AtomicWrite(path, bytes);

}
