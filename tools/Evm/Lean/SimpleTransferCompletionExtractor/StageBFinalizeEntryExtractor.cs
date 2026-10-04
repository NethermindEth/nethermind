// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nethermind.Evm.Lean.SimpleTransferCompletionExtractor;

internal static class StageBFinalizeEntryExtractor
{
    internal const string IrName = "FinalizeEntry.ir.json";
    internal const string LeanName = "FinalizeEntry.lean";
    internal const string ManifestName = "FinalizeEntry.source-manifest.json";
    private const string Package = StageBPayFeesExtractor.Package;

    internal static Dictionary<string, byte[]> RenderArtifacts(string root, IReadOnlyDictionary<string, string>? overrides = null)
    {
        CompilerClosure closure = CompilerSources.Load(root, overrides);
        StageBPlan plan = StageBLowering.Build(root, overrides);
        INamedTypeSymbol processor = closure.Compilation.GetTypeByMetadataName("Nethermind.Evm.TransactionProcessing.TransactionProcessorBase`1")
            ?? throw new ExtractionException("Missing FinalizeTransaction owner.");
        IMethodSymbol method = processor.GetMembers("FinalizeTransaction").OfType<IMethodSymbol>().Single();
        if (method.DeclaredAccessibility != Accessibility.Private || method.IsStatic || method.IsVirtual ||
            method.IsOverride || method.IsAbstract || method.IsGenericMethod ||
            method.DeclaringSyntaxReferences.Single().GetSyntax() is not MethodDeclarationSyntax { Body: not null, ExpressionBody: null })
            throw new ExtractionException("The private nonvirtual FinalizeTransaction ownership/signature changed.");
        StageBPrefixProgram prefix = StageBPrefixCompiler.Compile(plan);
        StageBArtifact.ValidateProgram(prefix);
        StageBMethod lowered = plan.Methods.Single(static candidate => candidate.Signature?.Name == "FinalizeTransaction");
        StageBMember descriptor = plan.Members.Single(member => member.Symbol == lowered.Symbol);
        ValidateEntry(lowered, descriptor);
        StageBTerm call = prefix.Refund.Continuation.SourceBlocks.Single(static block => block.Ordinal == 39).BranchValue
            ?? throw new ExtractionException("The accepted finalization invocation disappeared.");
        if (call.Symbol != descriptor.Symbol || call.Operator != "virtual=False" || call.Children.Length != 13 ||
            call.Binding?.Call is not { ReceiverChild: 0 } binding || binding.Target != descriptor.Symbol ||
            !binding.ArgumentChildren.SequenceEqual(Enumerable.Range(1, 12)))
            throw new ExtractionException("The accepted finalization call descriptor changed.");
        StageBBlock entry = lowered.ControlFlowEvidence[0];
        byte[] ir = Json(new
        {
            schemaVersion = 1, artifactKind = "stage-b-finalize-bind-empty-entry",
            owner = "private-instance-nonvirtual", signature = lowered.Signature, callerDescriptor = descriptor,
            entry = new StageBCfgPoint(0, 0), sourceEntry = entry,
            source = new { path = lowered.Path, sha256 = lowered.SourceSha256 },
            prefixIntegrity = prefix.Integrity,
            exclusions = new[] { "warmup-guard", "callee-body", "property-effects", "world-state", "receipts", "clr" },
        });
        byte[] lean = Encoding.UTF8.GetBytes(StageBLeanDataEmitter.FinalizeEntry(descriptor, entry));
        byte[] manifest = Json(new
        {
            schemaVersion = 1, artifactKind = "stage-b-finalize-bind-empty-entry",
            sources = closure.EffectiveSources, references = closure.References,
            compilerPins = new StageBArtifactFile(CompilerSources.PinsPath, CompilerSources.PinsSha256),
            compilerInventory = new StageBArtifactFile(CompilerReferences.InventoryPath, CompilerReferences.InventorySha256),
            extractionSources = new[] { "StageBFinalizeEntryExtractor.cs", "StageBArtifact.cs", "StageBLowering.cs", "StageBPrefixCompiler.cs" }
                .Select(path => new StageBArtifactFile(Package + "/" + path,
                    CompilerReferences.Hash(File.ReadAllBytes(CompilerReferences.Within(root, Package + "/" + path))))).ToArray(),
            ir = new StageBArtifactFile(IrName, CompilerReferences.Hash(ir)),
            lean = new StageBArtifactFile(LeanName, CompilerReferences.Hash(lean)),
        });
        return new(StringComparer.Ordinal) { [IrName] = ir, [LeanName] = lean, [ManifestName] = manifest };
    }

    internal static void ValidateEntry(StageBMethod method, StageBMember descriptor)
    {
        string[] names = ["tx", "spec", "tracer", "opts", "restore", "commit", "deleteCallerAccount", "senderReservedGasPayment", "executingAccount", "substate", "spentGas", "statusCode"];
        if (method.Signature is not { Parameters.Length: 12 } signature ||
            !Json(signature).AsSpan().SequenceEqual(Json(descriptor)) ||
            !signature.Parameters.Select(static parameter => parameter.Name).SequenceEqual(names) ||
            signature.Parameters.Where((parameter, index) => parameter.Ordinal != index || parameter.Optional ||
                parameter.RefKind != (index is 7 or 9 ? StageBRefKind.In : StageBRefKind.None)).Any() ||
            method.ExpressionEnvelope || method.SelectedEntry is not null ||
            method.ControlFlowEvidence.FirstOrDefault() is not
            { Ordinal: 0, Kind: "Entry", Reachable: true, ConditionKind: "None", Operations.Length: 0,
                BranchValue: null, ContainsExcludedOperations: false, Conditional: null,
                FallThrough: { Destination: 1, Semantics: "Regular", LeavingRegions.Length: 0, EnteringRegions.Length: 0, FinallyRegions.Length: 0 } })
            throw new ExtractionException("The admitted FinalizeTransaction signature or empty entry/edge changed.");
    }

    internal static void Extract(string root, string output, bool check)
    {
        Dictionary<string, byte[]> fresh = RenderArtifacts(root);
        if (check)
        {
            StageBEffectiveBlockGasExtractor.ValidateArtifacts(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(output, path).Replace('\\', '/'), File.ReadAllBytes, StringComparer.Ordinal), fresh);
            return;
        }
        Directory.CreateDirectory(output);
        foreach ((string name, byte[] bytes) in fresh) File.WriteAllBytes(Path.Combine(output, name), bytes);
    }

    private static byte[] Json(object value) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, CompilerReferences.JsonOptions) + "\n");
}
