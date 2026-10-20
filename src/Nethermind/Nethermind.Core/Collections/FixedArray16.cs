// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Core.Collections;

/// <summary>
/// A fixed array of 16 items stored inline, with no heap allocation of its own.
/// </summary>
/// <typeparam name="T">The item type.</typeparam>
[InlineArray(16)]
public struct FixedArray16<T>
{
    private T _element0;
}
