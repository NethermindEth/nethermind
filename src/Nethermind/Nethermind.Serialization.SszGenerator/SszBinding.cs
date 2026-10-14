// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;

internal enum CodecKind { Generated, Converter, Parameter }
internal enum CollectionStorage { Array, List, Span, ReadOnlySpan, Memory, ReadOnlyMemory, CustomSpan }
internal enum CollectionDisposal { None, DisposeMethod, DisposableInterface }
internal enum NullMapping { Reject, EmptyCollection, ZeroEncoded }

internal sealed record SszValue(SszSchema Schema, ClrBinding Binding);
internal abstract record ClrBinding(string TypeName, bool IsValueType, bool IsNullable, bool HasOwnedValues);
internal sealed record ScalarBinding(
    string TypeName,
    bool IsValueType,
    bool IsNullable,
    CodecKind CodecKind,
    string CodecType,
    string? EnumUnderlyingType,
    SszCodecDefinition? Definition,
    bool HasOwnedValues) : ClrBinding(TypeName, IsValueType, IsNullable, HasOwnedValues);
internal sealed record CollectionBinding(
    string TypeName,
    bool IsValueType,
    bool IsNullable,
    CollectionStorage Storage,
    SszValue Element,
    CollectionDisposal Disposal,
    bool OwnsElements,
    bool HasOwnedValues) : ClrBinding(TypeName, IsValueType, IsNullable, HasOwnedValues)
{
    public bool RequiresDispose => Disposal != CollectionDisposal.None;
}
internal sealed record BitArrayBinding(string TypeName, bool IsNullable) : ClrBinding(TypeName, false, IsNullable, false);
internal sealed record SszMember(
    string Name,
    SszValue Value,
    NullMapping NullMapping,
    int? FieldIndex = null,
    byte? SelectorValue = null);
internal sealed record SszDeclaration(
    string Namespace,
    string TypeName,
    string HintName,
    string Constraints,
    bool IsStruct,
    bool IsRecord);
internal sealed record SszCodecDefinition(
    SszDeclaration Declaration,
    SszSchema Schema,
    ImmutableArray<SszMember> Members,
    SszMember? WrappedMember,
    SszMember? Selector,
    bool HasOwnedValues);
