// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

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

    public static void MoveToMostRecent([NotNull] ref LinkedListNode<T>? leastRecentlyUsed, LinkedListNode<T> node)
    {
        if (node.Next == node)
        {
            if (leastRecentlyUsed != node)
            {
                InvalidNotSingleNodeList();
            }
            // Do nothing only one node
        }
        else
        {
            Remove(ref leastRecentlyUsed, node);
            AddMostRecent(ref leastRecentlyUsed, node);
        }
    }

    public static void Remove(ref LinkedListNode<T>? leastRecentlyUsed, LinkedListNode<T> node)
    {
        if (leastRecentlyUsed is null)
        {
            InvalidRemoveFromEmptyList();
        }
        if (node.Next == node)
        {
            if (leastRecentlyUsed != node)
            {
                InvalidNotSingleNodeList();
            }
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

        [DoesNotReturn, StackTraceHidden]
        static void InvalidRemoveFromEmptyList()
        {
            throw new InvalidOperationException("This method shouldn't be called on empty list");
        }
    }

    [DoesNotReturn, StackTraceHidden]
    static void InvalidNotSingleNodeList()
    {
        throw new InvalidOperationException("This should only be true for a list with only one node");
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
