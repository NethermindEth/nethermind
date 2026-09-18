// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.PrecompileFullFrameExtractor;

internal sealed class ExtractionException(string message) : Exception(message);

internal enum BranchKind
{
    PricingOverflow,
    PricingOutOfGas,
    ReturnedFailure,
    ManagedTopFailure,
    ManagedNestedFailure,
    TopSuccess,
    NestedSuccess,
    OuterEvmException,
    OuterOverflow,
    MissingNativeDependency,
}

internal enum Effect
{
    ActionStart,
    TransferLog,
    TouchExecutingAccount,
    RipemdLatch,
    Pricing,
    InstallPricedGas,
    RunOracle,
    RestoreSnapshot,
    RestoreRipemdTouch,
    OperationRemainingGasZero,
    OperationError,
    ActionError,
    ClearReturnData,
    RemoveAdvancedRefund,
    RestoreChildStateGasOnHalt,
    CreditNewAccountStateGas,
    ParentResume,
    TopFailureSubstate,
    ClearExecutionGas,
    ClassifyShouldRevert,
    ReturnChildExecutionGas,
    RestoreChildStateGas,
    HandleRevert,
    IncorporateAdvancedRefund,
    RefundChildGas,
    HandleRegularReturn,
    CommitToParent,
    RepayStateGasSpill,
    TraceTransactionActionEnd,
    PrepareTopLevelSubstate,
    ProcessExitExcluded,
}

internal sealed record Pin(string Path, string Sha256, string Role, string[] Symbols);
internal sealed record PinDocument(int SchemaVersion, Pin[] Files);
internal sealed record MemberIdentity(string Path, string Owner, string Member, string Kind, string Sha256, string[] StatementSha256);
internal sealed record OracleIdentity(string Name, int Address, string Path, string Sha256, string Namespace, string Symbol);
internal sealed record Branch(BranchKind Kind, string RawControlRoute, Effect[] Prefix, Effect[] TopSettlement, Effect[] NestedSettlement,
    bool InstallsGas, bool RunsOracle, bool OrdinaryOutcome);
internal sealed record ArtifactIdentity(string Path, string Sha256);
internal sealed record IrDocument(int SchemaVersion, string Kernel, string AcceptanceState, string[] Route, string ActionAddress, string BalanceAddress,
    Pin[] Sources, MemberIdentity[] Members, Pin[] Dependencies, OracleIdentity[] Oracles, Branch[] Branches, string[] Assumptions, string[] Exclusions);
internal sealed record SourceManifest(int SchemaVersion, string Kernel, string RoslynVersion, string AdmissionSha256,
    Pin[] Sources, MemberIdentity[] Members, Pin[] Dependencies, OracleIdentity[] Oracles, ArtifactIdentity Ir, ArtifactIdentity Lean);
internal sealed record Artifacts(IrDocument Ir, SourceManifest Manifest, byte[] IrBytes, byte[] ManifestBytes, byte[] LeanBytes);
