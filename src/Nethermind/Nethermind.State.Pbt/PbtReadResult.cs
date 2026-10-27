// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Pbt;

/// <summary>The caller's variable a span action decodes a store value into.</summary>
public readonly ref struct PbtReadResult<T>
{
    public readonly ref T Value;

    public PbtReadResult(ref T value) => Value = ref value;
}
