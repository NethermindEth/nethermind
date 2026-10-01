// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.SimpleTransferCompletionExtractor;

internal enum StageBCalleeOwner
{
    Local,
    AcceptedStage,
    ExternalRequest,
}

internal enum StageBRequestKind
{
    Constant, Projection, WorldState, Tracer, CodeLookup, Pool, Collection, Arithmetic, Representation, Framework,
}

internal sealed record StageBCallee(string Symbol, StageBCalleeOwner Owner, string Contract, StageBRequestKind? RequestKind = null);
internal sealed record StageBStageDependency(string Symbol, string Path, string SourceSha256);
internal sealed record StageBStage(string Name, string Theorem, string[] EntryPoints, StageBStageDependency[] SourceClosure);

internal enum StageBOperationKind
{
    OutsideSelectedDomain,
    Block, VariableDeclarationGroup, VariableDeclaration, VariableDeclarator, VariableInitializer, DeclarationExpression,
    ExpressionStatement, Return, Conditional, ConditionalAccess, ConditionalAccessInstance, Coalesce,
    SimpleAssignment, CompoundAssignment, Invocation, ObjectCreation, ObjectOrCollectionInitializer, MemberInitializer,
    CollectionExpression, ArrayCreation, ArrayInitializer, FieldReference, PropertyReference, LocalReference,
    ParameterReference, InstanceReference, Literal, DefaultValue, Binary, Unary, Conversion, Parenthesized, Argument,
    IsPattern, ConstantPattern, NegatedPattern, DeclarationPattern, DiscardPattern, SwitchExpression, SwitchExpressionArm,
    Discard, NameOf, IsNull, UsingDeclaration, FlowCapture, FlowCaptureReference, Loop, Branch, Tuple,
    DeconstructionAssignment, Throw,
}

/// <summary>Typed operations for the selected continuation, independently lowered from Roslyn.</summary>
/// <remarks>This checkpoint has no Lean interpreter or source-refinement theorem.</remarks>
internal sealed record StageBPlan(
    string Scope,
    SourceIdentity[] CompilerSources,
    ReferenceIdentity[] CompilerReferences,
    StageBMethod[] Methods,
    StageBCallee[] Callees,
    StageBStage[] AcceptedStages,
    string[] ExternalPremises)
{
    public StageBMember[] Members { get; init; } = [];
    public StageBType[] Types { get; init; } = [];
    public StageBPrefixAnchors? PrefixAnchors { get; init; }
}

internal enum StageBMemberKind { Method, Constructor, Property, Field }
internal enum StageBReceiverKind { Static, Reference, Value }
internal enum StageBRefKind { None, Ref, Out, In, RefReadOnly, RefReadOnlyParameter }
internal enum StageBArgumentMode { Value, ReadOnlyLocation, ReadOnlyTemporary, WritableLocation, OutLocation }
internal enum StageBTypeKind { Void, Value, Reference, Array, Parameter }
internal sealed record StageBParameter(string Symbol, string Name, int Ordinal, string Type, StageBRefKind RefKind, bool Optional);
internal sealed record StageBMember(string Symbol, string Definition, string Name, string DeclaringType,
    StageBMemberKind Kind, StageBReceiverKind Receiver, string Type, StageBRefKind RefKind, StageBParameter[] Parameters);
internal sealed record StageBFieldLayout(string Symbol, string Type, bool ReadOnly, string AssociatedProperty);
internal sealed record StageBType(string Symbol, StageBTypeKind Kind, string ElementType, string[] TypeArguments, StageBFieldLayout[] Fields);
internal sealed record StageBBinding(string Symbol, string Type, StageBRefKind RefKind, int Region);
internal sealed record StageBCapture(int Id, int Region, string Type);
internal sealed record StageBCfgPoint(int Block, int Operation);
internal sealed record StageBPrefixAnchors(string Entry, string SimpleTransfer, string Refund, string PolicyType, string[] AfterRefundMembers);
internal sealed record StageBCallSite(string Target, int ReceiverChild, int[] ArgumentChildren);
internal sealed record StageBTermBinding(int Capture, StageBRefKind RefKind, bool IsRef, StageBArgumentMode ArgumentMode,
    StageBCallSite? Call, string[] AdditionalMembers);

internal sealed record StageBMethod(
    string Symbol,
    string Path,
    string SourceSha256,
    string[] EntryFacts,
    StageBTerm[] Statements,
    StageBRegion[] Regions,
    StageBBlock[] ControlFlowEvidence)
{
    public StageBMember? Signature { get; init; }
    public StageBBinding[] Bindings { get; init; } = [];
    public StageBCapture[] Captures { get; init; } = [];
    public StageBCfgPoint? SelectedEntry { get; init; }
    public bool ExpressionEnvelope { get; init; }
}

internal sealed record StageBTerm(
    StageBOperationKind Kind,
    string Type,
    string Symbol,
    string Operator,
    string Constant,
    bool Implicit,
    string ArgumentKind,
    int ParameterOrdinal,
    string RefKind,
    string Conversion,
    StageBTerm[] Children)
{
    public StageBTermBinding? Binding { get; init; }
}

/// <summary>Original CFG topology with operations outside the selected source slice marked explicitly.</summary>
internal sealed record StageBBlock(
    int Ordinal,
    string Kind,
    bool Reachable,
    string ConditionKind,
    StageBTerm[] Operations,
    StageBTerm? BranchValue,
    bool ContainsExcludedOperations,
    StageBEdge? FallThrough,
    StageBEdge? Conditional);

internal sealed record StageBEdge(int Destination, string Semantics, int[] LeavingRegions, int[] EnteringRegions, int[] FinallyRegions);

internal sealed record StageBRegion(int Id, int Parent, string Kind, int FirstBlock, int LastBlock, string[] Locals, int[] Captures, string ExceptionType);
