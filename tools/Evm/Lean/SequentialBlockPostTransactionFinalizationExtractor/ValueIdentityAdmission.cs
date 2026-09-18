// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Evm.Lean.SequentialBlockPostTransactionFinalizationExtractor;

internal static partial class Extractor
{
    private const string HeaderLocalSymbol = BlockProcessorTypeSymbol + ".ProcessBlock.header";
    private const string BlockParameterSymbol = ProcessBlockMethodSymbol + ".0";
    private const string HeaderInitializer = "header=block.Header";
    private const string HeaderValueSymbol = "global::Nethermind.Core.Block.Header";

    private sealed record ValueIdentityRule(string Name, string SymbolId, string Type,
        string DefinitionCanonical, string[] ReadContexts);

    private static readonly ValueIdentityRule[] PreservedValueRules =
    [
        new("header", HeaderLocalSymbol, "Nethermind.Core.BlockHeader", HeaderInitializer,
        [
            "_systemContractHandler.ApplyBlockhashStateChanges(header,spec);",
            "header.BlobGasUsed=BlobGasCalculator.CalculateBlobGas(block.Transactions);",
            "header.ReceiptsRoot=CalculateReceiptsRoot(receipts,spec,block);",
            "ShouldComputeStateRoot(header)", "ComputeStateRoot(header);",
            "(header.Bloom,header.ReceiptsRoot)=bloomsAndReceiptsRootTask.GetAwaiter().GetResult();",
            "(header.Bloom,header.ReceiptsRoot)=bloomsAndReceiptsRootTask.GetAwaiter().GetResult();",
            "header.Hash=header.CalculateHash();", "header.Hash=header.CalculateHash();",
        ]),
        new("block", BlockParameterSymbol, "Nethermind.Core.Block", "Blockblock",
        [
            "BlockBodybody=block.Body;", "BlockHeaderheader=block.Header;",
            "ReceiptsTracer.StartNewBlockTrace(block);",
            "_blockTransactionsExecutor.SetBlockExecutionContext(CreateBlockExecutionContext(block.Header,spec));",
            "_balManager.Setup(block);", "_systemContractHandler.StoreBeaconRoot(block,spec,NullTxTracer.Instance);",
            "TxReceipt[]receipts=_blockTransactionsExecutor.ProcessTransactions(block,options,ReceiptsTracer,token);",
            "header.BlobGasUsed=BlobGasCalculator.CalculateBlobGas(block.Transactions);",
            "return(AccumulateBlockBloom(receipts),CalculateReceiptsRoot(receipts,spec,block));",
            "header.ReceiptsRoot=CalculateReceiptsRoot(receipts,spec,block);",
            "ApplyMinerRewards(block,blockTracer,spec);", "_systemContractHandler.ProcessWithdrawals(block,spec);",
            "_systemContractHandler.ProcessExecutionRequests(block,_stateProvider,receipts,spec);",
            "SetAccountChanges(block);", "_balManager.SetBlockAccessList(block);",
        ]),
        new("spec", ProcessBlockMethodSymbol + ".3", "Nethermind.Core.Specs.IReleaseSpec", "IReleaseSpecspec",
        [
            "_blockTransactionsExecutor.SetBlockExecutionContext(CreateBlockExecutionContext(block.Header,spec));",
            "_systemContractHandler.StoreBeaconRoot(block,spec,NullTxTracer.Instance);",
            "_systemContractHandler.ApplyBlockhashStateChanges(header,spec);",
            "CommitState(spec);", "CommitState(spec);", "spec.IsEip4844Enabled",
            "return(AccumulateBlockBloom(receipts),CalculateReceiptsRoot(receipts,spec,block));",
            "header.ReceiptsRoot=CalculateReceiptsRoot(receipts,spec,block);",
            "ApplyMinerRewards(block,blockTracer,spec);", "_systemContractHandler.ProcessWithdrawals(block,spec);",
            "CommitState(spec);", "_systemContractHandler.ProcessExecutionRequests(block,_stateProvider,receipts,spec);",
            "CommitStateAndStorageRoots(spec);",
        ]),
        new("blockTracer", ProcessBlockMethodSymbol + ".1", "Nethermind.Evm.Tracing.IBlockTracer", "IBlockTracerblockTracer",
        ["ReceiptsTracer.SetOtherTracer(blockTracer);", "ApplyMinerRewards(block,blockTracer,spec);"]),
        new("_stateProvider", BlockProcessorTypeSymbol + "._stateProvider", "Nethermind.Evm.State.IWorldState", "_stateProvider=stateProvider",
        ["_systemContractHandler.ProcessExecutionRequests(block,_stateProvider,receipts,spec);"]),
        new("_systemContractHandler", BlockProcessorTypeSymbol + "._systemContractHandler",
            "Nethermind.Consensus.Processing.BlockProcessor.ISystemContractHandler", "_systemContractHandler",
        [
            "_systemContractHandler.StoreBeaconRoot(block,spec,NullTxTracer.Instance);",
            "_systemContractHandler.ApplyBlockhashStateChanges(header,spec);",
            "_systemContractHandler.ProcessWithdrawals(block,spec);",
            "_systemContractHandler.ProcessExecutionRequests(block,_stateProvider,receipts,spec);",
        ]),
        new("ReceiptsTracer", BlockProcessorTypeSymbol + ".ReceiptsTracer", "Nethermind.Blockchain.Tracing.BlockReceiptsTracer",
            "protectedBlockReceiptsTracerReceiptsTracer{get;set;}=new();",
        [
            "ReceiptsTracer.SetOtherTracer(blockTracer);", "ReceiptsTracer.StartNewBlockTrace(block);",
            "TxReceipt[]receipts=_blockTransactionsExecutor.ProcessTransactions(block,options,ReceiptsTracer,token);",
            "ReceiptsTracer.EndBlockTrace(accumulateBlockBloom:bloomsAndReceiptsRootTaskisnull);",
        ]),
    ];

    private static PreservedValueIdentity[] BuildPreservedValues(MethodDeclarationSyntax method, SemanticModel model)
    {
        IMethodSymbol owner = model.GetDeclaredSymbol(method) as IMethodSymbol
            ?? throw new ExtractionException("Preserved value owner is unresolved.");
        IOperation body = model.GetOperation(method) ?? throw new ExtractionException("Preserved value body is unresolved.");
        List<PreservedValueIdentity> result = [];
        foreach (ValueIdentityRule rule in PreservedValueRules)
        {
            ISymbol symbol;
            if (rule.Name == "header")
            {
                VariableDeclaratorSyntax[] definitions = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                    .Where(variable => variable.Identifier.ValueText == rule.Name).ToArray();
                if (definitions.Length != 1 || Canonical(definitions[0]) != HeaderInitializer ||
                    model.GetOperation(definitions[0].Initializer!.Value) is not IPropertyReferenceOperation
                    {
                        Instance: IParameterReferenceOperation block,
                    } header || header.Property.ToSourceIdentity() != HeaderValueSymbol ||
                    block.Parameter.ToSourceIdentity() != BlockParameterSymbol)
                    throw new ExtractionException("Header identity must be the exact block.Header local initialization.");
                symbol = model.GetDeclaredSymbol(definitions[0]) ?? throw new ExtractionException("Header identity local is unresolved.");
            }
            else
            {
                symbol = owner.Parameters.FirstOrDefault(parameter => parameter.Name == rule.Name) ??
                    owner.ContainingType.GetMembers(rule.Name).Single();
            }

            SyntaxNode definition = symbol.DeclaringSyntaxReferences.Single().GetSyntax();
            ITypeSymbol type = symbol switch
            {
                ILocalSymbol local => local.Type,
                IParameterSymbol parameter => parameter.Type,
                IFieldSymbol field => field.Type,
                IPropertySymbol property => property.Type,
                _ => throw new ExtractionException($"Preserved identity '{rule.Name}' has an unexpected symbol kind."),
            };
            IdentifierNameSyntax[] references = method.DescendantNodes().OfType<IdentifierNameSyntax>()
                .Where(identifier => identifier.Identifier.ValueText == rule.Name).ToArray();
            foreach (IdentifierNameSyntax reference in references)
            {
                if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(reference).Symbol, symbol))
                    throw new ExtractionException($"Preserved identity '{rule.Name}' has a redirected reference symbol.");
                if (reference.Ancestors().OfType<RefExpressionSyntax>().Any() ||
                    reference.Ancestors().OfType<ArgumentSyntax>().Any(static argument => argument.RefKindKeyword.RawKind != 0))
                    throw new ExtractionException($"Preserved identity '{rule.Name}' escapes through ref/in/out.");
            }
            foreach (IOperation operation in DescendantOperations(body))
            {
                if (operation is IAssignmentOperation assignment && WritesIdentity(assignment.Target, symbol) ||
                    operation is IIncrementOrDecrementOperation increment && WritesIdentity(increment.Target, symbol))
                    throw new ExtractionException($"Preserved identity '{rule.Name}' has a competing definition.");
                if (operation is IArgumentOperation { Parameter.RefKind: not RefKind.None } argument &&
                    !IsExactExecutorContextArgument(argument, model.Compilation) &&
                    DescendantOperations(argument.Value).Any(value => ReferencesIdentity(value, symbol)))
                    throw new ExtractionException($"Preserved identity '{rule.Name}' escapes through ref/in/out.");
            }
            result.Add(new(rule.Name, symbol.ToSourceIdentity(), ArtifactSafety.TypeIdentity(type), Canonical(definition),
                references.Select(ValueContext).ToArray(), references.Select(static reference => reference.SpanStart).ToArray()));
        }
        PreservedValueIdentity[] identities = result.ToArray();
        ValidatePreservedValues(identities);
        return identities;
    }

    private static bool ReferencesIdentity(IOperation operation, ISymbol symbol) => operation switch
    {
        ILocalReferenceOperation local => SymbolEqualityComparer.Default.Equals(local.Local, symbol),
        IParameterReferenceOperation parameter => SymbolEqualityComparer.Default.Equals(parameter.Parameter, symbol),
        IFieldReferenceOperation field => SymbolEqualityComparer.Default.Equals(field.Field, symbol),
        IPropertyReferenceOperation property => SymbolEqualityComparer.Default.Equals(property.Property, symbol),
        _ => false,
    };

    private static bool WritesIdentity(IOperation target, ISymbol symbol) =>
        AssignmentTargets(target).Any(operation => ReferencesIdentity(operation, symbol));

    private static string ValueContext(IdentifierNameSyntax reference)
    {
        StatementSyntax statement = reference.Ancestors().OfType<StatementSyntax>().First();
        return Canonical(statement is IfStatementSyntax condition ? condition.Condition : statement);
    }

    private static void ValidatePreservedValues(PreservedValueIdentity[] identities)
    {
        if (identities is null || identities.Length != PreservedValueRules.Length)
            throw new ExtractionException("The complete preserved-value identity ledger is required.");
        foreach ((PreservedValueIdentity identity, ValueIdentityRule rule) in identities.Zip(PreservedValueRules))
        {
            if (identity is null || identity.Name != rule.Name || identity.SymbolId != rule.SymbolId || identity.Type != rule.Type ||
                identity.DefinitionCanonical != rule.DefinitionCanonical || identity.ReadContexts is null || identity.ReadPositions is null ||
                !identity.ReadContexts.SequenceEqual(rule.ReadContexts, StringComparer.Ordinal) ||
                identity.ReadPositions.Length != rule.ReadContexts.Length || !IsStrictlySortedDistinct(identity.ReadPositions) ||
                identity.ReadPositions.Any(static position => position <= 0))
                throw new ExtractionException($"Preserved identity '{rule.Name}' declaration or exact read/receiver contexts changed.");
        }
    }
}
