// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

internal static class SszCSharp
{
    internal static string Join(params string[] statements) =>
        string.Join("\n", statements.Where(statement => statement.Length != 0));

    internal static string Indent(string source, int depth = 1)
    {
        string prefix = new(' ', depth * 4);
        return string.Join("\n", source.Split('\n').Select(line => line.Length == 0 ? line : prefix + line));
    }

    internal static string Member(SszMember member, string owner = "container") => $"{owner}.@{member.Name}";

    internal static string Span(CollectionBinding binding, string expression) => binding.Storage switch
    {
        CollectionStorage.Span or CollectionStorage.ReadOnlySpan => expression,
        CollectionStorage.Memory or CollectionStorage.ReadOnlyMemory => $"{expression}.Span",
        CollectionStorage.Array => $"{expression}.AsSpan()",
        CollectionStorage.List => $"global::System.Runtime.InteropServices.CollectionsMarshal.AsSpan({expression})",
        CollectionStorage.CustomSpan => $"{expression}{(binding.IsNullable ? "!" : string.Empty)}.AsSpan()",
        _ => throw new InvalidOperationException($"Unsupported collection storage {binding.Storage}."),
    };

    internal static string ConverterSpan(CollectionBinding binding, string expression)
    {
        string span = Span(binding, expression);
        return binding.Element.Binding is ScalarBinding { EnumUnderlyingType: not null } element
            ? $"global::System.Runtime.InteropServices.MemoryMarshal.Cast<{element.TypeName}, {element.EnumUnderlyingType}>({span})"
            : span;
    }

    internal static string Scalar(ScalarBinding binding, string expression) =>
        binding.EnumUnderlyingType is null ? expression : $"({binding.EnumUnderlyingType}){expression}";

    internal static string Construct(CollectionBinding binding, string array) => binding.Storage switch
    {
        CollectionStorage.Array or CollectionStorage.Span or CollectionStorage.ReadOnlySpan
            or CollectionStorage.Memory or CollectionStorage.ReadOnlyMemory => array,
        CollectionStorage.List or CollectionStorage.CustomSpan => $"new {binding.TypeName}({array})",
        _ => throw new InvalidOperationException($"Unsupported collection storage {binding.Storage}."),
    };

    internal static string UnionSwitch(SszCodecDefinition definition, Func<SszMember, string> body, string fallback)
    {
        string selectorType = definition.Selector!.Value.Binding.TypeName;
        string cases = string.Join("\n", definition.Members.Select(member =>
            $"case ({selectorType}){member.SelectorValue}:\n{Indent(body(member))}"));
        return $"switch ({Member(definition.Selector)})\n{{\n{Indent(cases)}\n    default:\n{Indent(fallback, 2)}\n}}";
    }
}
