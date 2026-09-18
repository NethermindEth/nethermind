// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Evm.Lean.OrdinaryTransactionRefundAdapterExtractor;

internal static class OperationLowering
{
    private static readonly SymbolDisplayFormat IdentityFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMemberOptions(SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters |
            SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeRef | SymbolDisplayMemberOptions.IncludeExplicitInterface)
        .WithParameterOptions(SymbolDisplayParameterOptions.IncludeName | SymbolDisplayParameterOptions.IncludeType |
            SymbolDisplayParameterOptions.IncludeParamsRefOut | SymbolDisplayParameterOptions.IncludeDefaultValue);

    internal static string Symbol(ISymbol? symbol) => symbol?.ToDisplayString(IdentityFormat) ?? "";

    internal static OperationTerm Lower(IOperation operation)
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
            IAnonymousFunctionOperation or ILocalFunctionOperation or IDelegateCreationOperation or IAwaitOperation or
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
            operation.ChildOperations.Select(Lower).ToArray());
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
