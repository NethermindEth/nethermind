﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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
using Nethermind.Crypto;

namespace Nethermind.Consensus.Ethash
{
    public class EthashCache : IEthashDataSet
    {
        private uint[][] Data { get; set; }

        public uint Size { get; set; }

        public EthashCache(uint cacheSize, byte[] seed)
        {
            uint cachePageCount = cacheSize / Ethash.HashBytes;
            Size = cachePageCount * Ethash.HashBytes;
                
            Data = ArrayPool<uint[]>.Shared.Rent((int)cachePageCount);
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
            uint n = Size / Ethash.HashBytes;
            int r = Ethash.HashBytes / Ethash.WordBytes;

            uint[] mixInts = new uint[Ethash.HashBytes / Ethash.WordBytes];
            Buffer.BlockCopy(Data[i % n], 0, mixInts, 0, Ethash.HashBytes);

            mixInts[0] = i ^ mixInts[0];
            mixInts = Keccak512.ComputeUIntsToUInts(mixInts);

            for (uint j = 0; j < Ethash.DataSetParents; j++)
            {
                ulong cacheIndex = Ethash.Fnv(i ^ j, mixInts[j % r]);
                Ethash.Fnv(mixInts, Data[cacheIndex % n]);
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
            
            ArrayPool<uint[]>.Shared.Return(Data);
        }

        ~EthashCache()
        {
            Dispose(false);
        }
    }
}