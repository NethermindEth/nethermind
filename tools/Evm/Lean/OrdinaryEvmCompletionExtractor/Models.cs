// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.OrdinaryEvmCompletionExtractor;

internal sealed class AdmissionException(string message) : Exception(message);
internal sealed record SourceIdentity(string Path, string Role, string Sha256);
internal sealed record ReferenceIdentity(string Path, string AssemblyName, string Sha256, string Mvid, bool Selected);
internal sealed record ReferenceInventory(int SchemaVersion, int Count, ReferenceIdentity[] References);
internal sealed record ParameterIdentity(string Name, int Ordinal, string Type, string RefKind);
internal sealed record MemberIdentity(string Symbol, string MethodKind, string ReturnType, ParameterIdentity[] Parameters, string Path, int Start, int Length);
internal sealed record OperationTerm(string Kind, string Type, bool Implicit, string Symbol,
    string Operator, string Constant, string ArgumentKind, int ParameterOrdinal, string RefKind,
    string Conversion, OperationTerm[] Children);
internal sealed record ControlEdge(int Destination, string Semantics, int[] LeavingRegions, int[] FinallyRegions);
internal sealed record ControlBlock(int Ordinal, string Kind, string ConditionKind, OperationTerm[] Operations,
    OperationTerm? BranchValue, ControlEdge? FallThrough, ControlEdge? Conditional);
internal sealed record ControlRegionModel(string Kind, int First, int Last, string ExceptionType, string[] Locals, ControlRegionModel[] Children);
internal sealed record AdmittedMember(MemberIdentity Identity, OperationTerm Body, ControlBlock[] Blocks, ControlRegionModel Region);
internal sealed record CompilerAdmission(string AssemblyName, string LanguageVersion, string[] DefineConstants,
    string ReferenceInventorySha256, ReferenceIdentity[] References, SourceIdentity[] Sources, AdmittedMember[] Members);
internal sealed record ExpressionSite(string Role, string Owner, string Syntax, int Start, int Length, OperationTerm Operation);
internal enum ContinuationStage
{
    VmReturn, Metrics, CopyFrameGas, AccessReport, Rollback, DisposeFrame, Refund,
    ProcessorCounters, PayFees, TransactionGas, Commit, Receipt, DisposeEnvironment, DisposeAccessTracker, Return,
}
internal sealed record ContinuationPlan(ExpressionSite[] Expressions, ContinuationStage[] Stages);
internal sealed record SourceModel(CompilerAdmission Compiler, ContinuationPlan Plan);
internal sealed record ArtifactDocument(int SchemaVersion, string ExtractorVersion, string AcceptanceState,
    string SourceClosure, SourceIdentity[] Dependencies, SourceModel Source);
