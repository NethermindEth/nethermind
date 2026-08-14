// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Analyzers;

/// <summary>
/// Reports local variables assigned from object-creation expressions (<c>new T(...)</c>)
/// whose value is never read afterward. This closes the gap where IDE0059 silently skips
/// <c>new</c> expressions because their constructors may have side effects.
///
/// Constructors marked with <c>[ConstructorWithSideEffect]</c> are exempt — the object
/// creation is treated as intentional even when the result is unused.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnusedNewExpressionAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "NETH001";

    private const string ConstructorWithSideEffectAttributeName = "ConstructorWithSideEffectAttribute";

    private static readonly LocalizableString Title =
        "Constructed object is never used";

    private static readonly LocalizableString MessageFormat =
        "'{0}' is assigned a new '{1}' but is never read. Remove the assignment or use a discard if the constructor has side effects.";

    private static readonly LocalizableString Description =
        "A local variable is assigned from an object-creation expression but the variable is " +
        "never referenced afterward. This usually indicates dead code. If the constructor has " +
        "intentional side effects, mark it with [ConstructorWithSideEffect] to suppress this warning.";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        Title,
        MessageFormat,
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: Description);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationBlockAction(AnalyzeOperationBlock);
    }

    private static void AnalyzeOperationBlock(OperationBlockAnalysisContext context)
    {
        // Candidates: locals assigned from `new T(...)` whose constructor lacks [ConstructorWithSideEffect]
        Dictionary<ILocalSymbol, (Location Location, INamedTypeSymbol CreatedType)> candidates = new(
            SymbolEqualityComparer.Default);

        // Locals that are referenced anywhere after declaration
        HashSet<ILocalSymbol> referencedLocals = new(SymbolEqualityComparer.Default);

        foreach (IOperation operation in context.OperationBlocks.SelectMany(Flatten))
        {
            switch (operation)
            {
                case IVariableDeclaratorOperation declarator:
                    // Skip discard variables (named "_")
                    if (declarator.Symbol.Name == "_")
                        break;

                    IVariableInitializerOperation? initializer = declarator.Initializer;
                    // Walk through conversions to find the underlying object creation
                    IOperation? value = initializer?.Value;
                    while (value is IConversionOperation conversion)
                        value = conversion.Operand;

                    if (value is IObjectCreationOperation creation
                        && creation.Type is INamedTypeSymbol createdType)
                    {
                        bool hasSideEffectAttr = creation.Constructor is not null
                            && HasConstructorWithSideEffectAttribute(creation.Constructor);
                        if (!hasSideEffectAttr)
                        {
                            candidates[declarator.Symbol] = (declarator.Syntax.GetLocation(), createdType);
                        }
                    }
                    break;

                // Any use of a local (read or write) after declaration
                case ILocalReferenceOperation localRef:
                    referencedLocals.Add(localRef.Local);
                    break;
            }
        }

        foreach (KeyValuePair<ILocalSymbol, (Location Location, INamedTypeSymbol CreatedType)> entry in candidates)
        {
            if (!referencedLocals.Contains(entry.Key))
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(Rule, entry.Value.Location, entry.Key.Name, entry.Value.CreatedType.Name));
            }
        }
    }

    private static bool HasConstructorWithSideEffectAttribute(IMethodSymbol constructor)
    {
        foreach (AttributeData attribute in constructor.GetAttributes())
        {
            if (attribute.AttributeClass?.Name == ConstructorWithSideEffectAttributeName
                && attribute.AttributeClass.ContainingNamespace?.ToDisplayString() == "Nethermind.Core.Attributes")
                return true;
        }
        return false;
    }

    private static IEnumerable<IOperation> Flatten(IOperation root)
    {
        Stack<IOperation> stack = new();
        stack.Push(root);

        while (stack.Count > 0)
        {
            IOperation current = stack.Pop();
            yield return current;

            foreach (IOperation child in current.ChildOperations)
            {
                stack.Push(child);
            }
        }
    }
}
