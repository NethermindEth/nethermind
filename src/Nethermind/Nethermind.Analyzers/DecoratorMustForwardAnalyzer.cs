// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Nethermind.Analyzers;

/// <summary>
/// Reports concrete classes that decorate an interface (hold a field, property, or
/// constructor parameter of that interface type) yet rely on the default implementation
/// of an interface member marked <c>[MustForwardOnDecorate]</c>. Such classes silently
/// drop side effects on the wrapped instance.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DecoratorMustForwardAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "NETH007";

    private const string MustForwardAttributeMetadataName =
        "Nethermind.Core.Attributes.MustForwardOnDecorateAttribute";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Decorator does not forward a [MustForwardOnDecorate] interface member",
        messageFormat: "'{0}' wraps '{1}' but uses the default implementation of '{2}'. Add an explicit implementation that forwards the call to the inner instance.",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Interface members marked [MustForwardOnDecorate] have a no-op default " +
                     "intended only for non-decorating implementers. A class that wraps another " +
                     "instance of the same interface (via a field, property, or constructor " +
                     "parameter) must explicitly implement each tagged member and forward the " +
                     "call, otherwise side effects on inner implementations are silently lost.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static startContext =>
        {
            INamedTypeSymbol? attribute = startContext.Compilation.GetTypeByMetadataName(MustForwardAttributeMetadataName);
            if (attribute is null)
                return;

            startContext.RegisterSymbolAction(c => AnalyzeType(c, attribute), SymbolKind.NamedType);
        });
    }

    private static void AnalyzeType(SymbolAnalysisContext context, INamedTypeSymbol attribute)
    {
        if (context.Symbol is not INamedTypeSymbol type)
            return;
        if (type.TypeKind != TypeKind.Class)
            return;
        // Abstract decorators are still flagged: if a concrete subclass inherits the wrapped
        // field/parameter from an abstract base, the missing forward lives in the base, and
        // the subclass would not be detected as a decorator (it has no IFoo of its own).
        if (type.IsStatic)
            return;
        if (type.AllInterfaces.IsDefaultOrEmpty)
            return;

        foreach (INamedTypeSymbol iface in type.AllInterfaces)
        {
            List<ISymbol>? tagged = CollectTaggedMembers(iface, attribute);
            if (tagged is null)
                continue;

            if (!IsDecoratorOf(type, iface))
                continue;

            foreach (ISymbol taggedMember in tagged)
            {
                ISymbol? impl = type.FindImplementationForInterfaceMember(taggedMember);
                // impl == null can happen for unsupported member kinds; impl.ContainingType
                // being the interface itself means we're still using the DIM.
                if (impl is null || impl.ContainingType is { TypeKind: TypeKind.Interface })
                {
                    Location location = type.Locations.FirstOrDefault() ?? Location.None;
                    context.ReportDiagnostic(Diagnostic.Create(
                        Rule,
                        location,
                        type.Name,
                        iface.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        taggedMember.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
                }
            }
        }
    }

    private static List<ISymbol>? CollectTaggedMembers(INamedTypeSymbol iface, INamedTypeSymbol attribute)
    {
        List<ISymbol>? tagged = null;
        foreach (ISymbol member in iface.GetMembers())
        {
            // Restrict to ordinary methods and properties; property accessors are checked via the property.
            bool isTarget = member is IMethodSymbol { MethodKind: MethodKind.Ordinary }
                         || member is IPropertySymbol;
            if (!isTarget)
                continue;
            if (!HasAttribute(member, attribute))
                continue;
            tagged ??= [];
            tagged.Add(member);
        }
        return tagged;
    }

    private static bool HasAttribute(ISymbol member, INamedTypeSymbol attribute)
    {
        foreach (AttributeData data in member.GetAttributes())
        {
            if (SymbolEqualityComparer.Default.Equals(data.AttributeClass, attribute))
                return true;
        }
        return false;
    }

    private static bool IsDecoratorOf(INamedTypeSymbol type, INamedTypeSymbol iface)
    {
        // Instance fields and properties typed as the interface (or a subtype).
        foreach (ISymbol member in type.GetMembers())
        {
            switch (member)
            {
                case IFieldSymbol { IsStatic: false, IsImplicitlyDeclared: false } f
                    when TypeImplements(f.Type, iface):
                    return true;
                case IPropertySymbol { IsStatic: false } p
                    when TypeImplements(p.Type, iface):
                    return true;
            }
        }

        // Constructor parameters (covers primary constructors, whose captured fields are
        // synthesized and not surfaced as IFieldSymbol on the type).
        foreach (IMethodSymbol ctor in type.InstanceConstructors)
        {
            foreach (IParameterSymbol param in ctor.Parameters)
            {
                if (TypeImplements(param.Type, iface))
                    return true;
            }
        }

        return false;
    }

    private static bool TypeImplements(ITypeSymbol candidate, INamedTypeSymbol iface)
    {
        if (SymbolEqualityComparer.Default.Equals(candidate, iface))
            return true;
        foreach (INamedTypeSymbol implemented in candidate.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(implemented, iface))
                return true;
            // Match across generic constructions: List<T> vs List<int> share OriginalDefinition.
            if (SymbolEqualityComparer.Default.Equals(implemented.OriginalDefinition, iface.OriginalDefinition))
                return true;
        }
        return false;
    }
}
