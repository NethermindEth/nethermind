using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Mining
{
    public class EthashIntCache : IEthashDataSet<uint[]>
    {
        internal uint[][] Data { get; }
        
        public uint Size => (uint)(Data.Length * Ethash.HashBytes);
        
        public EthashIntCache(uint cacheSize, byte[] seed)
        {
            uint cachePageCount = cacheSize / Ethash.HashBytes;
            Data = new uint[cachePageCount][];
            Data[0] = Keccak512.ComputeToUInts(seed);
            for (uint i = 1; i < cachePageCount; i++)
            {
                Data[i] = Keccak512.ComputeUIntsToUInts(Data[i - 1]);
            }

            // http://www.hashcash.org/papers/memohash.pdf
            // RandMemoHash
            for (int _ = 0; _ < Ethash.CacheRounds; _++)
            {
                for (int i = 0; i < cachePageCount; i++)
                {
                    uint v = Data[i][0] % cachePageCount;
                    byte[] left = new byte[Ethash.HashBytes];
                    byte[] right = new byte[Ethash.HashBytes];
                    Buffer.BlockCopy(Data[(i - 1 + cachePageCount) % cachePageCount], 0, left, 0, (int)Ethash.HashBytes);
                    Buffer.BlockCopy(Data[v], 0, right, 0, (int)Ethash.HashBytes);
                    Data[i] = Keccak512.ComputeToUInts(left.Xor(right));
                }
            }
        }

        public uint[] CalcDataSetItem(uint i)
        {
            uint n = (uint)Data.Length;
            uint r = Ethash.HashBytes / Ethash.WordBytes;
            
            uint[] mix = (uint[])Data[i % n].Clone();
            mix[0] = i ^ mix[0];
            mix = Keccak512.ComputeUIntsToUInts(mix);

            for (uint j = 0; j < Ethash.DatasetParents; j++)
            {
                uint cacheIndex = Ethash.Fnv(i ^ j, mix[j % r]);
                Ethash.Fnv(mix, Data[cacheIndex % n]);
            }

            return Keccak512.ComputeUIntsToUInts(mix);
        }
    }
}