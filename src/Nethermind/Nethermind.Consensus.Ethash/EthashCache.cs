//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Crypto;

namespace Nethermind.Consensus.Ethash
{
    public class EthashCache : IEthashDataSet
    {
        private struct Bucket
        {
            public uint Item0;
            public uint Item1;
            public uint Item2;
            public uint Item3;
            public uint Item4;
            public uint Item5;
            public uint Item6;
            public uint Item7;
            public uint Item8;
            public uint Item9;
            public uint Item10;
            public uint Item11;
            public uint Item12;
            public uint Item13;
            public uint Item14;
            public uint Item15;

            public Span<uint> AsUInts()
            {
                return MemoryMarshal.Cast<Bucket, uint>(MemoryMarshal.CreateSpan(ref this, 1));
            }
        }

        private ArrayPool<Bucket> _arrayPool = ArrayPool<Bucket>.Create(1024 * 1024 * 4, 50);

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
                Span<Bucket> bucket = MemoryMarshal.CreateSpan(ref Data[i - 1], 1);
                Span<Bucket> targetBucket = MemoryMarshal.CreateSpan(ref Data[i], 1);
                Keccak512.ComputeUIntsToUInts(
                    MemoryMarshal.Cast<Bucket, uint>(bucket),
                    MemoryMarshal.Cast<Bucket, uint>(targetBucket));
            }

            // http://www.hashcash.org/papers/memohash.pdf
            // RandMemoHash
            for (int _ = 0; _ < Ethash.CacheRounds; _++)
            {
                for (int i = 0; i < cachePageCount; i++)
                {
                    uint v = Data[i].Item0 % cachePageCount;
                    long page = (i - 1 + cachePageCount) % cachePageCount;
                    Data[i].Item0 = Data[page].Item0 ^ Data[v].Item0;
                    Data[i].Item1 = Data[page].Item1 ^ Data[v].Item1;
                    Data[i].Item2 = Data[page].Item2 ^ Data[v].Item2;
                    Data[i].Item3 = Data[page].Item3 ^ Data[v].Item3;
                    Data[i].Item4 = Data[page].Item4 ^ Data[v].Item4;
                    Data[i].Item5 = Data[page].Item5 ^ Data[v].Item5;
                    Data[i].Item6 = Data[page].Item6 ^ Data[v].Item6;
                    Data[i].Item7 = Data[page].Item7 ^ Data[v].Item7;
                    Data[i].Item8 = Data[page].Item8 ^ Data[v].Item8;
                    Data[i].Item9 = Data[page].Item9 ^ Data[v].Item9;
                    Data[i].Item10 = Data[page].Item10 ^ Data[v].Item10;
                    Data[i].Item11 = Data[page].Item11 ^ Data[v].Item11;
                    Data[i].Item12 = Data[page].Item12 ^ Data[v].Item12;
                    Data[i].Item13 = Data[page].Item13 ^ Data[v].Item13;
                    Data[i].Item14 = Data[page].Item14 ^ Data[v].Item14;
                    Data[i].Item15 = Data[page].Item15 ^ Data[v].Item15;

                    Span<Bucket> bucket = MemoryMarshal.CreateSpan(ref Data[i], 1);
                    Keccak512.ComputeUIntsToUInts(MemoryMarshal.Cast<Bucket, uint>(bucket), MemoryMarshal.Cast<Bucket, uint>(bucket));
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