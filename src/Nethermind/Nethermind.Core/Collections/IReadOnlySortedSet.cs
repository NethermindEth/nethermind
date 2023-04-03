// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Core.Collections;

public interface IReadOnlySortedSet<T> : IReadOnlySet<T>
{
    public T? Max { get; }
    public T? Min { get; }
}
