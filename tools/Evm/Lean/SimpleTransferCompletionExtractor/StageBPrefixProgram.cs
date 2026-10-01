// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.SimpleTransferCompletionExtractor;

internal enum StageBPrefixTargetKind { Local, StaticField, DefaultValue, Initialization, StateCharge, ExternalRequest, RefundSuspension }
internal enum StageBPrefixExit { Branch, Return, Suspend, OutsideSelectedDomain }
internal enum StageBPrefixOperandMode { Value, Location, ReadOnlyLocation }
internal sealed record StageBPrefixTarget(StageBPrefixTargetKind Kind, StageBMember Member, string Body, StageBRequestKind? RequestKind);
internal sealed record StageBPrefixArgument(int Child, int Ordinal, StageBArgumentMode Mode, bool Implicit, string Kind);
internal sealed record StageBPrefixCall(StageBPrefixTarget Target, int ReceiverChild, StageBPrefixArgument[] Arguments);

/// <summary>Ordered operands; a suspending child prevents its parent operation from completing.</summary>
internal sealed record StageBPrefixNode(StageBOperationKind Kind, string Type, string Symbol, string Operator, string Constant,
    string Conversion, bool Implicit, StageBPrefixOperandMode Mode, StageBTermBinding Binding, StageBPrefixCall? Call,
    StageBPrefixTarget[] AdditionalTargets, StageBPrefixNode[] Children, bool Completes);

internal sealed record StageBPrefixBlock(int Ordinal, StageBPrefixNode[] Operations, StageBPrefixNode? BranchValue,
    string ConditionKind, StageBPrefixExit Exit, StageBEdge? FallThrough, StageBEdge? Conditional);
internal sealed record StageBPrefixFunction(StageBMember Signature, StageBCfgPoint Entry, StageBBinding[] Bindings,
    StageBCapture[] Captures, StageBRegion[] Regions, string[] EntryFacts, StageBPrefixBlock[] Blocks, string[] Calls,
    StageBPrefixCaptureMode[] CaptureModes, StageBPrefixBlockBound[] BlockBounds, long FuelBound, bool MayReturn);
internal sealed record StageBPrefixCaptureMode(int Capture, StageBPrefixOperandMode Mode);
internal sealed record StageBPrefixBlockBound(int Block, long Fuel);

/// <summary>Source position at which the invocation result is still unavailable.</summary>
internal sealed record StageBPrefixContinuation(string Function, int Block, int Operation, int[] CallPath,
    StageBTerm PendingOperation, StageBBlock[] SourceBlocks);
internal sealed record StageBPrefixSuspension(StageBPrefixCall Call, StageBPrefixNode[] OrderedOperands, StageBPrefixContinuation Continuation);
internal sealed record StageBInertFunction(StageBMember Signature, StageBCfgPoint Entry, StageBBlock[] SourceBlocks);

/// <summary>A bounded compilation, not an interpreter, Lean artifact, or refinement result.</summary>
/// <remarks>Fuel counts CFG dispatches and term visits with local calls expanded. Initializers are separate pre-entry value provenance.</remarks>
internal sealed record StageBPrefixProgram(string Entry, string PolicyType, StageBPrefixFunction[] Functions,
    string[] Initializers, StageBMember[] Members, StageBType[] Types, StageBPrefixSuspension Refund,
    string[] CalleeBeforeCaller, long FuelBound, string Scope, SourceIdentity[] CompilerSources,
    ReferenceIdentity[] CompilerReferences, string[] ExternalPremises, StageBStage[] AcceptedStages,
    StageBInertFunction PostRefundFeeHelper, string Integrity);
