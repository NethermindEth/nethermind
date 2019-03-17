/*
 * implementation from
 * https://github.com/MeadowSuite/Meadow/blob/master/src/Meadow.Core/Cryptography/KeccakHash.cs
 * only here for benchmark reference
 */

using System;
using System.Security.Cryptography;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Network.Benchmarks
{
    public class MeadowKdf
    {
        private KDFHashAlgorithm _hashType = KDFHashAlgorithm.Sha256;
        
        private enum KDFHashAlgorithm
        {
            Keccak256,
            Sha256
        }
        
        // thread local it
        private static SHA256 _sha256 = SHA256.Create();
        
                /// <summary>
        /// Performs the NIST SP 800-56 Concatenation Key Derivation Function ("KDF") to derive a key of the specified desired length from a base key of arbitrary length.
        /// </summary>
        /// <param name="key">The base key to derive another key from.</param>
        /// <param name="desiredKeyLength">The desired key length of the resulting derived key.</param>
        /// <param name="hashType">The type of hash algorithm to use in the key derivation process.</param>
        /// <returns>Returns the key derived from the provided base key and hash algorithm.</returns>
        public byte[] DeriveKeyKDF(byte[] key, int desiredKeyLength)
        {
            // References:
            // https://csrc.nist.gov/CSRC/media/Publications/sp/800-56a/archive/2006-05-03/documents/sp800-56-draft-jul2005.pdf

            // Define our block size and hash size
            int hashSize;
            if (_hashType == KDFHashAlgorithm.Sha256)
            {
                hashSize = 32;
            }
            else if (_hashType == KDFHashAlgorithm.Keccak256)
            {
                hashSize = 32;
            }
            else
            {
                throw new NotImplementedException();
            }

            // Determine the amount of hashes required to generate a key of the desired length (ceiling by adding one less bit than needed to round up 1)
            int hashRounds = (desiredKeyLength + (hashSize - 1)) / hashSize;

            // Create a memory space to store all hashes for each round. The final key will slice from the start of this for all bytes it needs.
            byte[] aggregateHashData = new byte[hashRounds * hashSize];
            Span<byte> aggregateHashMemory = aggregateHashData;
            int aggregateHashOffset = 0;

            // Loop for each hash round to compute.
            for (int i = 0; i <= hashRounds; i++)
            {
                // Get the iteration count (starting from 1)
                byte[] counterData = BitConverter.GetBytes(i + 1);
                if (BitConverter.IsLittleEndian)
                {
                    Array.Reverse(counterData);
                }

                // Get the data to hash in a single buffer
                byte[] dataToHash = Bytes.Concat(counterData, key);

                // Determine what provider to use to hash the buffer.
                if (_hashType == KDFHashAlgorithm.Sha256)
                {
                    // Calculate the SHA256 hash for this round.
                    byte[] hashResult = _sha256.ComputeHash(dataToHash);

                    // Copy it into our all hashes buffer.
                    hashResult.CopyTo(aggregateHashMemory.Slice(aggregateHashOffset, hashSize));
                }
                else if (_hashType == KDFHashAlgorithm.Keccak256)
                {
                    // Calculate the Keccak256 hash for this round.
                    KeccakHash.ComputeHash(dataToHash, aggregateHashMemory.Slice(aggregateHashOffset, hashSize));
                }
                else
                {
                    throw new NotImplementedException();
                }

                // Advance our offset
                aggregateHashOffset += hashSize;

                // If our offset is passed our required key length, we can stop early
                if (aggregateHashOffset >= desiredKeyLength)
                {
                    break;
                }
            }

            // Slice off only the desired data
            return aggregateHashMemory.Slice(0, desiredKeyLength).ToArray();
        }
    }
}