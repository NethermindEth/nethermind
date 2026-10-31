// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.FluentRlp;

/// <summary>Selects how a record with <see cref="RlpSerializableAttribute"/> is encoded.</summary>
public enum RlpRepresentation : byte
{
    /// <summary>A sequence of the items of the primary constructor parameters, in declaration order.</summary>
    Record = 0,

    /// <summary>The item of the only primary constructor parameter, without a sequence around it.</summary>
    Newtype = 1,
}

/// <summary>Generates an <see cref="IRlpConverter{T}"/> named <c>{Record}RlpConverter</c> for the record.</summary>
/// <remarks>
/// Only the primary constructor parameters are encoded. The generator reports an error for a record that also
/// declares settable properties or instance fields, because their values would be lost.
/// </remarks>
/// <param name="representation">The encoding of the record.</param>
/// <param name="length">
/// The exact number of bytes of a <see cref="RlpRepresentation.Newtype"/> record over a <see cref="byte"/> array,
/// or <c>-1</c> for any length. Reading or writing another length throws.
/// </param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RlpSerializableAttribute(RlpRepresentation representation = RlpRepresentation.Record, int length = -1) : Attribute
{
    /// <summary>Gets the encoding of the record.</summary>
    public RlpRepresentation Representation { get; } = representation;

    /// <summary>Gets the exact payload length, or <c>-1</c> for any length.</summary>
    public int Length { get; } = length;
}
