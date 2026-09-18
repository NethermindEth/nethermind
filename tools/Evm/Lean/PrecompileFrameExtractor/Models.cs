// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.PrecompileFrameExtractor;

internal sealed class ExtractionException(string message) : Exception(message);

internal enum ActivationRule
{
    Always,
    Eip198,
    Eip196And197,
    Eip152,
    Eip4844,
    Eip2537,
    Eip7212Or7951,
}

internal enum RouteMode
{
    InactiveCode,
    FullFrame,
    DirectStaticCall,
    DelegatedPrecompileSuppressed,
}

internal enum PricingOutcome
{
    Success,
    BaseDataOverflow,
    OutOfGas,
}

internal enum LeafOutcome
{
    Success,
    ReturnedFailure,
    ManagedException,
    MissingDependency,
}

internal enum CacheOutcome
{
    Uncached,
    Hit,
    Miss,
    InvalidInput,
}

internal enum CacheEffect
{
    ReturnUncached,
    NormalizeInput,
    BuildKey,
    Lookup,
    ReturnCached,
    RunOriginalInput,
    UpdateCache,
    SkipCacheUpdate,
}

internal enum TrySaveFailureEffect
{
    ReturnFalse,
    NoUpdateSize,
    NoSaveAfterGas,
    NoMemoryMutation,
    NoOutputWrite,
}

internal enum CallKind
{
    Call,
    CallCode,
    DelegateCall,
    StaticCall,
}

internal enum CallTargetKind
{
    CodeSource,
    ExecutingAccount,
}

internal enum FrameOutcome
{
    PricingHardFailure,
    ReturnedLeafHardFailure,
    ManagedNestedSoftRevert,
    Success,
}

internal enum DirectFrameOutcome
{
    PricingHardFailure,
    ReturnedLeafHardFailure,
    ManagedNestedSoftRevert,
    OutputCopyOutOfGas,
    Success,
}

internal enum DirectResult
{
    StackFailure,
    OutOfGas,
    StackSuccess,
}

internal enum FrameEffect
{
    ActionTrace,
    TransferLog,
    AccountTouchOrCreate,
    RipemdTouchLatch,
    Pricing,
    Run,
    ChildRefund,
    ChildCommit,
    StateGasRestore,
    SnapshotRestore,
    ExecutionGasClear,
    ReturnDataClear,
    HandleRevert,
    HandleReturn,
    ReturnData,
    ReturnOutOfGas,
    OutputCopy,
    StackSuccess,
    StackFailure,
}

internal sealed record AddressEntry(string Name, int Address);

internal sealed record ForkActivationStep(
    string Fork,
    string Parent,
    string SourcePath,
    string[] EnabledProperties);

internal sealed record CacheBranch(
    CacheOutcome Outcome,
    CacheEffect[] Effects,
    bool ReturnedFromCache,
    bool RunsLeaf,
    bool CacheUpdated);

internal sealed record CacheKeyDescriptor(
    string[] Fields,
    bool UsesReferenceEqualReleaseSpec,
    bool UsesNormalizedInput,
    bool UsesOriginalInputForRun);

internal sealed record CancellationDescriptor(
    bool Specialized,
    int CheckMask,
    bool ChecksBeforeDispatch,
    bool ChecksAtBoundary,
    string BoundaryPredicate,
    string PreDispatchPoll,
    string BoundaryPoll,
    string PollOrder);

internal sealed record FrameOutcomeDescriptor(
    FrameOutcome Outcome,
    FrameEffect[] Effects,
    bool RunsLeaf,
    bool TouchesAccount,
    bool ReturnsRefund,
    bool ClearsReturnData,
    bool PushesSuccess,
    bool RestoresSnapshot);

internal sealed record DirectFrameOutcomeDescriptor(
    DirectFrameOutcome Outcome,
    DirectResult Result,
    FrameEffect[] Effects,
    bool RunsLeaf,
    bool TouchesAccount,
    bool ReturnsRefund,
    bool ClearsReturnData,
    bool PushesSuccess,
    bool RestoresSnapshot);

internal sealed record PrecompileBinding(
    string Name,
    int Address,
    string TypeName,
    string BaseSourcePath,
    string[] StandardSourcePaths,
    ActivationRule Activation,
    string AddressField,
    string ProviderMember,
    string BaseGasMember,
    string DataGasMember,
    string RunMember,
    string NormalizeMember,
    string OracleModule,
    string OracleNamespace,
    string OracleEntrySymbol,
    string OracleSymbol,
    string[] InputRules,
    string[] OutputRules,
    string[] FailureRules);

internal sealed record ProviderEntry(
    string Name,
    int Address,
    string TypeName,
    ActivationRule Activation,
    string ProviderMember,
    bool IsStandardBuild);

internal sealed record RegistryDescriptor(
    string ProviderType,
    string ProviderProperty,
    string AddressType,
    string ReleaseSpecType,
    string ReleaseSpecMembershipMethod,
    string MainnetProviderType,
    string ForkScheduleProviderType,
    string AmsterdamForkType,
    int LowLookupMaximum,
    ProviderEntry[] ProviderEntries,
    AddressEntry[] AddressEntries,
    ForkActivationStep[] ActivationAncestry,
    string[] ActivationSteps,
    string[] StandardBuildSteps,
    string[] InactiveBehavior);

internal sealed record CacheDescriptor(
    string CodeInfoRepositoryType,
    string CachedRepositoryType,
    string ProviderSnapshotOperation,
    string CacheKeyShape,
    string NormalizationOperation,
    string InputToRunOperation,
    string InvalidInputOperation,
    string[] UncachedEffects,
    string[] CachedEffects,
    string[] Premises,
    CacheKeyDescriptor Key,
    CacheBranch[] Branches);

internal sealed record TrySaveBoundsDescriptor(
    string SourcePath,
    string Owner,
    string Member,
    string ValidationHelper,
    string FailureFlag,
    TrySaveFailureEffect[] FailureEffects,
    bool BoundsFailurePrecedesMutation);

internal sealed record RouteDescriptor(
    string Name,
    int Address,
    string ActivationRule,
    string[] FullFrameCallers,
    string[] DirectCallers,
    RouteMode DelegatedMode,
    string DirectGuard,
    string FullFrameEntry,
    string FullFramePreRunOrder,
    string FullFrameResultOrder,
    string DirectSuccessOrder,
    string DirectFailureOrder,
    string[] WorldEffects,
    string[] OpenEffects,
    CallKind[] CallKinds,
    CallTargetKind[] CallTargets,
    CancellationDescriptor Cancellation,
    bool FullFrameActionTraceConditional,
    FrameOutcomeDescriptor[] FullFrameOutcomes,
    DirectFrameOutcomeDescriptor[] DirectOutcomes);

internal sealed record PricingDescriptor(
    string GasPolicyType,
    string GasMethod,
    string LocalGasRule,
    string OverflowRule,
    string OutOfGasRule,
    string RunOrdering,
    PricingOutcome[] Outcomes,
    string[] Assumptions);

internal sealed record WorldRelationDescriptor(
    string RelationName,
    string CallbackType,
    string[] Inputs,
    string[] Outputs,
    string[] Preconditions,
    string[] Effects,
    bool CompositionGateOpen);

internal sealed record SourceIdentity(
    string Path,
    string Role,
    string Sha256,
    string RoslynSyntaxSha256,
    bool IsRaw,
    bool IncludedInStandardBuild);

internal sealed record MemberIdentity(
    string SourcePath,
    string Owner,
    string Member,
    string Signature,
    string SyntaxKind,
    string Sha256);

internal sealed record DependencyIdentity(
    string Kind,
    string Name,
    string Path,
    string Sha256,
    string Binding,
    bool OracleOnly);

internal sealed record ArtifactIdentity(string Path, string Sha256);

internal sealed record MutationCheck(
    string Name,
    string Subject,
    string RequiredRejectionOrDifference);

internal sealed record ScopeDescriptor(
    string Build,
    string Fork,
    string Claim,
    string[] IncludedSurfaces,
    string[] ExcludedSurfaces,
    string[] OpenCompositionGates,
    string[] Assumptions);

internal sealed record IrDocument(
    int SchemaVersion,
    string ExtractorVersion,
    string Kernel,
    string AcceptanceState,
    ScopeDescriptor Scope,
    RegistryDescriptor Registry,
    CacheDescriptor Cache,
    PricingDescriptor Pricing,
    PrecompileBinding[] PrecompileBindings,
    RouteDescriptor[] Routes,
    WorldRelationDescriptor WorldRelation,
    MutationCheck[] MutationChecks,
    SourceIdentity[] Sources,
    MemberIdentity[] Members,
    DependencyIdentity[] Dependencies,
    TrySaveBoundsDescriptor TrySaveBounds,
    string[] SemanticBindings);

internal sealed record SourceManifest(
    int SchemaVersion,
    string ExtractorVersion,
    string RoslynVersion,
    string LanguageVersion,
    string Kernel,
    SourceIdentity[] Sources,
    MemberIdentity[] Members,
    DependencyIdentity[] Dependencies,
    ArtifactIdentity Ir,
    ArtifactIdentity Lean,
    string CombinedSourceSha256,
    string CombinedMemberSha256,
    string[] SemanticBindings);

internal sealed record ExtractionResult(
    string IrPath,
    string ManifestPath,
    string LeanPath,
    int SourceCount,
    int MemberCount,
    int DependencyCount,
    int RouteCount,
    string IrSha256,
    string ManifestSha256,
    string LeanSha256,
    StageBExtractionResult StageB);
