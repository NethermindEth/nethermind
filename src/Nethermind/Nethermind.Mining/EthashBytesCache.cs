using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Mining
{
    public class EthashBytesCache : IEthashDataSet<byte[]>
    {
        internal byte[][] Data { get; set; }

        public uint Size => (uint)(Data.Length * Ethash.HashBytes);

        public EthashBytesCache(uint cacheSize, byte[] seed)
        {
            uint cachePageCount = cacheSize / Ethash.HashBytes;
            Data = new byte[cachePageCount][];
            Data[0] = Keccak512.Compute(seed).Bytes;
            for (uint i = 1; i < cachePageCount; i++)
            {
                Data[i] = Keccak512.Compute(Data[i - 1]).Bytes;
            }

            // http://www.hashcash.org/papers/memohash.pdf
            // RandMemoHash
            for (int _ = 0; _ < Ethash.CacheRounds; _++)
            {
                for (int i = 0; i < cachePageCount; i++)
                {
                    uint v = Ethash.GetUInt(Data[i], 0) % cachePageCount;
                    Data[i] = Keccak512.Compute(Data[(i - 1 + cachePageCount) % cachePageCount].Xor(Data[v])).Bytes;
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

            uint[] dataInts = new uint[mixInts.Length];
            for (uint j = 0; j < Ethash.DatasetParents; j++)
            {
                ulong cacheIndex = Ethash.Fnv(i ^ j, mixInts[j % r]);
                Buffer.BlockCopy(Data[cacheIndex % n], 0, dataInts, 0, (int)Ethash.HashBytes);
                Ethash.Fnv(mixInts, dataInts);
            }

            mixInts = Keccak512.ComputeUIntsToUInts(mixInts);
            return mixInts;
            //byte[] mix = new byte[Ethash.HashBytes];
            //Buffer.BlockCopy(mixInts, 0, mix, 0, (int)Ethash.HashBytes);
            //return mix;
        }
    }
}