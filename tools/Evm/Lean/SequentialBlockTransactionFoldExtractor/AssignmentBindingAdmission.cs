// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Evm.Lean.SequentialBlockTransactionFoldExtractor;

internal static partial class Extractor
{
    private const string TraceStartSignature = "M:" + TracerType + ".StartNewTxTrace(Nethermind.Core.Transaction)";
    private const string BalPrepareSignature = "M:" + BalType +
        ".PrepareForProcessing(Nethermind.Core.Block,Nethermind.Core.Specs.IReleaseSpec,Nethermind.Consensus.Processing.ProcessingOptions)";

    private static AssignmentValueBinding[] CaptureAssignmentValue(IOperation operation)
    {
        if (operation is not ISimpleAssignmentOperation assignment) return [];
        List<AssignmentValueBinding> bindings = [];
        Visit(assignment.Value, bindings);
        return [.. bindings];

        static void Visit(IOperation value, List<AssignmentValueBinding> bindings)
        {
            ISymbol? symbol = value switch
            {
                IInvocationOperation call => call.TargetMethod,
                IObjectCreationOperation creation => creation.Constructor,
                IFieldReferenceOperation field => field.Field,
                IPropertyReferenceOperation property => property.Property,
                IParameterReferenceOperation parameter => parameter.Parameter,
                ILocalReferenceOperation local => local.Local,
                IMethodReferenceOperation method => method.Method,
                IEventReferenceOperation @event => @event.Event,
                _ => null,
            };
            IOperation? receiver = value switch
            {
                IInvocationOperation call => call.Instance,
                IMemberReferenceOperation member => member.Instance,
                _ => null,
            };
            if (symbol is not null)
                bindings.Add(new(value.Kind.ToString(), CanonicalSymbol(symbol), TypeId(value.Type),
                    receiver is null ? string.Empty : TypeId(receiver.Type),
                    AssignmentReceiverSymbol(receiver),
                    symbol is IParameterSymbol parameter ? parameter.Ordinal : -1));
            foreach (IOperation child in value.ChildOperations) Visit(child, bindings);
        }
    }

    private static string AssignmentReceiverSymbol(IOperation? receiver) => receiver switch
    {
        null => string.Empty,
        IInstanceReferenceOperation instance => "this:" + TypeId(instance.Type),
        IFieldReferenceOperation field => CanonicalSymbol(field.Field),
        IPropertyReferenceOperation property => CanonicalSymbol(property.Property),
        IParameterReferenceOperation parameter => CanonicalSymbol(parameter.Parameter),
        ILocalReferenceOperation local => CanonicalSymbol(local.Local),
        IInvocationOperation invocation => CanonicalSymbol(invocation.TargetMethod),
        _ => "expression:" + Canonical(receiver.Syntax),
    };

    private static AssignmentValueBinding[] AssignmentValueContract(string id) => id switch
    {
        "tracer.tx-start-current" =>
        [
            new("ParameterReference", TraceStartSignature + "/parameter:0:tx", "Nethermind.Core.Transaction", "", "", 0),
        ],
        "tracer.tx-start-delegate" =>
        [
            new("Invocation", "M:Nethermind.Evm.Tracing.IBlockTracer.StartNewTxTrace(Nethermind.Core.Transaction)",
                "Nethermind.Evm.Tracing.ITxTracer", "Nethermind.Evm.Tracing.IBlockTracer", "F:" + TracerType + "._otherTracer", -1),
            new("FieldReference", "F:" + TracerType + "._otherTracer", "Nethermind.Evm.Tracing.IBlockTracer",
                TracerType, "this:" + TracerType, -1),
            new("ParameterReference", TraceStartSignature + "/parameter:0:tx", "Nethermind.Core.Transaction", "", "", 0),
        ],
        "tracer.receipt-index" =>
        [
            new("PropertyReference", "P:" + TracerType + "._currentIndex", "int", TracerType, "this:" + TracerType, -1),
        ],
        "bal.enabled-spec" =>
        [
            new("PropertyReference", "P:Nethermind.Core.Specs.IReleaseSpec.BlockLevelAccessListsEnabled", "bool",
                "Nethermind.Core.Specs.IReleaseSpec", BalPrepareSignature + "/parameter:1:spec", -1),
            new("ParameterReference", BalPrepareSignature + "/parameter:1:spec", "Nethermind.Core.Specs.IReleaseSpec", "", "", 1),
        ],
        "bal.enabled-derived" =>
        [
            new("FieldReference", "F:" + BalType + "._blockAccessListsEnabled", "bool", BalType, "this:" + BalType, -1),
            new("PropertyReference", "P:Nethermind.Core.Block.IsGenesis", "bool", "Nethermind.Core.Block",
                BalPrepareSignature + "/parameter:0:suggestedBlock", -1),
            new("ParameterReference", BalPrepareSignature + "/parameter:0:suggestedBlock", "Nethermind.Core.Block", "", "", 0),
        ],
        _ => [],
    };

    private static void ValidateAssignmentValue(string id, AssignmentValueBinding[]? bindings)
    {
        if (bindings is null || !bindings.SequenceEqual(AssignmentValueContract(id)))
            throw new ExtractionException($"fold.assignment.{id}: exact right-hand-side bindings changed.");
    }
}
