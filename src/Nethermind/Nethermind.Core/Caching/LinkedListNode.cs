// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Core.Caching;

internal sealed class LinkedListNode<T>
{
    internal LinkedListNode<T>? Next;
    internal LinkedListNode<T>? Prev;
    internal T Value;

    public LinkedListNode(T value)
    {
        Value = value;
    }
}
