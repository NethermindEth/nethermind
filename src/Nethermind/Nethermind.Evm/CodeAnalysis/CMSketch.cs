
using System;
using System.Collections.Concurrent;

namespace Nethermind.Evm.CodeAnalysis
{

    public class CMSketch
    {
        private ConcurrentDictionary<(int Row, int Column), uint> _sketch;
        private int[] _seeds;
        private int _buckets;
        private int _numberOfhashFunctions;

        public CMSketch(int numberOfhashFunctions, int buckets)
        {
            _sketch = new ConcurrentDictionary<(int, int), uint>();
            _buckets = buckets;
            _numberOfhashFunctions = numberOfhashFunctions;
            _seeds = new int[numberOfhashFunctions];
            Random rand = new Random();
            for (int i = 0; i < numberOfhashFunctions; i++)
            {
                _seeds[i] = rand.Next(int.MinValue, int.MaxValue);
            }
        }

        public void Update(ulong item)
        {
            for (int hasher = 0; hasher < _numberOfhashFunctions; hasher++)
            {
                _sketch.AddOrUpdate((hasher, ComputeHash(item, hasher, _seeds, _numberOfhashFunctions) % _buckets),
                             1,
                         (key, value) => value + 1
                        );
            }
        }

        public uint Query(ulong item)
        {
            var minCount = uint.MaxValue;
            for (int hasher = 0; hasher < _numberOfhashFunctions; hasher++)
            {
                _sketch.TryGetValue((hasher, ComputeHash(item, hasher, _seeds, _numberOfhashFunctions) % _buckets), out uint count);
                minCount = Math.Min(minCount, count);
            }
            return minCount;
        }


        public uint UpdateAndQuery(ulong item)
        {
            Update(item);
            return Query(item);
        }

        public void Reset()
        {
            _sketch = new ConcurrentDictionary<(int, int), uint>();
        }

        public static int ComputeHash(ulong value, int hasher, int[] _seeds, int _breadth)
        {
            // http://isthe.com/chongo/tech/comp/fnv/#FNV-1a
            // FNV_OFFSET_BASIS_32BIT = 2166136261
            // FNV_PRIME_32BIT  = 16777619
            return (int)(((((((((((((((((((((((2166136261 ^ (byte)(value & 0xFF)) * 16777619)
                  ^ (byte)((value >> 8) & 0xFF)) * 16777619)
                  ^ (byte)((value >> 16) & 0xFF)) * 16777619)
                  ^ (byte)((value >> 24) & 0xFF)) * 16777619)
                  ^ (byte)((value >> 32) & 0xFF)) * 16777619)
                  ^ (byte)((value >> 40) & 0xFF)) * 16777619)
                  ^ (byte)((value >> 48) & 0xFF)) * 16777619)
                  ^ (byte)((value >> 56) & 0xFF)) * 16777619)
          ^ (byte)(_seeds[hasher % _breadth] & 0xFF)) * 16777619)
          ^ (byte)((_seeds[hasher % _breadth] >> 8) & 0xFF)) * 16777619)
          ^ (byte)((_seeds[hasher % _breadth] >> 16) & 0xFF)) * 16777619)
          ^ (byte)((_seeds[hasher % _breadth] >> 24) & 0xFF)) * 16777619;

        }

    }
}
