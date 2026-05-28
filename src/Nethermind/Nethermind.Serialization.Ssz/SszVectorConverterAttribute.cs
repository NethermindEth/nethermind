// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.Ssz;

/// <summary>
/// Marks a type as the fixed-length SSZ vector converter for <typeparamref name="T"/>.
/// </summary>
/// <remarks>
/// Converter types must be public static classes and expose public static
/// <c>FromSpan</c>, <c>ToSpan</c>, and <c>Feed</c> methods, plus a public
/// constant <c>Length</c> member so the SSZ source generator can calculate
/// fixed offsets at generation time. Converters whose items can be SSZ-packed
/// in lists and vectors may expose a public constant <c>PacksItems</c> member
/// with value <see langword="true"/>.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class SszVectorConverterAttribute<T> : Attribute;
