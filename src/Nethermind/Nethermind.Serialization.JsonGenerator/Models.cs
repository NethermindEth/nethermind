// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Nethermind.Serialization.JsonGenerator;

/// <summary>Contract kinds; names match <c>Nethermind.Serialization.Json.GeneratedJsonPropertyKind</c>.</summary>
internal enum ContractKind
{
    Written,
    NotWritten,
    Ignored,
}

/// <summary>How a property value can be null, which decides the null and default checks emitted for it.</summary>
internal enum NullKind
{
    Reference,
    NullableValue,
    Value,
}

/// <summary>One entry of the metadata contract, in the order the metadata path lists it.</summary>
internal sealed record PropertyModel(
    string MemberName,
    string? ExplicitName,
    string DeclaringTypeName,
    string TypeName,
    string? ConverterTypeName,
    ContractKind Kind,
    bool Emit,
    string? IgnoreCondition,
    NullKind NullKind,
    bool IsObject);

internal sealed record DiagnosticModel(string Id, string Detail);

/// <summary>A source location as plain values, so cached models do not keep syntax trees alive.</summary>
internal sealed record LocationModel(string FilePath, TextSpan Span, LinePositionSpan LineSpan)
{
    public static LocationModel? From(Location? location) =>
        location is { IsInSource: true } ? new LocationModel(location.SourceTree!.FilePath, location.SourceSpan, location.GetLineSpan().Span) : null;

    public Location ToLocation() => Location.Create(FilePath, Span, LineSpan);
}

internal sealed record TypeModel(
    string FullName,
    string? Namespace,
    string WriterName,
    bool HasOnSerializing,
    bool HasOnSerialized,
    bool RegisterWithSerializer,
    EquatableArray<PropertyModel> Contract,
    EquatableArray<DiagnosticModel> Diagnostics,
    LocationModel? Location)
{
    /// <summary>Unique per type in the compilation, unlike <see cref="WriterName"/>, which repeats across namespaces.</summary>
    public string HintName => FullName.Replace("global::", string.Empty).Replace('<', '_').Replace('>', '_') + "JsonWriter.g.cs";
}
