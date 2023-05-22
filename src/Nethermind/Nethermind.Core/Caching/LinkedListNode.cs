// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Core.Caching;

[DebuggerDisplay("{Value} ({AccessCount})")]
internal sealed class LinkedListNode<T>
{
    internal LinkedListNode<T>? Next;
    internal LinkedListNode<T>? Prev;
    internal T Value;
    internal uint AccessCount { get; private set; }
    internal uint LastAccessSec { get; private set; }

    public LinkedListNode(T value)
    {
        Value = value;
        ResetAccessCount();
    }

    public void ResetAccessCount()
    {
        AccessCount = 1;
        ResetAccessTime();
    }
    public void ResetAccessTime()
    {
        // Max ~133 years since process restart
        LastAccessSec = (uint)(Environment.TickCount64 / 1024);
    }

    public static void MoveToMostRecent(ref LinkedListNode<T>? singleAccessLru, [NotNull] ref LinkedListNode<T>? multiAccessLru, LinkedListNode<T> node)
    {
        uint accessCount = node.AccessCount;

        Remove(ref accessCount == 1 ? ref singleAccessLru : ref multiAccessLru, node);
        AddMostRecent(ref multiAccessLru, node);

        if (accessCount < uint.MaxValue)
        {
            node.AccessCount = accessCount + 1;
        }

        node.ResetAccessTime();
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

        node.LastAccessSec = 0;
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

        node.ResetAccessTime();
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
