//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Buffers;
using System.Runtime.InteropServices;
using Nethermind.Crypto;

namespace Nethermind.Consensus.Ethash
{
    public class EthashCache : IEthashDataSet
    {
        private struct Bucket
        {
            public uint Item0;
            private uint Item1;
            private uint Item2;
            private uint Item3;
            private uint Item4;
            private uint Item5;
            private uint Item6;
            private uint Item7;
            private uint Item8;
            private uint Item9;
            private uint Item10;
            private uint Item11;
            private uint Item12;
            private uint Item13;
            private uint Item14;
            private uint Item15;

            public Span<uint> AsUInts()
            {
                return MemoryMarshal.Cast<Bucket, uint>(MemoryMarshal.CreateSpan(ref this, 1));
            }

            public static Bucket Xor(Bucket a, Bucket b)
            {
                Bucket result = new();
                result.Item0 = a.Item0 ^ b.Item0;
                result.Item1 = a.Item1 ^ b.Item1;
                result.Item2 = a.Item2 ^ b.Item2;
                result.Item3 = a.Item3 ^ b.Item3;
                result.Item4 = a.Item4 ^ b.Item4;
                result.Item5 = a.Item5 ^ b.Item5;
                result.Item6 = a.Item6 ^ b.Item6;
                result.Item7 = a.Item7 ^ b.Item7;
                result.Item8 = a.Item8 ^ b.Item8;
                result.Item9 = a.Item9 ^ b.Item9;
                result.Item10 = a.Item10 ^ b.Item10;
                result.Item11 = a.Item11 ^ b.Item11;
                result.Item12 = a.Item12 ^ b.Item12;
                result.Item13 = a.Item13 ^ b.Item13;
                result.Item14 = a.Item14 ^ b.Item14;
                result.Item15 = a.Item15 ^ b.Item15;
                return result;
            }
        }

        private ArrayPool<Bucket> _arrayPool = ArrayPool<Bucket>.Create(1024 * 1024 * 2, 50);

        private Bucket[] Data { get; set; }

        public uint Size { get; set; }

        public EthashCache(uint cacheSize, byte[] seed)
        {
            uint cachePageCount = cacheSize / Ethash.HashBytes;
            Size = cachePageCount * Ethash.HashBytes;

            Data = _arrayPool.Rent((int) cachePageCount);
            Data[0] = MemoryMarshal.Cast<uint, Bucket>(Keccak512.ComputeToUInts(seed))[0];

            for (uint i = 1; i < cachePageCount; i++)
            {
                Keccak512.ComputeUIntsToUInts(Data[i - 1].AsUInts(), Data[i].AsUInts());
            }

            // http://www.hashcash.org/papers/memohash.pdf
            // RandMemoHash
            for (int _ = 0; _ < Ethash.CacheRounds; _++)
            {
                for (int i = 0; i < cachePageCount; i++)
                {
                    uint v = Data[i].Item0 % cachePageCount;
                    long page = (i - 1 + cachePageCount) % cachePageCount;
                    Data[i] = Bucket.Xor(Data[page], Data[v]);
                    Span<uint> bucketAsUInts = Data[i].AsUInts();
                    Keccak512.ComputeUIntsToUInts(bucketAsUInts, bucketAsUInts);
                }
            }
        }

        public uint[] CalcDataSetItem(uint i)
        {
            uint n = Size / Ethash.HashBytes;
            int r = Ethash.HashBytes / Ethash.WordBytes;

            uint[] mixInts = new uint[Ethash.HashBytes / Ethash.WordBytes];
            Data[i % n].AsUInts().CopyTo(mixInts);

            mixInts[0] = i ^ mixInts[0];
            mixInts = Keccak512.ComputeUIntsToUInts(mixInts);

            for (uint j = 0; j < Ethash.DataSetParents; j++)
            {
                ulong cacheIndex = Ethash.Fnv(i ^ j, mixInts[j % r]);
                Ethash.Fnv(mixInts, MemoryMarshal.Cast<Bucket, uint>(MemoryMarshal.CreateSpan(ref Data[cacheIndex % n], 1)));
            }

            mixInts = Keccak512.ComputeUIntsToUInts(mixInts);
            return mixInts;
        }

        private bool isDisposed;

        public void Dispose()
        {
            Dispose(true);
        }

        private void Dispose(bool isDisposing)
        {
            if (isDisposed)
            {
                return;
            }

            isDisposed = true;

            if (isDisposing)
            {
                GC.SuppressFinalize(this);
            }
            
            _arrayPool.Return(Data);
        }

        ~EthashCache()
        {
            Dispose(false);
        }
    }
}
