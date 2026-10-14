// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using static SszCSharp;

internal static class SszOwnershipEmitter
{
    internal static string Release(SszCodecDefinition definition, string expression)
    {
        if (!definition.HasOwnedValues) return string.Empty;
        int index = 0;
        string body = ReleaseMembers(definition, expression, ref index);
        return definition.Declaration.IsStruct ? body : $"if ({expression} is not null)\n{{\n{Indent(body)}\n}}";
    }

    internal static string ReleaseElements(CollectionBinding collection, string expression)
    {
        if (!collection.Element.Binding.HasOwnedValues) return string.Empty;
        int index = 0;
        string body = ReleaseValue(collection.Element, "__ownedItem", ref index);
        return $"foreach (var __ownedItem in {expression})\n{{\n{Indent(body)}\n}}";
    }

    internal static string ReleaseValue(SszValue value, string expression)
    {
        int index = 0;
        return ReleaseValue(value, expression, ref index);
    }

    private static string ReleaseMembers(SszCodecDefinition definition, string expression, ref int index)
    {
        List<string> statements = [];
        foreach (SszMember member in definition.Members)
        {
            if (!member.Value.Binding.HasOwnedValues) continue;
            string local = $"__owned{index++}";
            statements.Add($"var {local} = {Member(member, expression)};");
            statements.Add(ReleaseValue(member.Value, local, ref index));
        }
        return string.Join("\n", statements);
    }

    private static string ReleaseValue(SszValue value, string expression, ref int index)
    {
        if (!value.Binding.HasOwnedValues) return string.Empty;
        string body;
        if (value.Binding is CollectionBinding collection)
        {
            List<string> statements = [];
            if (collection.Element.Binding.HasOwnedValues && !collection.OwnsElements)
            {
                string item = $"__ownedItem{index++}";
                string release = ReleaseValue(collection.Element, item, ref index);
                statements.Add($"foreach (var {item} in {Span(collection, expression)})\n{{\n{Indent(release)}\n}}");
            }
            if (collection.RequiresDispose)
                statements.Add(collection.Disposal == CollectionDisposal.DisposableInterface
                    ? $"((global::System.IDisposable){expression}).Dispose();"
                    : $"{expression}.Dispose();");
            body = string.Join("\n", statements);
        }
        else if (value.Binding is ScalarBinding { CodecKind: CodecKind.Parameter })
        {
            body = $"global::Nethermind.Serialization.Ssz.Ssz.ReleaseDecoded({expression});";
        }
        else
        {
            body = ReleaseMembers(((ScalarBinding)value.Binding).Definition!, expression, ref index);
        }
        return value.Binding.IsValueType ? body : $"if ({expression} is not null)\n{{\n{Indent(body)}\n}}";
    }
}
