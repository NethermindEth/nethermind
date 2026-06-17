// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.Ssz;

/// <summary>
/// Marks a type as the fixed-length SSZ basic type converter for <typeparamref name="T"/>.
/// </summary>
/// <remarks>
/// Converter types must be public static classes and expose public static
/// <c>FromSpan</c>, <c>ToSpan</c>, and <c>Feed</c> methods, plus a public
/// constant <c>Length</c> member so the SSZ source generator can calculate
/// fixed offsets at generation time. Lists and vectors of this item type use
/// SSZ packed basic collection merkleization.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class SszBasicTypeConverterAttribute<T> : Attribute;

/// <summary>
/// Marks a type as the fixed-length SSZ vector type converter for <typeparamref name="T"/>.
/// </summary>
/// <remarks>
/// Converter types must be public static classes and expose public static
/// <c>FromSpan</c>, <c>ToSpan</c>, and <c>Feed</c> methods, plus a public
/// constant <c>Length</c> member so the SSZ source generator can calculate
/// fixed offsets at generation time. Lists and vectors of this item type use
/// composite collection merkleization over per-item roots.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class SszVectorTypeConverterAttribute<T> : Attribute;
