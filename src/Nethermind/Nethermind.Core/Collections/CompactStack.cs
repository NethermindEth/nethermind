// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.ObjectPool;

namespace Nethermind.Core.Collections;

/// <summary>
/// Its basically a linked list stack, but the node have an array instead of a single item. This is to prevent
/// allocating a large array (as with standard stack), and allow configuring the size of the node so that it
/// will not become a LOH allocation. Additionally, the Pop will release the node, so it can reduce memory usage
/// without an explicit TryTrim. Also allow specifying object pool of node to be shared between multiple stacks.
/// </summary>
/// <typeparam name="T"></typeparam>
public class CompactStack<T>
{
    public class Node
    {
        internal Node? _tail = null;
        internal T[] _array = null!;
        internal int _count = 0;

        public Node(int nodeSize)
        {
            _array = new T[nodeSize];
        }
    }

    public class ObjectPoolPolicy : IPooledObjectPolicy<Node>
    {
        private readonly int _nodeSize;

        public ObjectPoolPolicy(int nodeSize)
        {
            _nodeSize = nodeSize;
        }

        public Node Create()
        {
            return new Node(_nodeSize);
        }

        public bool Return(Node obj)
        {
            obj._count = 0;
            obj._tail = null;
            return true;
        }
    }

    private ObjectPool<Node> _nodePool;
    private Node? _head = null;

    public CompactStack(ObjectPool<Node>? nodePool = null)
    {
        _nodePool = nodePool ?? new DefaultObjectPool<Node>(new ObjectPoolPolicy(64), 1);
    }

    public bool IsEmpty => _head == null;

    public void Push(T item)
    {
        _head ??= _nodePool.Get();

        if (_head._count == _head._array.Length)
        {
            Node newNode = _nodePool.Get();
            newNode._tail = _head;
            _head = newNode;
        }

        _head._array[_head._count] = item;
        _head._count++;
    }

    public bool TryPop(out T? item)
    {
        if (_head == null)
        {
            item = default;
            return false;
        }

        item = _head._array[_head._count - 1];
        _head._array[_head._count - 1] = default!;
        _head._count--;
        if (_head._count == 0)
        {
            Node? oldHead = _head;
            _head = oldHead._tail;
            if (oldHead != null)
            {
                _nodePool.Return(oldHead);
            }
        }
        return true;
    }
}
