// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.SynchronousBlockPipelineMachineExtractor;

/// <summary>Names the externally relevant phases of the synchronous standard-mainnet route.</summary>
internal enum PipelinePhase
{
    SuggestedBlockValidation,
    SynchronousBranchSelectionAndPreparation,
    SenderAndAuthorityRecovery,
    OpenWorldStateScope,
    DaoTransition,
    BeaconRootSystemCall,
    HistoricalBlockhashStateChange,
    UserTransactionFold,
    BlobGasReceiptRootAndBloom,
    Rewards,
    Withdrawals,
    ExecutionRequestsAndSystemCalls,
    StorageAndStateRoots,
    BlockAccessList,
    ProcessedHeaderValidation,
    CommitTreeInvocation,
    SynchronousResultClassificationAndHeadFinalization,
}

/// <summary>Classifies the externally visible result without conflating signals and exceptions.</summary>
internal enum LogicalOutcome
{
    Accepted,
    Skipped,
    RejectedInvalidBlock,
    Escaped,
}

/// <summary>Identifies why a route terminated.</summary>
internal enum FailureKind
{
    UnknownParentSkip,
    MissingTotalDifficulty,
    MissingBlockHash,
    MissingUncleHash,
    InvalidBlock,
    GenericException,
    Cancellation,
    RetryableBalFailure,
    BackgroundReceiptFailure,
    ScopeFailure,
    CommitTreeFailure,
    HeadUpdateException,
}

/// <summary>Describes the production handling promised by a hook contract.</summary>
internal enum FailureDisposition
{
    Signal,
    RejectInvalidBlock,
    RetrySequential,
    Escape,
}

/// <summary>Chooses the two source-backed receipt-artifact scheduling arms.</summary>
internal enum ReceiptArtifactMode
{
    Synchronous,
    Background,
}

/// <summary>Stable names for the source-backed hook dependency catalog.</summary>
internal enum HookId
{
    SuggestedBlockSimpleChecks,
    EvaluateEligibility,
    SelectAndPrepareBranch,
    RecoverSignatures,
    RecoverSignatureImplementation,
    RecoverAuthorities,
    BeginBranchScope,
    BeginGenesisScope,
    PrepareBal,
    SelectSystemContractHandler,
    ApplyDaoTransition,
    PrepareBlockForProcessing,
    SetOtherTracer,
    StartBlockTrace,
    SetBlockExecutionContext,
    SetupBlockAccessList,
    StoreBeaconRoot,
    ApplyBlockhashStateChanges,
    CommitPreTransactionState,
    ExecuteTransactionFold,
    TransactionsExecutedSignal,
    CommitPostTransactionState,
    BuildReceiptsAndCumulativePaidGas,
    CalculateBlobGas,
    CalculateReceiptBlooms,
    AccumulateBlockBloom,
    CalculateReceiptsRoot,
    InstallSynchronousReceiptArtifacts,
    ScheduleBackgroundReceiptArtifacts,
    AwaitBackgroundReceiptArtifacts,
    ApplyMinerRewards,
    ProcessWithdrawals,
    CommitPostSystemState,
    ProcessExecutionRequests,
    EndBlockTrace,
    CommitStorageAndStateRoots,
    SetAccountChanges,
    ComputeStateRoot,
    FinalizeBal,
    ValidateProcessedBlock,
    DisposeAccountChanges,
    DisposeRetryScope,
    ReopenRetryScope,
    DisposeCheckpointScope,
    ReopenCheckpointScope,
    WaitPrewarm,
    CommitTree,
    ResetScope,
    DisposeBranchScope,
    RecordInclusionListSignal,
    IncrementSuccessfulPrefix,
    CatchBranchException,
    CompleteBranchProcessing,
    ClassifySynchronousResult,
    UpdateTotalDifficulty,
    TryUpdateMainChain,
    MarkChainAsProcessed,
}

/// <summary>Gas dimensions retained by the reference boundary.</summary>
internal sealed record GasCounters(
    ulong ExecutionGas,
    ulong StateGas,
    ulong CumulativePaidGas,
    ulong HeaderGasUsed);

/// <summary>A log retained so receipt bloom calculation cannot be treated as a no-op.</summary>
internal sealed record LogObservation(string Address, IReadOnlyList<string> Topics, string Data);

/// <summary>Receipt fields consumed by counters, requests and block artifacts.</summary>
internal sealed record ReceiptObservation(
    string TransactionId,
    string Status,
    ulong GasUsed,
    ulong CumulativeGasUsed,
    IReadOnlyList<LogObservation> Logs,
    string LogsBloom,
    string? ContractAddress);

/// <summary>Receipt artifact observations are explicit about the selected source arm.</summary>
internal sealed record ReceiptArtifactObservation(
    ReceiptArtifactMode Mode,
    bool ReceiptsUpdatedBeforeRoot,
    bool RootInstalledBeforeTraceEnd,
    bool BackgroundTaskAwaited,
    bool FailureObserved);

/// <summary>Proposed-header fields that processing may preserve or overwrite.</summary>
internal sealed record HeaderObservation(
    string ParentHash,
    string UnclesHash,
    string Beneficiary,
    string? Author,
    string StateRoot,
    string TransactionsRoot,
    string ReceiptsRoot,
    string Bloom,
    ulong Difficulty,
    ulong Number,
    ulong GasUsed,
    ulong GasLimit,
    ulong Timestamp,
    string ExtraData,
    string MixHash,
    string Nonce,
    ulong TotalDifficulty,
    ulong? BaseFeePerGas,
    string? WithdrawalsRoot,
    string? ParentBeaconBlockRoot,
    string? RequestsHash,
    string? BlockAccessListHash,
    ulong? BlobGasUsed,
    ulong? ExcessBlobGas,
    bool IsPostMerge,
    ulong? SlotNumber);

/// <summary>Processing-header projection; reset fields are explicit rather than inferred.</summary>
internal sealed record ProcessingHeaderObservation(
    HeaderObservation Proposed,
    HeaderObservation Processing,
    IReadOnlyList<string> ResetFields,
    IReadOnlyList<string> PreservedFields);

/// <summary>Logical state is separate from physical persistence and hashing.</summary>
internal sealed record LogicalState(
    string StateToken,
    ulong JournalVersion,
    IReadOnlyList<string> AccountChanges);

/// <summary>Tracks ownership and lifecycle of the branch world-state scope.</summary>
internal sealed record ScopeState(
    bool IsOpen,
    bool ExternallyOwned,
    string? BaseBlock,
    int Depth,
    ulong Generation);

/// <summary>All state observable at the typed pipeline boundary.</summary>
internal sealed record BlockWorld(
    LogicalState LogicalState,
    ScopeState Scope,
    GasCounters Gas,
    ProcessingHeaderObservation Header,
    IReadOnlyList<ReceiptObservation> Receipts,
    ReceiptArtifactObservation ReceiptArtifacts,
    string? RequestsHash,
    string? BlockAccessListHash,
    string? StateRoot,
    bool? InclusionListSatisfied,
    IReadOnlyList<string> Trace,
    IReadOnlyList<string> Observations);

/// <summary>One transaction's ordinary/system and two-dimensional gas boundary.</summary>
internal sealed record TransactionObservation(
    string TransactionId,
    bool IsSystem,
    ulong PaidGas,
    ulong ExecutionGas,
    ulong StateGas,
    ReceiptObservation Receipt);

/// <summary>Input facts required to select and process one suggested block.</summary>
internal sealed record BlockInput(
    string BlockId,
    string? ParentBlock,
    bool IsGenesis,
    bool IsEligible,
    bool SimpleChecksPass,
    bool Preprocessed,
    bool ReadOnly,
    HeaderObservation ProposedHeader,
    IReadOnlyList<TransactionObservation> Transactions,
    IReadOnlyList<string> Options);

/// <summary>Failure detail retained independently of a caught invalid-block result.</summary>
internal sealed record PipelineFailure(
    FailureKind Kind,
    string Message,
    bool CaughtAsInvalidBlock,
    int? FailedIndex,
    bool ScopeRestored,
    bool CommitTreeAttempted,
    IReadOnlyList<string> CompletedEffects);

/// <summary>Result of a one-block machine boundary.</summary>
internal sealed record BlockPipelineResult(
    LogicalOutcome Outcome,
    BlockWorld ReturnedWorld,
    PipelineFailure? Failure,
    IReadOnlyList<PipelinePhase> CompletedPhases,
    bool CommitTreeInvoked);

/// <summary>Typed scope operations matching the nonnegative indexed production events.</summary>
internal enum ScopeOperation
{
    Opened,
    GenesisExternallyOwned,
    DisposedForRetry,
    ReopenedForRetry,
    DisposedAtCheckpoint,
    ReopenedAtCheckpoint,
    ResetAfterCommit,
    DisposedAtBranchEnd,
}

/// <summary>Observed scope operation, including retry and checkpoint lifecycle ordering.</summary>
internal sealed record ScopeEvent(ScopeOperation Operation, string BlockId, uint? Index, ulong Generation);

/// <summary>Observed first attempt and optional forced-sequential retry.</summary>
internal sealed record ParallelAttempt(
    bool Eligible,
    bool WasAttempted,
    bool Completed,
    bool RetryableBalFailure,
    bool SequentialRetryForced,
    IReadOnlyList<string> FailedAttemptEffects,
    IReadOnlyList<ScopeEvent> ScopeEvents);

/// <summary>Branch result preserving canonical state separately from committed-prefix evidence.</summary>
internal sealed record BranchPipelineResult(
    LogicalOutcome Outcome,
    BlockWorld ReturnedWorld,
    BlockWorld CommittedPrefixWorld,
    IReadOnlyList<BlockPipelineResult> Blocks,
    IReadOnlyList<ScopeEvent> ScopeEvents,
    PipelineFailure? Failure,
    ParallelAttempt? ParallelAttempt,
    int? FailedIndex,
    int CommitTreeInvocationCount);

/// <summary>Outer chain finalization signals are not converted into invalid-block rejection.</summary>
internal enum ChainFinalizationStep
{
    TotalDifficultyAssigned,
    MainChainUpdateAttempted,
    MarkProcessedAttempted,
}

/// <summary>Outer chain finalization signals are not converted into invalid-block rejection.</summary>
internal sealed record ChainFinalizationObservation(
    bool TotalDifficultyUpdated,
    bool MainChainUpdateReturnedTrue,
    bool MarkProcessedInvoked,
    bool MarkProcessedCompleted,
    IReadOnlyList<ChainFinalizationStep> CompletedSteps,
    IReadOnlyList<string> Effects,
    PipelineFailure? EscapedFailure);

/// <summary>Preserves the production distinction between pre-branch returns and branch results.</summary>
internal abstract record ChainPipelineResult
{
    /// <summary>A simple-check, unknown-parent, or not-better-than-head path with no branch witness.</summary>
    internal sealed record PreBranch(
        LogicalOutcome Outcome,
        ChainFinalizationObservation? Finalization,
        IReadOnlyList<PipelinePhase> Trace,
        PipelineFailure? Failure) : ChainPipelineResult;

    /// <summary>A path that actually entered the selected branch and may finalize it.</summary>
    internal sealed record Branched(
        LogicalOutcome Outcome,
        BranchPipelineResult Branch,
        ChainFinalizationObservation? Finalization,
        IReadOnlyList<PipelinePhase> Trace,
        PipelineFailure? Failure) : ChainPipelineResult;
}

/// <summary>A source symbol that must remain present before extraction can be considered.</summary>
internal sealed record SourceSymbol(
    string Path,
    string Namespace,
    string Type,
    string? Member,
    int? ParameterCount,
    string Role);

/// <summary>An ordered source-site anchor, checked against the actual syntax projection.</summary>
internal sealed record OrderedAnchor(
    string Id,
    string Path,
    string Owner,
    string CanonicalText);

/// <summary>Static membership mapping from the 17-label trace to source-backed hook names.</summary>
internal sealed record PhaseContract(
    PipelinePhase Phase,
    string Label,
    HookId[] Hooks);

/// <summary>Static dependency contract; it is not a caller-supplied semantic relation.</summary>
internal sealed record HookDependency(
    HookId Hook,
    PipelinePhase Phase,
    string ProductionPath,
    string ProductionSymbol,
    string InputShape,
    string OutputShape,
    FailureDisposition FailureDisposition,
    bool MayMutateLogicalState,
    bool ReceiptDependent,
    bool RequiresScope);

/// <summary>A source-backed partial-order edge; hook catalogs do not imply one flat runtime order.</summary>
internal sealed record OrderEdge(
    string Id,
    string Path,
    string Type,
    string Method,
    int ParameterCount,
    string Before,
    string After);

/// <summary>Audit profile boundary embedded in the package.</summary>
internal sealed record AuditProfile(
    int SchemaVersion,
    string Status,
    AuditBoundary Boundary,
    SourcePin[] Pins,
    ConfigurationPin[] ConfigurationPins,
    SpecificationPin[] SpecificationPins,
    CompositionPin[] CompositionPins);

/// <summary>Describes the route whose identities are pinned by an audit profile.</summary>
internal sealed record AuditBoundary(
    string Processor,
    string Implementation,
    string BranchProcessor,
    string BlockchainProcessor,
    string SpecProvider,
    string SpecDerivation,
    string MainnetConfigPath,
    string MainnetChainSpecPath,
    string Fork,
    bool ForkActivationConfigured,
    bool GeneratedKernelPresent);

/// <summary>One immutable source identity and its admission status.</summary>
internal sealed record SourcePin(
    string Path,
    string Role,
    string Kind,
    string Sha256,
    bool CompositionAdmitted);

/// <summary>A non-C# configuration or chain-spec byte identity used by the route derivation.</summary>
internal sealed record ConfigurationPin(
    string Path,
    string Role,
    string Kind,
    string Sha256);

/// <summary>An exact byte identity for one handwritten Lean scaffold input.</summary>
internal sealed record SpecificationPin(
    string Path,
    string Role,
    string Kind,
    string Sha256);

/// <summary>An exact byte identity for a pre-existing handwritten composition artifact.</summary>
internal sealed record CompositionPin(
    string Artifact,
    string Path,
    string Role,
    string Sha256,
    bool AcceptedByteIdentity);

/// <summary>Names the exact artifact-to-input ownership expected by the composition boundary.</summary>
internal sealed record CompositionRequirement(string Artifact, string Path);

/// <summary>Names one exact path and kind required by the configuration boundary.</summary>
internal sealed record FileRequirement(string Path, string Kind);

/// <summary>One exact cross-language hook contract in the checked metadata mirror.</summary>
internal sealed record MirrorHook(
    string Name,
    string Phase,
    string ProductionPath,
    string ProductionSymbol,
    string InputShape,
    string OutputShape,
    string FailureDisposition,
    bool MayMutateLogicalState,
    bool ReceiptDependent,
    bool RequiresScope);

/// <summary>JSON mirror consumed by C# and reproduced as Lean constants.</summary>
internal sealed record MetadataMirror(
    int SchemaVersion,
    string Route,
    string[] PhaseLabels,
    MirrorHook[] Hooks);

/// <summary>Result returned by the static source audit.</summary>
internal sealed record AuditResult(
    string Route,
    int SourceCount,
    int SymbolCount,
    int AnchorCount,
    int HookCount,
    int PhaseCount,
    IReadOnlyList<string> OpenBoundaries,
    bool ExtractionClosed);
