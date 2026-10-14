// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using static SszCSharp;

internal static class SszDecodeEmitter
{
    internal static string Decode(SszCodecDefinition definition)
    {
        string preflight = string.Empty;
        string body;
        if (definition.WrappedMember is { } wrapped)
            body = Value(definition, wrapped, "data");
        else if (definition.Schema is UnionSchema)
        {
            preflight = $"ValidateMinimumLength(data.Length, 1, nameof({definition.Declaration.TypeName}));";
            body = Join($"{Member(definition.Selector!)} = ({definition.Selector!.Value.Binding.TypeName})data[0];",
                UnionSwitch(definition, member => Value(definition, member, "data.Slice(1)") + " break;",
                    $"ThrowInvalidData(nameof({definition.Declaration.TypeName}), \"unsupported union selector.\"); break;"),
                $"ValidateExactLength(data.Length, GetLength(container), nameof({definition.Declaration.TypeName}));");
        }
        else
        {
            ContainerSchema schema = (ContainerSchema)definition.Schema;
            List<string> checks = [$"{(schema.FixedSize is null ? "ValidateMinimumLength" : "ValidateExactLength")}(data.Length, {schema.FixedSectionSize}, nameof({definition.Declaration.TypeName}));"];
            List<string> fields = [];
            SszMember[] variables = definition.Members.Where(member => member.Value.Schema.FixedSize is null).ToArray();
            int variableIndex = 0;
            for (int i = 0; i < definition.Members.Length; i++)
            {
                SszMember member = definition.Members[i];
                int fixedOffset = schema.Fields[i].FixedOffset;
                int width = member.Value.Schema.FixedSize ?? 4;
                if (member.Value.Schema.FixedSize is null)
                    checks.Add($"int __offset{++variableIndex} = DecodeOffset(data.Slice({fixedOffset}, 4));");
                else
                    fields.Add(Value(definition, member, $"data.Slice({fixedOffset}, {width})"));
            }
            if (variables.Length != 0)
                checks.Add($"ValidateDynamicOffsets(data, {schema.FixedSectionSize}, nameof({definition.Declaration.TypeName}), [{string.Join(", ", Enumerable.Range(1, variables.Length).Select(index => $"__offset{index}"))}]);");
            for (int i = 0; i < variables.Length; i++)
            {
                string end = i + 1 == variables.Length ? "data.Length" : $"__offset{i + 2}";
                fields.Add(Value(definition, variables[i], $"data.Slice(__offset{i + 1}, {end} - __offset{i + 1})"));
            }
            preflight = string.Join("\n", checks);
            body = string.Join("\n", fields);
        }
        string ownedLocals = string.Empty;
        if (definition.HasOwnedValues)
        {
            List<string> locals = [];
            List<string> release = [];
            for (int i = 0; i < definition.Members.Length; i++)
            {
                SszMember member = definition.Members[i];
                if (!member.Value.Binding.HasOwnedValues) continue;
                locals.Add($"{member.Value.Binding.TypeName} __ownedValue{i} = default!;\nbool __hasOwned{i} = false;");
                release.Add($"if (__hasOwned{i})\n{{\n{Indent(SszOwnershipEmitter.ReleaseValue(member.Value, $"__ownedValue{i}"))}\n}}");
            }
            ownedLocals = string.Join("\n", locals);
            body = $"try\n{{\n{Indent(body)}\n}}\ncatch\n{{\n{Indent(string.Join("\n", release))}\n    container = default!;\n    throw;\n}}";
        }
        return Join(preflight, "container = new();", ownedLocals, body);
    }

    private static string Value(SszCodecDefinition definition, SszMember member, string data)
    {
        string target = Member(member);
        if (member.Value.Binding is ScalarBinding scalar)
        {
            if (scalar.HasOwnedValues)
            {
                int index = definition.Members.IndexOf(member);
                return Join($"{scalar.CodecType}.Decode({data}, out __ownedValue{index});",
                    $"__hasOwned{index} = true;", $"{target} = __ownedValue{index};");
            }
            return scalar.CodecKind == CodecKind.Converter
                ? $"{target} = {(scalar.EnumUnderlyingType is null ? string.Empty : $"({scalar.TypeName})")}{scalar.CodecType}.FromSpan({data});"
                : $"{{ {scalar.CodecType}.Decode({data}, out {scalar.TypeName} __value); {target} = __value; }}";
        }
        if (member.Value.Schema is BitSequenceSchema bits)
            return Join("{", bits.Kind == SequenceKind.Vector
                ? $"global::Nethermind.Serialization.Ssz.Ssz.Decode({data}, {bits.Bound}, out global::System.Collections.BitArray __value);"
                : $"DecodeBitList({data}, {bits.Bound ?? ulong.MaxValue}UL, nameof({definition.Declaration.TypeName}), out global::System.Collections.BitArray __value);",
                $"{target} = __value;", "}");

        SequenceSchema schema = (SequenceSchema)member.Value.Schema;
        CollectionBinding collection = (CollectionBinding)member.Value.Binding;
        ScalarBinding element = (ScalarBinding)collection.Element.Binding;
        string typeName = $"{definition.Declaration.TypeName}.{member.Name}";
        string bound = schema.Kind == SequenceKind.Vector ? schema.Bound!.Value.ToString() : "__count";
        string lengthCheck = schema.FixedSize is { } width
            ? $"ValidateExactLength(__data.Length, {width}, \"{typeName}\");"
            : element.CodecKind == CodecKind.Converter || collection.Element.Schema.FixedSize is not null
                ? $"ValidateMultipleOf(__data.Length, {collection.Element.Schema.FixedSize}, \"{typeName}\");"
                : string.Empty;
        string count = collection.Element.Schema.FixedSize is { } itemSize
            ? $"__data.Length / {itemSize}"
            : $"(__data.IsEmpty ? 0 : ValidateVariableItemCount(__data, DecodeOffset(__data), \"{typeName}\"))";
        List<string> lines = ["{", $"global::System.ReadOnlySpan<byte> __data = {data};", lengthCheck, $"int __count = {count};"];
        if (schema.Kind == SequenceKind.List && schema.Bound is <= int.MaxValue)
            lines.Add($"if (__count > {schema.Bound}) ThrowInvalidValue(nameof({definition.Declaration.TypeName}), nameof(@{member.Name}), \"element count exceeds the SSZ limit.\");");
        if (schema.Kind == SequenceKind.Vector && schema.FixedSize is null)
            lines.Add($"if (__count != {schema.Bound}) ThrowInvalidValue(nameof({definition.Declaration.TypeName}), nameof(@{member.Name}), \"incorrect vector element count.\");");

        if (element.CodecKind == CodecKind.Converter)
        {
            bool isList = collection.Storage == CollectionStorage.List;
            lines.Add(isList
                ? $"{collection.TypeName} __items = new({bound});\nglobal::System.Runtime.InteropServices.CollectionsMarshal.SetCount(__items, {bound});"
                : $"{element.TypeName}[] __items = new {element.TypeName}[{bound}];");
            string destination = isList ? "global::System.Runtime.InteropServices.CollectionsMarshal.AsSpan(__items)" : "__items.AsSpan()";
            if (element.EnumUnderlyingType is not null)
                destination = $"global::System.Runtime.InteropServices.MemoryMarshal.Cast<{element.TypeName}, {element.EnumUnderlyingType}>({destination})";
            lines.Add($"{element.CodecType}.FromSpan(__data, {destination});");
            lines.Add(Assign(definition, member, isList ? "__items" : Construct(collection, "__items")));
        }
        else
        {
            lines.Add($"{element.CodecType}.Decode(__data, out {element.TypeName}[] __items);");
            string constructed = Construct(collection, "__items");
            if (collection.Element.Binding.HasOwnedValues && collection.Storage is CollectionStorage.CustomSpan or CollectionStorage.List)
            {
                lines.Add($"{collection.TypeName} __collection;");
                lines.Add($"try\n{{\n    __collection = {constructed};\n}}\ncatch\n{{\n{Indent(SszOwnershipEmitter.ReleaseElements(collection, "__items"))}\n    throw;\n}}");
                constructed = "__collection";
            }
            lines.Add(Assign(definition, member, constructed));
        }
        lines.Add("}");
        return string.Join("\n", lines.Where(line => line.Length != 0));
    }

    private static string Assign(SszCodecDefinition definition, SszMember member, string value)
    {
        if (!member.Value.Binding.HasOwnedValues) return $"{Member(member)} = {value};";
        int index = definition.Members.IndexOf(member);
        return Join($"__ownedValue{index} = {value};", $"__hasOwned{index} = true;", $"{Member(member)} = __ownedValue{index};");
    }
}
