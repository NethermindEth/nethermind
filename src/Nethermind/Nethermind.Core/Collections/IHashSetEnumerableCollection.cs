// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Core.Collections;

public interface IHashSetEnumerableCollection<T> : IReadOnlyCollection<T>
{
    new HashSet<T>.Enumerator GetEnumerator();
}
