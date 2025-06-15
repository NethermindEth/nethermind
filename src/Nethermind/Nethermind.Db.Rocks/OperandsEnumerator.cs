// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db.Rocks;

public readonly ref struct OperandsEnumerator(MergeOperators.OperandsEnumerator enumerator) : IOperandsEnumerator
{
    private readonly MergeOperators.OperandsEnumerator _enumerator = enumerator;

    public int Count => _enumerator.Count;

    // ReSharper disable once PossiblyImpureMethodCallOnReadonlyVariable
    public ReadOnlySpan<byte> Get(int index) => _enumerator.Get(index);
}
