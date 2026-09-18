// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Evm.Lean.OrdinaryEvmCompletionExtractor;

internal static class OperationLowering
{
    private static readonly SymbolDisplayFormat IdentityFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMemberOptions(SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters |
            SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeRef | SymbolDisplayMemberOptions.IncludeExplicitInterface)
        .WithParameterOptions(SymbolDisplayParameterOptions.IncludeName | SymbolDisplayParameterOptions.IncludeType |
            SymbolDisplayParameterOptions.IncludeParamsRefOut | SymbolDisplayParameterOptions.IncludeDefaultValue);

    internal static string Symbol(ISymbol? symbol) => symbol switch
    {
        ILocalSymbol local => $"{Symbol(local.ContainingSymbol)}::local:{local.Name}@{(local.Locations.FirstOrDefault()?.SourceSpan.Start ?? -1) - (local.ContainingSymbol.DeclaringSyntaxReferences.FirstOrDefault()?.Span.Start ?? 0)}:{local.Type.ToDisplayString(IdentityFormat)}",
        IParameterSymbol parameter => $"{Symbol(parameter.ContainingSymbol)}::parameter:{parameter.Ordinal}:{parameter.Name}:{parameter.RefKind}:{parameter.Type.ToDisplayString(IdentityFormat)}",
        _ => symbol?.ToDisplayString(IdentityFormat) ?? "",
    };

    internal static OperationTerm Lower(IOperation operation, IReadOnlyDictionary<CaptureId, int>? captures = null)
    {
        string symbol = operation switch
        {
            IInvocationOperation call => Symbol(call.TargetMethod),
            IObjectCreationOperation creation => Symbol(creation.Constructor),
            IFieldReferenceOperation field => Symbol(field.Field),
            IPropertyReferenceOperation property => Symbol(property.Property),
            IParameterReferenceOperation parameter => Symbol(parameter.Parameter),
            ILocalReferenceOperation local => Symbol(local.Local),
            IVariableDeclaratorOperation variable => Symbol(variable.Symbol),
            IMethodReferenceOperation method => Symbol(method.Method),
            IConversionOperation conversion => Symbol(conversion.OperatorMethod),
            IBinaryOperation binary => Symbol(binary.OperatorMethod),
            ICompoundAssignmentOperation assignment => Symbol(assignment.OperatorMethod),
            IIncrementOrDecrementOperation increment => Symbol(increment.OperatorMethod),
            ILocalFunctionOperation localFunction => Symbol(localFunction.Symbol),
            IUnaryOperation unary => Symbol(unary.OperatorMethod),
            IArgumentOperation argument => Symbol(argument.Parameter),
            _ => "",
        };
        string operatorKind = operation switch
        {
            IBinaryOperation binary => $"{binary.OperatorKind};checked={binary.IsChecked};lifted={binary.IsLifted}",
            IUnaryOperation unary => $"{unary.OperatorKind};checked={unary.IsChecked};lifted={unary.IsLifted}",
            ICompoundAssignmentOperation assignment => $"{assignment.OperatorKind};checked={assignment.IsChecked};lifted={assignment.IsLifted}",
            IConversionOperation conversion => $"checked={conversion.IsChecked};tryCast={conversion.IsTryCast}",
            IInvocationOperation invocation => $"virtual={invocation.IsVirtual}",
            IInstanceReferenceOperation instance => instance.ReferenceKind.ToString(),
            IBranchOperation branch => branch.BranchKind.ToString(),
            IIncrementOrDecrementOperation increment => $"checked={increment.IsChecked};lifted={increment.IsLifted};postfix={increment.IsPostfix}",
            ISimpleAssignmentOperation assignment => $"ref={assignment.IsRef}",
            IFlowCaptureOperation capture => "capture=" + Capture(capture.Id),
            IFlowCaptureReferenceOperation reference => "capture=" + Capture(reference.Id),
            _ => "",
        };
        IArgumentOperation? argumentOperation = operation as IArgumentOperation;
        string conversionKind = operation switch
        {
            IConversionOperation conversion => Conversion(conversion.Conversion),
            IArgumentOperation argument => $"in:{Conversion(argument.InConversion)};out:{Conversion(argument.OutConversion)}",
            ICompoundAssignmentOperation assignment => $"in:{Conversion(assignment.InConversion)};out:{Conversion(assignment.OutConversion)}",
            _ => "",
        };
        if (operation is IInvalidOperation or IDynamicInvocationOperation or IDynamicMemberReferenceOperation or
            IAnonymousFunctionOperation or IDelegateCreationOperation or IAwaitOperation or
            IFunctionPointerInvocationOperation)
        {
            throw new AdmissionException($"Unadmitted callable operation: {operation.Kind}.");
        }

        return new(operation.Kind.ToString(), Symbol(operation.Type), operation.IsImplicit,
            symbol, operatorKind,
            operation.ConstantValue.HasValue ? Constant(operation.ConstantValue.Value) : "",
            argumentOperation?.ArgumentKind.ToString() ?? "",
            argumentOperation?.Parameter?.Ordinal ?? -1,
            argumentOperation?.Parameter?.RefKind.ToString() ?? "",
            conversionKind,
            operation.ChildOperations.Select(child => Lower(child, captures)).ToArray());

        int Capture(CaptureId id) => captures is not null && captures.TryGetValue(id, out int index)
            ? index : throw new AdmissionException("Unbound CFG capture identity.");
    }

    private static string Constant(object? value) => value switch
    {
        null => "null",
        bool boolean => boolean ? "bool:true" : "bool:false",
        string text => "string:" + text,
        IFormattable formattable => value.GetType().FullName + ":" + formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => throw new AdmissionException("Unsupported operation constant."),
    };

    private static string Conversion(CommonConversion conversion) =>
        $"exists={conversion.Exists};identity={conversion.IsIdentity};numeric={conversion.IsNumeric};reference={conversion.IsReference};nullable={conversion.IsNullable};user={conversion.IsUserDefined};method={Symbol(conversion.MethodSymbol)}";
}
