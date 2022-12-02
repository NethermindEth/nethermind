// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.HashLib.Crypto.SHA3;

namespace Nethermind.HashLib
{
    public static class HashFactory
    {
        public static class Crypto
        {
            public static class SHA3
            {
                public static IHash CreateBlake224()
                {
                    return new HashLib.Crypto.SHA3.Blake224();
                }

                public static IHash CreateBlake256()
                {
                    return new HashLib.Crypto.SHA3.Blake256();
                }

                public static IHash CreateBlake384()
                {
                    return new HashLib.Crypto.SHA3.Blake384();
                }

                public static IHash CreateBlake512()
                {
                    return new HashLib.Crypto.SHA3.Blake512();
                }

                /// <summary>
                /// 
                /// </summary>
                /// <param name="a_hash_size">224, 256, 384, 512</param>
                /// <returns></returns>
                public static IHash CreateBlake(HashLib.HashSize a_hash_size)
                {
                    switch (a_hash_size)
                    {
                        case HashLib.HashSize.HashSize224: return CreateBlake224();
                        case HashLib.HashSize.HashSize256: return CreateBlake256();
                        case HashLib.HashSize.HashSize384: return CreateBlake384();
                        case HashLib.HashSize.HashSize512: return CreateBlake512();
                        default: throw new ArgumentException();
                    }
                }

                public static IHash CreateKeccak224()
                {
                    return new HashLib.Crypto.SHA3.Keccak224();
                }

                public static Keccak256 CreateKeccak256()
                {
                    return new();
                }

                public static IHash CreateKeccak384()
                {
                    return new HashLib.Crypto.SHA3.Keccak384();
                }

                public static Keccak512 CreateKeccak512()
                {
                    return new();
                }

                /// <summary>
                /// 
                /// </summary>
                /// <param name="a_hash_size">224, 256, 384, 512</param>
                /// <returns></returns>
                public static IHash CreateKeccak(HashLib.HashSize a_hash_size)
                {
                    switch (a_hash_size)
                    {
                        case HashLib.HashSize.HashSize224: return CreateKeccak224();
                        case HashLib.HashSize.HashSize256: return CreateKeccak256();
                        case HashLib.HashSize.HashSize384: return CreateKeccak384();
                        case HashLib.HashSize.HashSize512: return CreateKeccak512();
                        default: throw new ArgumentException();
                    }
                }
            }

            public static class BuildIn
            {
                public static IHash CreateMD5CryptoServiceProvider()
                {
                    return new HashLib.Crypto.BuildIn.MD5CryptoServiceProvider();
                }

                public static IHash CreateSHA256Cng()
                {
                    return new HashLib.Crypto.BuildIn.SHA256Cng();
                }

                public static IHash CreateSHA256CryptoServiceProvider()
                {
                    return new HashLib.Crypto.BuildIn.SHA256CryptoServiceProvider();
                }

                public static IHash CreateSHA256Managed()
                {
                    return new HashLib.Crypto.BuildIn.SHA256Managed();
                }
            }


            public static IHash CreateMD5()
            {
                return new HashLib.Crypto.MD5();
            }

            public static IHash CreateRIPEMD()
            {
                return new HashLib.Crypto.RIPEMD();
            }

            public static IHash CreateRIPEMD160()
            {
                return new HashLib.Crypto.RIPEMD160();
            }

            public static IHash CreateSHA256()
            {
                return new HashLib.Crypto.SHA256();
            }

            public static IHash CreateSHA384()
            {
                return new HashLib.Crypto.SHA384();
            }

            public static IHash CreateSHA512()
            {
                return new HashLib.Crypto.SHA512();
            }
        }

        public static class HMAC
        {
            public static IHMAC CreateHMAC(IHash a_hash)
            {
                if (a_hash is IHMAC)
                {
                    return (IHMAC)a_hash;
                }
                else if (a_hash is IHasHMACBuildIn)
                {
                    IHasHMACBuildIn h = (IHasHMACBuildIn)a_hash;
                    return new HMACBuildInAdapter(h.GetBuildHMAC(), h.BlockSize);
                }
                else
                {
                    return new HMACNotBuildInAdapter(a_hash);
                }
            }
        }

        public static class Wrappers
        {
            public static System.Security.Cryptography.HashAlgorithm HashToHashAlgorithm(IHash a_hash)
            {
                return new HashAlgorithmWrapper(a_hash);
            }

            public static IHash HashAlgorithmToHash(System.Security.Cryptography.HashAlgorithm a_hash,
                int a_block_size = -1)
            {
                return new HashCryptoBuildIn(a_hash, a_block_size);
            }
        }
    }
}
