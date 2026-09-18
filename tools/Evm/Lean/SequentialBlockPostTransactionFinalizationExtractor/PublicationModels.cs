// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nethermind.Evm.Lean.SequentialBlockPostTransactionFinalizationExtractor;

internal sealed record PublicationPinDocument(int SchemaVersion, string Status, SourcePin[] Sources);

internal sealed record PublicationSourceIdentity(string Path, string Role, string Sha256, string SyntaxSha256);

internal sealed record PublicationSourceFile(
    string RelativePath,
    string Role,
    string Sha256,
    string SyntaxSha256,
    CompilationUnitSyntax Root,
    SyntaxTree Tree);

/// <summary>A source-bound ProcessOne suffix anchor with typed identity and structural flow evidence.</summary>
/// <remarks>These fields are admission evidence, not an execution trace or a CLR proof.</remarks>
internal sealed record PublicationAnchor(
    string Id,
    string Path,
    string OwnerFqn,
    string SymbolFqn,
    string NodeKind,
    string CanonicalSyntax,
    int StartLine,
    int EndLine,
    string CfgRegion,
    string DataFlow,
    bool IsReachable,
    string SourceSyntaxSha256,
    int Position,
    int ControlFlowBlock,
    string OperationKind);

internal sealed record PublicationControlFlowIdentity(
    string Id,
    string OwnerFqn,
    int StartLine,
    int EndLine,
    string[] ReachableRegions,
    string[] Edges,
    string[] NormalExits,
    string[] ExceptionalExits,
    string ShapeSha256,
    ControlFlowBlockMembership[] BlockMemberships);

internal sealed record PublicationDataFlowIdentity(
    string Id,
    string SymbolFqn,
    string Definition,
    string[] Reads,
    string[] Writes,
    string ReachingDefinitions,
    bool DataFlowSucceeded);

internal sealed record PublicationAdapterPremise(string Id, string Statement);

internal sealed record PublicationSynchronousCallable(
    string Id, string Symbol, string MethodKind, string BodyKind, bool ReturnsVoid,
    bool IsAsync, bool IsIterator, bool IsPartial, bool IsExtern, bool IsAbstract,
    bool HasConditionalAttribute, string[] Attributes, bool IsStatic, bool IsVirtual,
    bool IsOverride, string Accessibility, string DeclaringType, string DeclaringAssembly,
    string[] BaseTypes, string[] Interfaces, string[] ImplementedInterfaceMethods,
    string[] OverriddenMethods, string[] InheritedAttributes, bool HasInheritedConditionalAttribute);

internal sealed record PublicationDependencyIdentity(string Path, string Sha256);

internal sealed record ProcessOnePublicationIrDocument(
    int SchemaVersion,
    string ExtractorVersion,
    string Kernel,
    string AcceptanceState,
    string SourceClosureSha256,
    PublicationSourceIdentity[] Sources,
    CompilerClosureIdentity CompilerClosure,
    PublicationSynchronousCallable[] SynchronousCallables,
    PublicationAnchor[] Anchors,
    PublicationControlFlowIdentity[] ControlFlows,
    PublicationDataFlowIdentity[] DataFlows,
    PublicationAdapterPremise[] AdapterPremises,
    string[] OrderedEvents,
    string[] ForbiddenEffects,
    string[] Dependencies,
    string[] MutationChecks,
    string[] OpenObligations);

internal sealed record PublicationArtifactIdentity(string Path, string Sha256);

internal sealed record ProcessOnePublicationManifest(
    int SchemaVersion,
    string ExtractorVersion,
    string Kernel,
    string AcceptanceState,
    string SourceClosureSha256,
    PublicationSourceIdentity[] Sources,
    CompilerClosureIdentity CompilerClosure,
    PublicationSynchronousCallable[] SynchronousCallables,
    PublicationControlFlowIdentity[] ControlFlows,
    PublicationDataFlowIdentity[] DataFlows,
    PublicationAdapterPremise[] AdapterPremises,
    PublicationArtifactIdentity Ir,
    PublicationArtifactIdentity Lean,
    PublicationDependencyIdentity[] Dependencies,
    string[] MutationChecks,
    string[] OpenObligations);

internal sealed record ProcessOnePublicationExtractionResult(
    string IrPath,
    string ManifestPath,
    string LeanPath,
    int AnchorCount,
    int ControlFlowCount);
