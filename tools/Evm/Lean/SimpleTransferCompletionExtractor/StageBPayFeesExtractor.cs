// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nethermind.Evm.Lean.SimpleTransferCompletionExtractor;

internal static class StageBPayFeesExtractor
{
    internal const string Package = "tools/Evm/Lean/SimpleTransferCompletionExtractor";
    internal const string IrName = "PayFees.ir.json";
    internal const string LeanName = "PayFees.lean";
    internal const string ManifestName = "PayFees.source-manifest.json";
    private const string Processor = "Nethermind.Evm.TransactionProcessing.TransactionProcessorBase`1";
    private const string ExpectedBody = """
        {
            UInt256 fees = premiumPerGas * spentGas;
            bool gasBeneficiaryNotDestroyed = !substate.DestroyListContains(header.GasBeneficiary);
            if (statusCode == StatusCode.Failure || gasBeneficiaryNotDestroyed || spec.IsEip8037Enabled)
            {
                WorldState.AddToBalanceAndCreateIfNotExists(header.GasBeneficiary!, fees, spec);
            }
            UInt256 effectiveBaseFee = UInt256.Min(header.BaseFeePerGas, effectiveGasPrice);
            UInt256 eip1559Fees = !tx.IsFree() ? effectiveBaseFee * spentGas : UInt256.Zero;
            UInt256 collectedFees = spec.IsEip1559Enabled ? eip1559Fees : UInt256.Zero;
            if (tx.SupportsBlobs && spec.IsEip4844FeeCollectorEnabled)
            {
                collectedFees += blobBaseFee;
            }
            if (spec.FeeCollector is not null && !collectedFees.IsZero)
            {
                WorldState.AddToBalanceAndCreateIfNotExists(spec.FeeCollector, collectedFees, spec);
            }
            if (tracer.IsTracingFees)
            {
                tracer.ReportFees(fees, eip1559Fees + blobBaseFee);
            }
        }
        """;

    internal static Dictionary<string, byte[]> RenderArtifacts(string root, IReadOnlyDictionary<string, string>? overrides = null)
    {
        CompilerClosure closure = CompilerSources.Load(root, overrides);
        StageBPlan plan = StageBLowering.Build(root, overrides);
        INamedTypeSymbol basis = Type(closure, Processor);
        IMethodSymbol slot = basis.GetMembers("PayFees").OfType<IMethodSymbol>().Single();
        if (!slot.IsVirtual || slot.IsOverride || slot.IsStatic || !slot.ReturnsVoid ||
            slot.DeclaredAccessibility != Accessibility.Protected || slot.Parameters.Length != 10 ||
            !slot.Parameters.Select(static parameter => parameter.RefKind).SequenceEqual(
                new[] { RefKind.None, RefKind.None, RefKind.None, RefKind.None, RefKind.In, RefKind.None, RefKind.In, RefKind.In, RefKind.In, RefKind.None }) ||
            slot.DeclaringSyntaxReferences.Single().GetSyntax() is not MethodDeclarationSyntax { Body: { } body } ||
            Canonical(body) != Canonical(SyntaxFactory.ParseStatement(ExpectedBody)))
            throw new ExtractionException("The admitted PayFees slot/body changed.");

        INamedTypeSymbol policy = Type(closure, "Nethermind.Evm.GasPolicy.EthereumGasPolicy");
        INamedTypeSymbol ethereum = Type(closure, "Nethermind.Evm.TransactionProcessing.EthereumTransactionProcessor");
        INamedTypeSymbol bal = Type(closure, "Nethermind.Evm.TransactionProcessing.TransactionProcessor`1").Construct(policy);
        foreach (INamedTypeSymbol leaf in new[] { ethereum, bal })
        {
            if (!leaf.IsSealed || !SymbolEqualityComparer.Default.Equals(Resolve(leaf).OriginalDefinition, slot))
                throw new ExtractionException("A standard-mainnet PayFees receiver shadows or overrides the admitted virtual slot.");
            INamedTypeSymbol? cursor = leaf;
            while (cursor is not null && !SymbolEqualityComparer.Default.Equals(cursor.OriginalDefinition, basis))
                cursor = cursor.BaseType;
            if (cursor is null || cursor.TypeArguments is not [ITypeSymbol argument] ||
                !SymbolEqualityComparer.Default.Equals(argument, policy))
                throw new ExtractionException("The standard-mainnet PayFees policy lineage changed.");
        }
        INamedTypeSymbol system = Type(closure, "Nethermind.Evm.TransactionProcessing.SystemTransactionProcessor`1").Construct(policy);
        IMethodSymbol systemSlot = Resolve(system);
        if (!systemSlot.IsOverride || systemSlot.OverriddenMethod is null ||
            !SymbolEqualityComparer.Default.Equals(systemSlot.OverriddenMethod.OriginalDefinition, slot) ||
            systemSlot.DeclaringSyntaxReferences.Single().GetSyntax() is not MethodDeclarationSyntax { Body.Statements.Count: 0 })
            throw new ExtractionException("The separately excluded system PayFees override changed.");

        StageBMethod lowered = plan.Methods.Single(static method => method.Signature?.Name == "PayFees");
        byte[] ir = Json(new
        {
            schemaVersion = 1, artifactKind = "stage-b-pay-fees-projected-source-admission",
            receivers = new[] { Display(ethereum), Display(bal) },
            declaringType = Display(basis.Construct(policy)),
            excludedSystem = Display(system), excludedSystemBodyEmpty = true,
            source = lowered,
            projection = new[] { "premium", "destroyProbe", "beneficiary", "effectiveBase", "baseFees", "initialCollector", "blobCollector", "collector", "report", "return" },
            exclusions = new[] { "autofac-resolution", "system-override-execution", "optimism", "taiko", "xdc", "plugin-receivers", "provider-correctness", "uint256-limb-clr-bridge", "finalization" },
        });
        byte[] lean = Encoding.UTF8.GetBytes("""
            -- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
            -- SPDX-License-Identifier: LGPL-3.0-only
            -- Generated from the exact admitted PayFees slot, body, and standard receiver lineages.

            namespace SimpleTransferCompletionExtractor.StageB.PayFees.Generated

            inductive Receiver where | ethereum | balEthereum deriving DecidableEq, Repr
            inductive Owner where | standardBase | systemOverride deriving DecidableEq, Repr
            def resolve (_ : Receiver) : Owner := .standardBase
            def systemOwner : Owner := .systemOverride
            def systemBodyEmpty : Bool := true

            inductive Op where
              | premium | destroyProbe | beneficiary | effectiveBase | baseFees
              | initialCollector | blobCollector | collector | report | returnVoid
              deriving DecidableEq, Repr

            def program : List Op :=
              [.premium, .destroyProbe, .beneficiary, .effectiveBase, .baseFees,
               .initialCollector, .blobCollector, .collector, .report, .returnVoid]

            def wordModulus : Nat := 2 ^ 256
            def priorityFee (premium paid : Nat) : Nat := premium * paid % wordModulus
            def effectiveBaseFee (base effective : Nat) : Nat := min base effective
            def baseFees (free : Bool) (effectiveBase paid : Nat) : Nat :=
              if !free then effectiveBase * paid % wordModulus else 0
            def collectorFees (eip1559 supportsBlobs blobCollector : Bool) (base blob : Nat) : Nat :=
              if supportsBlobs && blobCollector then ((if eip1559 then base else 0) + blob) % wordModulus
              else if eip1559 then base else 0
            def reportedBurnt (base blob : Nat) : Nat := (base + blob) % wordModulus

            end SimpleTransferCompletionExtractor.StageB.PayFees.Generated
            """ + "\n");
        byte[] manifest = Json(new
        {
            schemaVersion = 1, artifactKind = "stage-b-pay-fees-projected-source-admission",
            sources = closure.EffectiveSources,
            references = closure.References,
            compilerPins = new StageBArtifactFile(CompilerSources.PinsPath, CompilerSources.PinsSha256),
            compilerInventory = new StageBArtifactFile(CompilerReferences.InventoryPath, CompilerReferences.InventorySha256),
            extractor = new StageBArtifactFile(Package + "/StageBPayFeesExtractor.cs",
                CompilerReferences.Hash(File.ReadAllBytes(CompilerReferences.Within(root, Package + "/StageBPayFeesExtractor.cs")))),
            ir = new StageBArtifactFile(IrName, CompilerReferences.Hash(ir)),
            lean = new StageBArtifactFile(LeanName, CompilerReferences.Hash(lean)),
        });
        return new(StringComparer.Ordinal) { [IrName] = ir, [LeanName] = lean, [ManifestName] = manifest };
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

    private static IMethodSymbol Resolve(INamedTypeSymbol leaf)
    {
        for (INamedTypeSymbol? type = leaf; type is not null; type = type.BaseType)
        {
            IMethodSymbol[] methods = type.GetMembers("PayFees").OfType<IMethodSymbol>().ToArray();
            if (methods is [IMethodSymbol method]) return method;
            if (methods.Length != 0) throw new ExtractionException("The PayFees overload roster changed.");
        }
        throw new ExtractionException("The PayFees slot disappeared.");
    }

    private static string Canonical(SyntaxNode node) => string.Join(" ", node.DescendantTokens().Select(static token => token.Text));
    private static INamedTypeSymbol Type(CompilerClosure closure, string name) => closure.Compilation.GetTypeByMetadataName(name)
        ?? throw new ExtractionException("Missing or ambiguous PayFees receiver type: " + name);
    private static string Display(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    private static byte[] Json(object value) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, CompilerReferences.JsonOptions) + "\n");
}
