using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;

namespace Nethermind.Mining
{
    public class Ethash : IEthash
    {
        public Ethash()
        {
            
        }
        
        private readonly ConcurrentDictionary<ulong, byte[][]> _cacheCache = new ConcurrentDictionary<ulong, byte[][]>();

        public const int WordBytes = 4; // bytes in word
        public ulong DatasetBytesInit = (ulong)BigInteger.Pow(2, 30); // bytes in dataset at genesis
        public ulong DatasetBytesGrowth = (ulong)BigInteger.Pow(2, 23); // dataset growth per epoch
        public uint CacheBytesInit = (uint)BigInteger.Pow(2, 24); // bytes in cache at genesis
        public uint CacheBytesGrowth = (uint)BigInteger.Pow(2, 17); // cache growth per epoch
        public const int CacheMultiplier = 1024; // Size of the DAG relative to the cache
        public const ulong EpochLength = 30000; // blocks per epoch
        public const uint MixBytes = 128; // width of mix
        public const uint HashBytes = 64; // hash length in bytes
        public const uint DatasetParents = 256; // number of parents of each dataset element
        public const int CacheRounds = 3; // number of rounds in cache production
        public const int Accesses = 64; // number of accesses in hashimoto loop

        public ulong GetEpoch(BigInteger blockNumber)
        {
            return (ulong)blockNumber / EpochLength;
        }

        public ulong GetDataSize(BigInteger blockNumber)
        {
            ulong size = DatasetBytesInit + DatasetBytesGrowth * GetEpoch(blockNumber);
            size -= MixBytes;
            while (!IsPrime(size / MixBytes))
            {
                size -= 2 * MixBytes;
            }

            return size;
        }

        public uint GetCacheSize(BigInteger blockNumber)
        {
            uint size = CacheBytesInit + CacheBytesGrowth * (uint)GetEpoch(blockNumber);
            size -= HashBytes;
            while (!IsPrime(size / HashBytes))
            {
                size -= 2 * HashBytes;
            }

            return size;
        }

        public static bool IsPrime(ulong number)
        {
            if (number == 1)
            {
                return false;
            }

            if (number == 2 || number == 3)
            {
                return true;
            }

            if (number % 2 == 0 || number % 3 == 0)
            {
                return false;
            }

            ulong w = 2;
            ulong i = 5;
            while (i * i <= number)
            {
                if (number % i == 0)
                {
                    return false;
                }

                i += w;
                w = 6 - w;
            }

            return true;
        }

        public Keccak GetSeedHash(BigInteger blockNumber)
        {
            byte[] seed = new byte[32];
            for (int i = 0; i < blockNumber / EpochLength; i++)
            {
                seed = Keccak.Compute(seed).Bytes; // TODO: optimize
            }

            return new Keccak(seed);
        }

        private readonly BigInteger _2To256 = BigInteger.Pow(2, 256);

        private static readonly Random Random = new Random();

        private static ulong GetRandomNonce()
        {
            byte[] buffer = new byte[8];
            Random.NextBytes(buffer);
            return BitConverter.ToUInt64(buffer, 0);
        }

        private bool IsGreaterThanTarget(byte[] result, byte[] target)
        {
            throw new NotImplementedException();
        }

        public ulong Mine(ulong fullSize, byte[][] dataSet, BlockHeader header, BigInteger difficulty, Func<ulong, byte[][], BlockHeader, ulong, (byte[], byte[])> hashimoto)
        {
            ulong nonce = GetRandomNonce();
            byte[] target = BigInteger.Divide(_2To256, difficulty).ToBigEndianByteArray();
            (byte[] _, byte[] result) = hashimoto(fullSize, dataSet, header, nonce);
            while (IsGreaterThanTarget(result, target))
            {
                unchecked
                {
                    nonce += 1;
                }
            }

            return nonce;
        }

        public ulong MineFull(ulong fullSize, byte[][] dataSet, BlockHeader header, BigInteger difficulty)
        {
            return Mine(fullSize, dataSet, header, difficulty, HashimotoFull);
        }

        public ulong MineLight(ulong fullSize, byte[][] cache, BlockHeader header, BigInteger difficulty)
        {
            return Mine(fullSize, cache, header, difficulty, HashimotoLight);
        }

        public byte[][] BuildDataSet(ulong setSize, byte[][] cache)
        {
            Console.WriteLine($"building data set of length {setSize}"); // TODO: temp, remove
            byte[][] dataSet = new byte[(uint)(setSize / HashBytes)][];
            for (uint i = 0; i < dataSet.Length; i++)
            {
                if (i % 100000 == 0)
                {
                    Console.WriteLine($"building data set of length {setSize}, built {i}"); // TODO: temp, remove
                }

                dataSet[i] = CalcDataSetItem(i, cache);
            }

            return dataSet;
        }

        // TODO: optimize, check, work in progress
        private byte[] CalcDataSetItem(uint i, byte[][] cache)
        {
            uint n = (uint)cache.Length;
            uint r = HashBytes / WordBytes;

            byte[] mix = (byte[])cache[i % n].Clone();
            SetUInt(mix, 0, i ^ GetUInt(mix, 0));
            mix = Keccak512.Compute(mix).Bytes;

            for (uint j = 0; j < DatasetParents; j++)
            {
                ulong cacheIndex = Fnv(i ^ j, GetUInt(mix, j % r));
                mix = Fnv(mix, cache[cacheIndex % n]); // TODO: check
            }

            return Keccak512.Compute(mix).Bytes;
        }

        // TODO: optimize, check, work in progress
        public byte[][] MakeCache(uint cacheSize, byte[] seed)
        {   
            uint cachePageCount = cacheSize / HashBytes;
            byte[][] cache = new byte[cachePageCount][];
            cache[0] = Keccak512.Compute(seed).Bytes;
            for (uint i = 1; i < cachePageCount; i++)
            {
                cache[i] = Keccak512.Compute(cache[i - 1]).Bytes;
            }

            // http://www.hashcash.org/papers/memohash.pdf
            // RandMemoHash
            for (int _ = 0; _ < CacheRounds; _++)
            {
                for (int i = 0; i < cachePageCount; i++)
                {
                    uint v = GetUInt(cache[i], 0) % cachePageCount;
                    if (!Bytes.UnsafeCompare(cache[v], cache[v]))
                    {
                        throw new Exception();
                    }

                    cache[i] = Keccak512.Compute(cache[(i - 1 + cachePageCount) % cachePageCount].Xor(cache[v])).Bytes;
                }
            }

            return cache;
        }

        private const uint FnvPrime = 0x01000193;

        // TODO: optimize, check, work in progress
        private static byte[] Fnv(byte[] b1, byte[] b2)
        {
            Debug.Assert(b1.Length == b2.Length, "FNV expecting same length arrays");
            Debug.Assert(b1.Length % 4 == 0, "FNV expecting length to be a multiple of 4");

            // TODO: check this thing (in place calc)
            byte[] result = new byte[b1.Length];
            for (uint i = 0; i < b1.Length / 4; i++)
            {
                uint v1 = GetUInt(b1, i);
                uint v2 = GetUInt(b2, i);
                SetUInt(result, i, Fnv(v1, v2));
            }

            return result;
        }

        private static void SetUInt(byte[] bytes, uint offset, uint value)
        {
            byte[] valueBytes = value.ToByteArray(Bytes.Endianness.Little);
            Buffer.BlockCopy(valueBytes, 0, bytes, (int)offset * 4, 4);
        }

        private static uint GetUInt(byte[] bytes, uint offset)
        {
            return bytes.Slice((int)offset * 4, 4).ToUInt32(Bytes.Endianness.Little);
        }

        private static uint Fnv(uint v1, uint v2)
        {
            return (v1 * FnvPrime) ^ v2;
        }

        private const int CacheCacheSizeLimit = 6;
        
        public bool Validate(BlockHeader header)
        {   
            ulong epoch = GetEpoch(header.Number);
            
            ulong? epochToRemove = null;
            byte[][] cache = _cacheCache.GetOrAdd(epoch, e =>
            {   
                uint cacheSize = GetCacheSize(header.Number);
                Keccak seed = GetSeedHash(header.Number);

                // naive
                if (_cacheCache.Count > CacheCacheSizeLimit)
                {
                    int indextToRemove = Random.Next(CacheCacheSizeLimit);
                    {
                        int index = 0;
                        foreach (ulong epochInCache in _cacheCache.Keys)
                        {
                            if (index == indextToRemove)
                            {
                                epochToRemove = epochInCache;                                
                            }
                        }
                    }
                }
                
                Console.WriteLine($"Building cache for epoch {epoch}");
                return MakeCache(cacheSize, seed.Bytes); // TODO: load cache
            });

            if (epochToRemove.HasValue)
            {
                Console.WriteLine($"Removing cache for epoch {epochToRemove}");
                _cacheCache.TryRemove(epochToRemove.Value, out byte[][] removedItem);
            }

            ulong fullSize = GetDataSize(header.Number);
            (byte[] _, byte[] result) = HashimotoLight(fullSize, cache, header, header.Nonce);

            BigInteger threshold = BigInteger.Divide(BigInteger.Pow(2, 256), header.Difficulty);
//            BigInteger resultAsInteger = result.ToUnsignedBigInteger();
            BigInteger resultAsInteger = result.ToUnsignedBigInteger();
            return resultAsInteger < threshold;
        }

        private (byte[], byte[]) Hashimoto(ulong fullSize, BlockHeader header, ulong nonce, Func<uint, byte[]> getDataSetItem)
        {
            uint hashesInFull = (uint)(fullSize / HashBytes);
            uint wordsInMix = MixBytes / WordBytes;
            uint hashesInMix = MixBytes / HashBytes;
            byte[] headerHashed = Keccak.Compute(Rlp.Encode(header, false)).Bytes; // sic! Keccak here not Keccak512  // this tests fine
            byte[] headerAndNonceHashed = Keccak512.Compute(Bytes.Concat(headerHashed, nonce.ToByteArray(Bytes.Endianness.Little))).Bytes; // this tests fine
            byte[] mix = new byte[MixBytes];
            for (int i = 0; i < hashesInMix; i++)
            {
                Buffer.BlockCopy(headerAndNonceHashed, 0, mix, i * headerAndNonceHashed.Length, headerAndNonceHashed.Length);
            }

            for (uint i = 0; i < Accesses; i++)
            {
                uint p = Fnv(i ^ GetUInt(headerAndNonceHashed, 0), GetUInt(mix, i % wordsInMix)) % (hashesInFull / hashesInMix) * hashesInMix; // since we take 'hashesInMix' consecutive blocks we want only starting indices of such blocks
                byte[] newData = new byte[MixBytes];
                for (uint j = 0; j < hashesInMix; j++)
                {
                    byte[] item = getDataSetItem(p + j);
                    Buffer.BlockCopy(item, 0, newData, (int)(j * item.Length), item.Length);
                }

//                mix = Fnv(mix, newData);
                mix = Fnv(mix, newData);
            }


            byte[] cmix = new byte[MixBytes / 4];
            for (uint i = 0; i < MixBytes / 4; i += 4)
            {
                uint fnv = Fnv(Fnv(Fnv(GetUInt(mix, i), GetUInt(mix, i + 1)), GetUInt(mix, i + 2)), GetUInt(mix, i + 3));
                SetUInt(cmix, i / 4, fnv);
            }

            if (header.MixHash != Keccak.Zero && !Bytes.UnsafeCompare(cmix, header.MixHash.Bytes))
            {
                // TODO: handle properly
                throw new InvalidOperationException();
            }
            
            return (cmix, Keccak.Compute(Bytes.Concat(headerAndNonceHashed, cmix)).Bytes); // this tests fine
        }

        public (byte[], byte[]) HashimotoLight(ulong fullSize, byte[][] cache, BlockHeader header, ulong nonce)
        {
            return Hashimoto(fullSize, header, nonce, index => CalcDataSetItem(index, cache));
        }

        public (byte[], byte[]) HashimotoFull(ulong fullSize, byte[][] dataSet, BlockHeader header, ulong nonce)
        {
            return Hashimoto(fullSize, header, nonce, index => dataSet[index]);
        }
    }
}