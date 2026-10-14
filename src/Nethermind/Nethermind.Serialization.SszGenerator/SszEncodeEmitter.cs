// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using static SszCSharp;

internal static class SszEncodeEmitter
{
    internal static int CachedLengthCount(SszCodecDefinition definition)
    {
        if (definition.Schema is not ContainerSchema) return 0;
        SszMember[] variables = definition.Members.Where(member => member.Value.Schema.FixedSize is null).ToArray();
        bool repeatsTraversal = variables.Take(variables.Length - 1).Any(member => member.Value.Binding switch
        {
            ScalarBinding => true,
            CollectionBinding { Element.Binding: ScalarBinding { CodecKind: not CodecKind.Converter } } => true,
            _ => false,
        });
        return repeatsTraversal ? variables.Length : 0;
    }

    internal static string Measure(SszCodecDefinition definition, bool cacheLengths = false)
    {
        if (definition.WrappedMember is { } wrapped)
            return Join(Validate(definition, wrapped), $"return checked({Length(wrapped)});");

        if (definition.Schema is UnionSchema)
            return UnionSwitch(definition, member => Join(Validate(definition, member), $"return checked(1 + {Length(member)});"),
                $"return ThrowInvalidData<int>(nameof({definition.Declaration.TypeName}), $\"unsupported union selector {{(byte){Member(definition.Selector!)}}}.\");");

        ContainerSchema schema = (ContainerSchema)definition.Schema;
        if (cacheLengths)
        {
            SszMember[] variables = definition.Members.Where(member => member.Value.Schema.FixedSize is null).ToArray();
            return Join(Validate(definition),
                string.Join("\n", variables.Select((member, index) => $"__lengths[{index}] = checked({Length(member)});")),
                $"return checked({schema.FixedSectionSize} + {string.Join(" + ", variables.Select((_, index) => $"__lengths[{index}]"))});");
        }
        string length = string.Join(" + ", new[] { schema.FixedSectionSize.ToString() }
            .Concat(definition.Members.Where(member => member.Value.Schema.FixedSize is null).Select(member => Length(member))));
        return Join(Validate(definition), $"return checked({length});");
    }

    internal static string Encode(SszCodecDefinition definition)
    {
        if (definition.WrappedMember is { } wrapped) return Value(wrapped, "data", Member(wrapped));
        if (definition.Schema is UnionSchema)
            return Join($"data[0] = (byte){Member(definition.Selector!)};",
                UnionSwitch(definition, member => Value(member, "data.Slice(1)", Member(member)) + " break;",
                    $"ThrowInvalidData(nameof({definition.Declaration.TypeName}), \"unsupported union selector.\"); break;"));

        ContainerSchema schema = (ContainerSchema)definition.Schema;
        SszMember[] variables = definition.Members.Where(member => member.Value.Schema.FixedSize is null).ToArray();
        List<string> lines = [];
        for (int i = 0; i < variables.Length; i++)
        {
            string offset = schema.FixedSectionSize.ToString();
            if (i > 0)
            {
                string previousLength = CachedLengthCount(definition) == 0 ? Length(variables[i - 1]) : $"__lengths[{i - 1}]";
                offset = $"checked(__offset{i} + {previousLength})";
            }
            lines.Add($"int __offset{i + 1} = {offset};");
        }
        int variableIndex = 0;
        for (int i = 0; i < definition.Members.Length; i++)
        {
            SszMember member = definition.Members[i];
            int fixedOffset = schema.Fields[i].FixedOffset;
            int slotLength = member.Value.Schema.FixedSize ?? 4;
            lines.Add(member.Value.Schema.FixedSize is null
                ? $"EncodeOffset(data.Slice({fixedOffset}, 4), __offset{++variableIndex});"
                : Value(member, $"data.Slice({fixedOffset}, {slotLength})", Member(member)));
        }
        for (int i = 0; i < variables.Length; i++)
        {
            string end = i + 1 == variables.Length ? "data.Length" : $"__offset{i + 2}";
            lines.Add(Value(variables[i], $"data.Slice(__offset{i + 1}, {end} - __offset{i + 1})", Member(variables[i])));
        }
        return string.Join("\n", lines);
    }

    internal static string Validate(SszCodecDefinition definition) =>
        string.Join("\n", definition.Members.Select(member => Validate(definition, member)).Where(line => line.Length != 0));

    internal static string Validate(SszCodecDefinition definition, SszMember member)
    {
        string expression = Member(member);
        string owner = $"nameof({definition.Declaration.TypeName}), nameof(@{member.Name})";
        string validation = member.Value switch
        {
            { Schema: BitSequenceSchema { Kind: SequenceKind.Vector } bits } => $"ValidateBitvectorLength({expression}, {bits.Bound}, {owner});",
            { Schema: BitSequenceSchema { Kind: SequenceKind.List } bits } => $"ValidateBitlistLimit({expression}, {bits.Bound}UL, {owner});",
            { Schema: SequenceSchema { Kind: SequenceKind.Vector } sequence, Binding: CollectionBinding collection } =>
                $"ValidateVectorLength({Span(collection, expression)}, {sequence.Bound}, {owner});",
            { Schema: SequenceSchema { Kind: SequenceKind.List } sequence, Binding: CollectionBinding collection } =>
                $"ValidateListLimit({Span(collection, expression)}, {sequence.Bound}UL, {owner});",
            _ => string.Empty,
        };
        if (member.Value.Binding.IsValueType) return validation;
        if (member.NullMapping == NullMapping.Reject)
            return Join($"if ({expression} is null) ThrowInvalidValue({owner}, \"null is not valid for this SSZ field.\");", validation);
        return validation.Length == 0 ? string.Empty : $"if ({expression} is not null) {validation}";
    }

    internal static string Length(SszMember member, string? expression = null)
    {
        expression ??= Member(member);
        if (member.Value.Schema.FixedSize is { } fixedSize) return fixedSize.ToString();
        string length = member.Value switch
        {
            { Schema: BitSequenceSchema } => $"({expression} is null ? 1 : {expression}.Length / 8 + 1)",
            { Binding: CollectionBinding { Element.Binding: ScalarBinding { CodecKind: CodecKind.Converter } } collection } =>
                $"({collection.Element.Schema.FixedSize} * {Span(collection, expression)}.Length)",
            { Binding: CollectionBinding collection } =>
                $"{((ScalarBinding)collection.Element.Binding).CodecType}.GetLength({Span(collection, expression)})",
            { Binding: ScalarBinding scalar } => $"{scalar.CodecType}.GetLength({expression})",
            _ => throw new InvalidOperationException("No length operation for this binding."),
        };
        return !member.Value.Binding.IsValueType && member.NullMapping == NullMapping.EmptyCollection && member.Value.Schema is not BitSequenceSchema
            ? $"({expression} is null ? 0 : {length})" : length;
    }

    internal static string Value(SszMember member, string target, string expression)
    {
        string encode = member.Value switch
        {
            { Binding: ScalarBinding { CodecKind: CodecKind.Converter } scalar } =>
                $"{scalar.CodecType}.ToSpan({target}, {Scalar(scalar, expression)});",
            { Binding: ScalarBinding scalar } => $"{scalar.CodecType}.Encode({target}, {expression});",
            { Binding: CollectionBinding { Element.Binding: ScalarBinding { CodecKind: CodecKind.Converter } scalar } collection } =>
                $"{scalar.CodecType}.ToSpan({target}, {ConverterSpan(collection, expression)});",
            { Binding: CollectionBinding collection } =>
                $"{((ScalarBinding)collection.Element.Binding).CodecType}.Encode({target}, {Span(collection, expression)});",
            { Schema: BitSequenceSchema { Kind: SequenceKind.Vector } } =>
                $"global::Nethermind.Serialization.Ssz.Ssz.Encode({target}, {expression});",
            { Schema: BitSequenceSchema bits } =>
                $"global::Nethermind.Serialization.Ssz.Ssz.Encode({target}, {expression} ?? new global::System.Collections.BitArray(0), {bits.Bound ?? 0} );",
            _ => throw new InvalidOperationException("No encode operation for this binding."),
        };
        if (member.Value.Binding.IsValueType || member.Value.Schema is BitSequenceSchema { Kind: not SequenceKind.Vector })
            return encode;
        return member.NullMapping switch
        {
            NullMapping.ZeroEncoded => $"if ({expression} is null) {target}.Clear(); else {encode}",
            NullMapping.EmptyCollection => $"if ({expression} is not null) {encode}",
            _ => encode,
        };
    }
}
