// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Evm.Lean.SequentialBlockPostTransactionFinalizationExtractor;

internal static partial class ProcessOneValidatedPublicationExtractor
{
    private const string PublicationValueDiagnostic = "publication.values: ";

    internal static void AuditValueIdentitiesForTest(string root, IReadOnlyDictionary<string, byte[]> overrides)
    {
        PublicationSourceFile[] sources = ReadSources(root, ReadPins(root).Sources, overrides, enforcePins: false);
        PublicationSemanticContext context = CompileSources(root, sources);
        ClassDeclarationSyntax processor = FindClass(sources.Single(source => source.RelativePath == BlockProcessorPath).Root, "BlockProcessor");
        ClassDeclarationSyntax validator = FindClass(sources.Single(source => source.RelativePath == BlockValidatorPath).Root, "BlockValidator");
        InterfaceDeclarationSyntax contract = sources.Single(source => source.RelativePath == BlockValidatorInterfacePath).Root
            .DescendantNodes().OfType<InterfaceDeclarationSyntax>().Single(type => type.Identifier.ValueText == "IBlockValidator");
        ValidatePublicationValueIdentities(context, FindMethod(processor, "ProcessOne", 5),
            FindMethod(processor, "ValidateProcessedBlock", 4), FindMethod(processor, "PostValidation", 4),
            FindMethod(processor, "StoreTxReceipts", 3), FindMethod(validator, "ValidateProcessedBlock", 4), contract);
    }

    private static void ValidatePublicationValueIdentities(PublicationSemanticContext context,
        MethodDeclarationSyntax processOne, MethodDeclarationSyntax validate, MethodDeclarationSyntax post,
        MethodDeclarationSyntax store, MethodDeclarationSyntax validator, InterfaceDeclarationSyntax contract)
    {
        IMethodSymbol Method(MethodDeclarationSyntax declaration) => context.Model(declaration).GetDeclaredSymbol(declaration)
            ?? throw new ExtractionException(PublicationValueDiagnostic + "missing method symbol.");
        void Parameters(MethodDeclarationSyntax declaration, string expectedMethod, params string[] names)
        {
            IMethodSymbol method = Method(declaration);
            if (method.MethodKind != MethodKind.Ordinary || method.Arity != 0 || MethodKey(method) != expectedMethod ||
                !method.Parameters.Select(static parameter => parameter.Name).SequenceEqual(names, StringComparer.Ordinal) ||
                method.Parameters.Where((parameter, ordinal) => parameter.Ordinal != ordinal).Any())
                throw new ExtractionException(PublicationValueDiagnostic + "exact declaration parameter roles changed: " + declaration.Identifier.ValueText);
        }
        Parameters(processOne, ProcessOneFqn, "suggestedBlock", "options", "blockTracer", "spec", "token");
        Parameters(validate, ValidateProcessedBlockFqn, "suggestedBlock", "options", "block", "receipts");
        Parameters(post, PostValidationFqn, "suggestedBlock", "processedBlock", "receipts", "options");
        Parameters(store, StoreReceiptsFqn, "block", "txReceipts", "spec");
        Parameters(validator, ValidatorImplementationFqn, "processedBlock", "receipts", "suggestedBlock", "error");

        ISymbol Value(MethodDeclarationSyntax declaration, string name)
        {
            IParameterSymbol? parameter = Method(declaration).Parameters.SingleOrDefault(candidate => candidate.Name == name);
            if (parameter is not null) return parameter;
            SemanticModel model = context.Model(declaration);
            ISymbol[] locals = declaration.DescendantNodes().Where(node => node is VariableDeclaratorSyntax or SingleVariableDesignationSyntax)
                .Select(node => model.GetDeclaredSymbol(node)).OfType<ILocalSymbol>()
                .Where(local => local.Name == name).Cast<ISymbol>().ToArray();
            return locals.Length == 1 ? locals[0] :
                throw new ExtractionException(PublicationValueDiagnostic + "local identity is not unique: " + name);
        }

        void Reference(IOperation operation, ISymbol expected)
        {
            while (operation is IConversionOperation conversion)
            {
                if (!conversion.IsImplicit || conversion.OperatorMethod is not null || !conversion.Conversion.IsIdentity)
                    throw new ExtractionException(PublicationValueDiagnostic + "contextual conversion changed.");
                operation = conversion.Operand;
            }
            if (operation is IDeclarationExpressionOperation declaration) operation = declaration.Expression;
            ISymbol? actual = operation switch
            {
                IParameterReferenceOperation parameter => parameter.Parameter,
                ILocalReferenceOperation local => local.Local,
                _ => null,
            };
            if (!SymbolEqualityComparer.Default.Equals(actual, expected))
                throw new ExtractionException(PublicationValueDiagnostic + "argument or receiver identity changed.");
        }

        void Arguments(IArgumentOperation[] arguments, MethodDeclarationSyntax owner, params string[] names)
        {
            if (arguments.Length != names.Length) throw new ExtractionException(PublicationValueDiagnostic + "argument count changed.");
            for (int index = 0; index < names.Length; index++)
            {
                IArgumentOperation argument = arguments[index];
                RefKind expectedRef = names[index] == "error" ? RefKind.Out : RefKind.None;
                if (argument.Parameter?.Ordinal != index || argument.Parameter.RefKind != expectedRef ||
                    argument.ArgumentKind != ArgumentKind.Explicit || !argument.InConversion.IsIdentity || !argument.OutConversion.IsIdentity)
                    throw new ExtractionException(PublicationValueDiagnostic + "argument ordinal, ref-kind, or contextual conversion changed.");
                Reference(argument.Value, Value(owner, names[index]));
            }
        }

        InvocationExpressionSyntax Invocation(MethodDeclarationSyntax owner, string name)
        {
            InvocationExpressionSyntax[] calls = owner.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call =>
                call.Expression switch
                {
                    IdentifierNameSyntax identifier => identifier.Identifier.ValueText == name,
                    MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText == name,
                    _ => false,
                }).ToArray();
            return calls.Length == 1 ? calls[0] : throw new ExtractionException(PublicationValueDiagnostic + "call site is not unique.");
        }

        void Call(MethodDeclarationSyntax owner, string name, IMethodSymbol expected, params string[] names)
        {
            InvocationExpressionSyntax invocation = Invocation(owner, name);
            if (context.Model(invocation).GetOperation(invocation) is not IInvocationOperation call ||
                call.TargetMethod.MethodKind != MethodKind.Ordinary || call.TargetMethod.Arity != 0 ||
                !SymbolEqualityComparer.Default.Equals(call.TargetMethod, expected))
                throw new ExtractionException(PublicationValueDiagnostic + "internal call does not target its exact member declaration.");
            if (call.Instance is not IInstanceReferenceOperation instance ||
                !SymbolEqualityComparer.Default.Equals(instance.Type, Method(owner).ContainingType))
                throw new ExtractionException(PublicationValueDiagnostic + "internal receiver changed.");
            Arguments(call.Arguments.ToArray(), owner, names);
        }

        MethodDeclarationSyntax processBlock = FindMethod((ClassDeclarationSyntax)processOne.Parent!, "ProcessBlock", 5);
        Call(processOne, "ProcessBlock", Method(processBlock),
            "block", "blockTracer", "options", "spec", "token");
        Call(processOne, "ValidateProcessedBlock", Method(validate),
            "suggestedBlock", "options", "block", "receipts");
        Call(validate, "PostValidation", Method(post),
            "suggestedBlock", "block", "receipts", "options");
        Call(processOne, "StoreTxReceipts", Method(store), "block", "receipts", "spec");

        void ExternalCall(MethodDeclarationSyntax owner, string name, string expectedTarget,
            string receiverName, int receiverOrdinal, params string[] arguments)
        {
            InvocationExpressionSyntax invocation = Invocation(owner, name);
            if (context.Model(invocation).GetOperation(invocation) is not IInvocationOperation call ||
                call.TargetMethod.MethodKind != MethodKind.Ordinary || call.TargetMethod.Arity != 0 ||
                MethodKey(call.TargetMethod) != expectedTarget ||
                call.Instance is not IParameterReferenceOperation receiver ||
                receiver.Parameter.Name != receiverName || receiver.Parameter.Ordinal != receiverOrdinal ||
                receiver.Parameter.ContainingSymbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } constructor ||
                !SymbolEqualityComparer.Default.Equals(constructor.ContainingType, Method(owner).ContainingType))
                throw new ExtractionException(PublicationValueDiagnostic + "external call target or captured constructor receiver changed.");
            Arguments(call.Arguments.ToArray(), owner, arguments);
            if (receiverName == "blockValidator")
            {
                IMethodSymbol expected = Method(contract.Members.OfType<MethodDeclarationSyntax>()
                    .Single(method => method.Identifier.ValueText == "ValidateProcessedBlock"));
                if (!SymbolEqualityComparer.Default.Equals(call.TargetMethod, expected))
                    throw new ExtractionException(PublicationValueDiagnostic + "validator contract declaration changed.");
            }
        }
        ExternalCall(validate, "ValidateProcessedBlock",
            ValidatorCallFqn, "blockValidator", 1, "block", "receipts", "suggestedBlock", "error");
        ExternalCall(store, "InsertDeferred", InsertDeferredFqn,
            "receiptStorage", 5, "block", "txReceipts", "spec");

        if (context.Model(processOne).GetOperation(FindReturn(processOne).Expression!) is not ITupleOperation tuple ||
            tuple.Elements.Length != 2)
            throw new ExtractionException(PublicationValueDiagnostic + "returned tuple operation changed.");
        Reference(tuple.Elements[0], Value(processOne, "block"));
        Reference(tuple.Elements[1], Value(processOne, "receipts"));

        IPropertyReferenceOperation Property(IOperation operation, string propertyName, ISymbol receiver)
        {
            if (operation is not IPropertyReferenceOperation property || property.Property.IsStatic || property.Property.IsIndexer ||
                property.Property.ContainingType.ToDisplayString() != "Nethermind.Core.Block" || property.Property.Name != propertyName ||
                property.Arguments.Length != 0 || property.Instance is null)
                throw new ExtractionException(PublicationValueDiagnostic + "publication operand is not the exact Block property.");
            Reference(property.Instance, receiver);
            return property;
        }
        foreach (string propertyName in new[] { "AccountChanges", "ExecutionRequests", "GeneratedBlockAccessList", "EncodedBlockAccessList" })
        {
            AssignmentExpressionSyntax[] assignments = post.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Where(assignment => assignment.Left is MemberAccessExpressionSyntax member &&
                    member.Name.Identifier.ValueText == propertyName).ToArray();
            if (assignments.Length != 1)
                throw new ExtractionException(PublicationValueDiagnostic + "publication assignment is not unique.");
            AssignmentExpressionSyntax syntax = assignments[0];
            if (context.Model(syntax).GetOperation(syntax) is not ISimpleAssignmentOperation assignment || assignment.IsRef)
                throw new ExtractionException(PublicationValueDiagnostic + "publication is not a simple value assignment.");
            IPropertyReferenceOperation target = Property(assignment.Target, propertyName, Value(post, "suggestedBlock"));
            if (propertyName == "EncodedBlockAccessList")
            {
                if (assignment.Value is not ICoalesceOperation coalesce || !coalesce.ValueConversion.IsIdentity)
                    throw new ExtractionException(PublicationValueDiagnostic + "encoded BAL fallback conversion changed.");
                IPropertyReferenceOperation first = Property(coalesce.Value, propertyName, Value(post, "processedBlock"));
                IPropertyReferenceOperation fallback = Property(coalesce.WhenNull, propertyName, Value(post, "suggestedBlock"));
                if (!SymbolEqualityComparer.Default.Equals(target.Property, first.Property) ||
                    !SymbolEqualityComparer.Default.Equals(target.Property, fallback.Property))
                    throw new ExtractionException(PublicationValueDiagnostic + "encoded BAL property symbol changed.");
            }
            else
            {
                IPropertyReferenceOperation value = Property(assignment.Value, propertyName, Value(post, "processedBlock"));
                if (!SymbolEqualityComparer.Default.Equals(target.Property, value.Property))
                    throw new ExtractionException(PublicationValueDiagnostic + "publication source property changed.");
            }
        }
        foreach (MethodDeclarationSyntax owner in new[] { processOne, validate, post, store })
        {
            if (owner.DescendantNodes().Any(node => node is LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax) ||
                context.Model(owner).GetOperation(owner) is { } body && Operations(body).Any(operation => operation is IMethodReferenceOperation))
                throw new ExtractionException(PublicationValueDiagnostic + "additional lexical callable or method-reference route is not admitted.");
        }
    }
}
