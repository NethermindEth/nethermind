// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Collections;

public interface ISpanEqualityComparer<T>
{
    bool Equals(ReadOnlySpan<T> x, ReadOnlySpan<T> y);
    int GetHashCode(ReadOnlySpan<T> obj);
}


