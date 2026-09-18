// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Evm.Lean.SequentialBlockPostTransactionFinalizationExtractor;

internal static partial class Extractor
{
    private sealed record HelperPreservationRule(string Member, string Path, string SymbolId, string SyntaxSha256);

    private const string EntryBodySha256 = "80d2f26d89842515ead4ad275d0ecd424df2395b80ba3b30569a280cf9080bb2";

    private static HelperPreservationIdentity BuildEntryPreservation(MethodDeclarationSyntax method)
    {
        string body = Canonical(method.Body ?? throw new ExtractionException("ProcessBlock executable body is missing."));
        HelperPreservationIdentity identity = new(BlockProcessorPath, "ProcessBlock", ProcessBlockMethodSymbol, body,
            Sha256(Encoding.UTF8.GetBytes(body)));
        ValidateEntryPreservation(identity);
        return identity;
    }

    private static void ValidateEntryPreservation(HelperPreservationIdentity identity)
    {
        if (identity is null || identity.Path != BlockProcessorPath || identity.Member != "ProcessBlock" ||
            identity.SymbolId != ProcessBlockMethodSymbol || identity.CanonicalSyntax is null ||
            identity.SyntaxSha256 != EntryBodySha256 ||
            Sha256(Encoding.UTF8.GetBytes(identity.CanonicalSyntax)) != EntryBodySha256)
            throw new ExtractionException("ProcessBlock complete executable body/callback roster changed.");
    }

    private static readonly HelperPreservationRule[] HelperPreservationRules =
    [
        new("AccumulateBlockBloom", BlockProcessorPath, AccumulateBlockBloomSymbol,
            "add2837433c5d3d18d5ddc670aa36d5c73d024a1b73497c5409d9c3ea733ab5e"),
        new("ApplyMinerReward", BlockProcessorPath,
            BlockProcessorTypeSymbol + ".ApplyMinerReward(Nethermind.Consensus.Rewards.BlockReward,Nethermind.Core.Specs.IReleaseSpec)",
            "9243e1988ea538748bb87c7e6f2f2c488db6b3ae81a72703815a4dd432037330"),
        new("ApplyMinerRewards", BlockProcessorPath, ApplyMinerRewardsMethodSymbol,
            "80bd12efcd7c533307f6cd2da28554bd331ab164b7578e341297325d8a81a654"),
        new("CalculateBlooms", BlockProcessorPath, CalculateBloomsMethodSymbol,
            "52628ec2ee27c2ebb75e5cd5f7c527aa50ee68080cd59631f2965c007e8a1318"),
        new("CalculateReceiptsRoot", BlockProcessorPath, ReceiptsRootValueSymbol,
            "c7a64338bbe5f3eabc9fe9c6699db88848e6be56af62255e4fb33d81afa6f345"),
        new("CommitState", BlockProcessorPath, CommitStateMethodSymbol,
            "7cd46a953aa3d5524db0883358b8e629b0741c548ee1e1e7f5e609a68c523d82"),
        new("CommitStateAndStorageRoots", BlockProcessorPath, CommitRootsMethodSymbol,
            "f366476bb0490f6c788bce0ba7f1ea1bed830427716029b16cddd3f4eebc90f6"),
        new("ComputeStateRoot", BlockProcessorPath, ComputeStateRootMethodSymbol,
            "f2b6c4f3eed0f57f712d044a55154259ed680ae03a3bfd6605cd4136cb42a219"),
        new("CountLogs", BlockProcessorPath, CountLogsMethodSymbol,
            "9e6ae117ecbd4d97a3f8aa86bfd19fadf367d390f2f59087ddc639d0fe722f06"),
        new("CreateBlockExecutionContext", BlockProcessorPath, CreateBlockExecutionContextMethodSymbol,
            "c6061361cfaaa61961b114b89dc835d8b91697fd45c55d9633c8d02b113bb456"),
        new("SetAccountChanges", BlockProcessorPath, SetAccountChangesMethodSymbol,
            "8ec2b585964311e79203d574baf4ce92d5e9a436b6b8955eb8741652f5af5843"),
        new("ShouldCalculateReceiptsInBackground", BlockProcessorStandardPath, BackgroundPredicateSymbol,
            "35f9710b6403354d97203b8a383dc01fe5a9e90fd094628fb2e985cad2dccede"),
        new("ShouldComputeStateRoot", BlockProcessorPath,
            BlockProcessorTypeSymbol + ".ShouldComputeStateRoot(Nethermind.Core.BlockHeader)",
            "77e2a33c3428bde4e2f8e056ddd3c3f892d0092ed008d5f56a4957cd0ec13bca"),
        new("TraceMinerReward", BlockProcessorPath, BlockProcessorTypeSymbol + ".TraceMinerReward(Nethermind.Consensus.Rewards.BlockReward)",
            "54e75525b573c26bb4d97e794e44eedc04cb641ae64dfa6f001229f51702b77c"),
    ];

    private static HelperPreservationIdentity[] BuildHelperPreservation(SourceLocalMethod[] closure) =>
        closure.Select(static method => new HelperPreservationIdentity(method.Path, method.Syntax.Identifier.ValueText,
            MethodSymbolId(method.Symbol), Canonical(method.Syntax), Sha256(Encoding.UTF8.GetBytes(Canonical(method.Syntax)))))
            .ToArray();

    private static void ValidateHelperPreservation(HelperPreservationIdentity[] identities, MemberIdentity[] members)
    {
        if (identities is null || identities.Length != HelperPreservationRules.Length)
            throw new ExtractionException("The complete source-local helper preservation roster is required.");
        foreach ((HelperPreservationIdentity identity, HelperPreservationRule rule) in identities.Zip(HelperPreservationRules))
        {
            if (identity is null || identity.Path != rule.Path || identity.Member != rule.Member || identity.SymbolId != rule.SymbolId)
                throw new ExtractionException("The exact source-local helper preservation roster changed.");
            if (identity.SyntaxSha256 != rule.SyntaxSha256 || identity.CanonicalSyntax is null ||
                Sha256(Encoding.UTF8.GetBytes(identity.CanonicalSyntax)) != rule.SyntaxSha256)
                throw new ExtractionException($"Source-local helper '{rule.Member}' preservation body changed.");
        }
        foreach (MemberIdentity member in members.Skip(1))
        {
            HelperPreservationIdentity helper = identities.Single(identity => identity.SymbolId == member.Signature);
            if (helper.Path != member.Path || helper.Member != member.Name ||
                helper.CanonicalSyntax != member.Binding.CanonicalSyntax || helper.SyntaxSha256 != member.Binding.SyntaxSha256)
                throw new ExtractionException("The source-local helper/member preservation mapping changed.");
        }
    }

    private static void ValidatePinnedPreservationEffects(IReadOnlyList<SourceFile> sources, Compilation compilation)
    {
        const string selection = "_systemContractHandler=_balManager.Enabled?_balSystemContractHandler.Value:_standardSystemContractHandler.Value";
        int handlerSelections = 0;
        foreach (SourceFile source in sources)
        {
            SemanticModel model = compilation.GetSemanticModel(source.Tree, ignoreAccessibility: true);
            foreach (SyntaxNode syntax in source.Root.DescendantNodes())
            {
                IOperation? target = syntax switch
                {
                    AssignmentExpressionSyntax when model.GetOperation(syntax) is IAssignmentOperation assignment => assignment.Target,
                    PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax when
                        model.GetOperation(syntax) is IIncrementOrDecrementOperation increment => increment.Target,
                    _ => null,
                };
                if (target is not null)
                {
                    foreach (IOperation leaf in AssignmentTargets(target))
                    {
                        string? receiver = PreservedReceiver(leaf);
                        if (receiver is not null)
                        {
                            MethodDeclarationSyntax? owner = syntax.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                            if (receiver == "_systemContractHandler" && source.RelativePath == BlockProcessorPath &&
                                owner is not null && OwnerPath(owner) == "BlockProcessor" && owner.Identifier.ValueText == "ProcessOne" &&
                                Canonical(syntax) == selection)
                                handlerSelections++;
                            else
                                throw new ExtractionException($"Pinned source changes preserved receiver identity: {receiver}.");
                        }
                        if (leaf is IPropertyReferenceOperation property &&
                            property.Property.ContainingType.ToSourceIdentity() == "global::Nethermind.Core.TxReceipt" &&
                            property.Property.Name is "Index" or "Logs")
                            throw new ExtractionException($"Pinned source writes returned receipt projection: {property.Property.Name}.");
                        if (leaf is IArrayElementReferenceOperation element &&
                            ArtifactSafety.TypeIdentity(element.ArrayReference.Type) == "Nethermind.Core.TxReceipt[]")
                            throw new ExtractionException("Pinned source replaces a receipt array element.");
                    }
                }
                ExpressionSyntax? escaped = syntax switch
                {
                    ArgumentSyntax argument when argument.RefKindKeyword.RawKind != 0 => argument.Expression,
                    RefExpressionSyntax reference => reference.Expression,
                    _ => null,
                };
                if (escaped is not null && IsPreservedReferenceType(model.GetTypeInfo(escaped).Type))
                    throw new ExtractionException("Pinned source exposes preserved identity through ref/in/out.");
            }
        }
        if (handlerSelections != 1)
            throw new ExtractionException("The one pre-ProcessBlock handler selection must remain exact.");
    }

    private static IEnumerable<IOperation> AssignmentTargets(IOperation operation)
    {
        switch (operation)
        {
            case IConversionOperation conversion:
                foreach (IOperation target in AssignmentTargets(conversion.Operand)) yield return target;
                break;
            case IParenthesizedOperation parenthesized:
                foreach (IOperation target in AssignmentTargets(parenthesized.Operand)) yield return target;
                break;
            case ITupleOperation tuple:
                foreach (IOperation element in tuple.Elements)
                    foreach (IOperation target in AssignmentTargets(element)) yield return target;
                break;
            default:
                yield return operation;
                break;
        }
    }

    private static string? PreservedReceiver(IOperation operation)
    {
        ISymbol? symbol = operation switch
        {
            IFieldReferenceOperation field => field.Field,
            IPropertyReferenceOperation property => property.Property,
            _ => null,
        };
        return symbol is not null && symbol.ContainingType?.ToSourceIdentity() == BlockProcessorTypeSymbol &&
            symbol.Name is "_stateProvider" or "_systemContractHandler" or "ReceiptsTracer" ? symbol.Name : null;
    }

    private static bool IsPreservedReferenceType(ITypeSymbol? type) => ArtifactSafety.TypeIdentity(type) is
        "Nethermind.Core.Block" or "Nethermind.Core.BlockHeader" or "Nethermind.Core.Specs.IReleaseSpec" or
        "Nethermind.Evm.Tracing.IBlockTracer" or "Nethermind.Evm.State.IWorldState" or
        "Nethermind.Consensus.Processing.BlockProcessor.ISystemContractHandler" or
        "Nethermind.Blockchain.Tracing.BlockReceiptsTracer" or "Nethermind.Core.TxReceipt" or "Nethermind.Core.TxReceipt[]";
}
