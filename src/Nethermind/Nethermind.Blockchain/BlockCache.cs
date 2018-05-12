using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain
{
    /// <summary>
    /// https://stackoverflow.com/questions/754233/is-it-there-any-lru-implementation-of-idictionary
    /// </summary>
    internal class BlockCache // TODO: all copy pasted while testing cache approaches, use generics in the end most likely
    {
        private readonly int _capacity;
        private readonly Dictionary<Keccak, LinkedListNode<LruCacheItem>> _cacheMap = new Dictionary<Keccak, LinkedListNode<LruCacheItem>>();
        private readonly LinkedList<LruCacheItem> _lruList = new LinkedList<LruCacheItem>();

        public BlockCache(int capacity)
        {
            _capacity = capacity;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public Block Get(Keccak key)
        {
            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem> node))
            {
                Block value = node.Value.Value;
                _lruList.Remove(node);
                _lruList.AddLast(node);
                return value;
            }

            return default(Block);
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void Set(Keccak key, Block val)
        {
            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem> node))
            {
                node.Value.Value = val;
                _lruList.Remove(node);
                _lruList.AddLast(node);
            }
            else
            {
                if (_cacheMap.Count >= _capacity)
                {
                    RemoveFirst();
                }

                LruCacheItem cacheItem = new LruCacheItem(key, val);
                LinkedListNode<LruCacheItem> newNode = new LinkedListNode<LruCacheItem>(cacheItem);
                _lruList.AddLast(newNode);
                _cacheMap.Add(key, newNode);
            }
        }

        private void RemoveFirst()
        {
            LinkedListNode<LruCacheItem> node = _lruList.First;
            _lruList.RemoveFirst();

            _cacheMap.Remove(node.Value.Key);
        }

        private class LruCacheItem
        {
            public LruCacheItem(Keccak k, Block v)
            {
                Key = k;
                Value = v;
            }

            public readonly Keccak Key;
            public Block Value;
        }
    }
}