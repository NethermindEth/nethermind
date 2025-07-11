// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Core.Container;

public class OrderedComponents<T>
{
    private IList<T> _components = new List<T>();
    public IEnumerable<T> Components => _components;

    public void AddLast(T item)
    {
        _components.Add(item);
    }

    public void AddFirst(T item)
    {
        _components.Insert(0, item);
    }
}
