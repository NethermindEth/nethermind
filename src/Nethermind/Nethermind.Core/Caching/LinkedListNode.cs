// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Core.Caching;

[DebuggerDisplay("{Value} (AccessCount)")]
internal sealed class LinkedListNode<T>
{
    internal LinkedListNode<T>? Next;
    internal LinkedListNode<T>? Prev;
    internal T Value;
    internal ulong AccessCount;

    public LinkedListNode(T value)
    {
        Value = value;
        AccessCount = 1;
    }

    public static void MoveToMostRecent(ref LinkedListNode<T>? singleAccessLru, [NotNull] ref LinkedListNode<T>? multiAccessLru, LinkedListNode<T> node)
    {
        ulong accessCount = node.AccessCount;

        Remove(ref accessCount == 1 ? ref singleAccessLru : ref multiAccessLru, node);
        AddMostRecent(ref multiAccessLru, node);

        node.AccessCount = accessCount + 1;
    }

    public static void Remove(ref LinkedListNode<T>? leastRecentlyUsed, LinkedListNode<T> node)
    {
        if (node.Next == node)
        {
            leastRecentlyUsed = null;
        }
        else
        {
            node.Next!.Prev = node.Prev;
            node.Prev!.Next = node.Next;
            if (ReferenceEquals(leastRecentlyUsed, node))
            {
                leastRecentlyUsed = node.Next;
            }
        }
    }

    public static void AddMostRecent([NotNull] ref LinkedListNode<T>? leastRecentlyUsed, LinkedListNode<T> node)
    {
        if (leastRecentlyUsed is null)
        {
            SetFirst(ref leastRecentlyUsed, node);
        }
        else
        {
            InsertMostRecent(leastRecentlyUsed, node);
        }
    }

    private static void InsertMostRecent(LinkedListNode<T> leastRecentlyUsed, LinkedListNode<T> newNode)
    {
        newNode.Next = leastRecentlyUsed;
        newNode.Prev = leastRecentlyUsed.Prev;
        leastRecentlyUsed.Prev!.Next = newNode;
        leastRecentlyUsed.Prev = newNode;
    }

    private static void SetFirst([NotNull] ref LinkedListNode<T>? leastRecentlyUsed, LinkedListNode<T> newNode)
    {
        newNode.Next = newNode;
        newNode.Prev = newNode;
        leastRecentlyUsed = newNode;
    }
}
