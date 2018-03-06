using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Mining
{
    public class EthashCache : IEthashDataSet
    {
        internal uint[][] Data { get; set; }

        public uint Size => (uint)(Data.Length * Ethash.HashBytes);

        public EthashCache(uint cacheSize, byte[] seed)
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
                    long page = (i - 1 + cachePageCount) % cachePageCount;
                    for (int j = 0; j < Data[i].Length; j++)
                    {
                        Data[i][j] = Data[page][j] ^ Data[v][j];
                    }

                    Data[i] = Keccak512.ComputeUIntsToUInts(Data[i]);
                }
            }
        }

        public uint[] CalcDataSetItem(uint i)
        {
            uint n = (uint)Data.Length;
            uint r = Ethash.HashBytes / Ethash.WordBytes;

            uint[] mixInts = new uint[Ethash.HashBytes / Ethash.WordBytes];
            Buffer.BlockCopy(Data[i % n], 0, mixInts, 0, (int)Ethash.HashBytes);

            mixInts[0] = i ^ mixInts[0];
            mixInts = Keccak512.ComputeUIntsToUInts(mixInts);

            for (uint j = 0; j < Ethash.DatasetParents; j++)
            {
                ulong cacheIndex = Ethash.Fnv(i ^ j, mixInts[j % r]);
                Ethash.Fnv(mixInts, Data[cacheIndex % n]);
            }

            mixInts = Keccak512.ComputeUIntsToUInts(mixInts);
            return mixInts;
        }
    }
}