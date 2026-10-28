// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.FluentRlp;

/// <summary>Declares that a value is always encoded as a byte string of exactly <paramref name="length"/> bytes.</summary>
/// <remarks>
/// Put it on a type with a hand-written converter, or on a parameter of an <see cref="RlpSerializableAttribute"/>
/// record whose type cannot carry it. The generated converter then computes the payload length of a record or a list
/// of such values without a pass over them. A write that does not match the length throws
/// <see cref="InvalidOperationException"/>.
/// </remarks>
/// <param name="length">The number of bytes of the byte string, at least 2.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Parameter, Inherited = false)]
public sealed class RlpFixedLengthAttribute(int length) : Attribute
{
    /// <summary>Gets the number of bytes of the byte string.</summary>
    public int Length { get; } = length;
}
