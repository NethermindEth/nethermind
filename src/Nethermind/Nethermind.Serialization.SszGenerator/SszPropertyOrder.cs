using Microsoft.CodeAnalysis;

internal static class SszPropertyOrder
{
    internal static IEnumerable<IPropertySymbol> GetPublicProperties(ITypeSymbol type)
    {
        List<ITypeSymbol> typeChain = [];
        ITypeSymbol? current = type;
        while (current is not null && current.SpecialType == SpecialType.None)
        {
            typeChain.Add(current);
            current = current.BaseType;
        }
        typeChain.Reverse();

        Dictionary<string, IPropertySymbol> mostDerived = new(StringComparer.Ordinal);
        foreach (ITypeSymbol t in typeChain)
        {
            foreach (IPropertySymbol p in GetPublicReadWriteProperties(t))
            {
                mostDerived[p.Name] = p;
            }
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (ITypeSymbol t in typeChain)
        {
            foreach (IPropertySymbol p in GetPublicReadWriteProperties(t))
            {
                if (!seen.Add(p.Name))
                    continue;

                if (!mostDerived.TryGetValue(p.Name, out IPropertySymbol? canonical))
                    continue;

                if (canonical.GetAttributes().Any(a => a.AttributeClass?.Name == "SszIgnoreAttribute"))
                    continue;

                yield return canonical;
            }
        }
    }

    private static IEnumerable<IPropertySymbol> GetPublicReadWriteProperties(ITypeSymbol type) =>
        type.GetMembers().OfType<IPropertySymbol>()
            .Where(p => p.GetMethod?.DeclaredAccessibility == Accessibility.Public
                     && p.SetMethod?.DeclaredAccessibility == Accessibility.Public);

}
