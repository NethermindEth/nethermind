// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.CodeAnalysis;

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

internal sealed record TypeModel(
    string FullName,
    string? Namespace,
    string WriterName,
    bool HasOnSerializing,
    bool HasOnSerialized,
    EquatableArray<PropertyModel> Contract,
    EquatableArray<DiagnosticModel> Diagnostics,
    Location? Location);
