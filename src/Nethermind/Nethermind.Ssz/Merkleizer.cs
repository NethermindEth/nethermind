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
using System.Linq;
using System.Runtime.InteropServices;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Ssz
{
    public ref struct Merkleizer
    {
        public bool IsKthBitSet(int k)
        {
            return (_filled & ((ulong)1 << k)) != 0;
        }
        
        public void SetKthBit(int k)
        {
            _filled |= (ulong)1 << k;
        }
        
        public void UnsetKthBit(int k)
        {
            _filled &= ~((ulong)1 << k);
        }

        private Span<UInt256> _chunks;
        private ulong _filled;

        public UInt256 PartChunk
        {
            get
            {
                _chunks[^1] = UInt256.Zero;
                return _chunks[^1];
            }
        }

        public Merkleizer(Span<UInt256> chunks)
        {
            _chunks = chunks;
            _filled = 0;
        }
        
        public Merkleizer(int depth)
        {
            _chunks = new UInt256[depth + 1];
            _filled = 0;
        }

        public void Feed(UInt256 chunk)
        {
            FeedAtLevel(chunk, 0);
        }
        
        public void Feed(Span<byte> bytes)
        {
            FeedAtLevel(MemoryMarshal.Cast<byte, UInt256>(bytes)[0], 0);
        }
        
        public void Feed(bool value)
        {
            Merkle.Ize(out _chunks[^1], value);
            Feed(_chunks[^1]);
        }
        
        public void Feed(uint value)
        {
            Merkle.Ize(out _chunks[^1], value);
            Feed(_chunks[^1]);
        }
        
        public void Feed(ulong value)
        {
            Merkle.Ize(out _chunks[^1], value);
            Feed(_chunks[^1]);
        }
        
        public void Feed(byte[] value)
        {
            Merkle.Ize(out _chunks[^1], value);
            Feed(_chunks[^1]);
        }
        
        public void Feed(BlsPublicKey value)
        {
            Merkle.Ize(out _chunks[^1], value.Bytes);
            Feed(_chunks[^1]);
        }
        
        public void Feed(BlsSignature value)
        {
            Merkle.Ize(out _chunks[^1], value.Bytes);
            Feed(_chunks[^1]);
        }
        
        public void Feed(ValidatorIndex value)
        {
            Merkle.Ize(out _chunks[^1], value.Number);
            Feed(_chunks[^1]);
        }
        
        public void Feed(CommitteeIndex value)
        {
            Merkle.Ize(out _chunks[^1], value.Number);
            Feed(_chunks[^1]);
        }
        
        public void Feed(Epoch value)
        {
            Merkle.Ize(out _chunks[^1], value.Number);
            Feed(_chunks[^1]);
        }
        
        public void Feed(Checkpoint value)
        {
            Merkle.Ize(out _chunks[^1], value);
            Feed(_chunks[^1]);
        }
        
        public void Feed(BeaconBlockHeader value)
        {
            Merkle.Ize(out _chunks[^1], value);
            Feed(_chunks[^1]);
        }
        
        public void Feed(AttestationData value)
        {
            Merkle.Ize(out _chunks[^1], value);
            Feed(_chunks[^1]);
        }
        
        public void Feed(ForkVersion value)
        {
            Merkle.Ize(out _chunks[^1], value.Number);
            Feed(_chunks[^1]);
        }
        
        public void Feed(Gwei value)
        {
            Merkle.Ize(out _chunks[^1], value.Amount);
            Feed(_chunks[^1]);
        }
        
        public void Feed(Slot value)
        {
            Merkle.Ize(out _chunks[^1], value.Number);
            Feed(_chunks[^1]);
        }
        
        public void Feed(Sha256 value)
        {
            Feed(MemoryMarshal.Cast<byte, UInt256>(value.Bytes)[0]);
        }
        
        public void Feed(Span<Sha256> value)
        {
            UInt256[] input = new UInt256[value.Length];
            for (int i = 0; i < value.Length; i++)
            {
                UInt256.CreateFromLittleEndian(out input[i], value[i].Bytes);
            }
            
            Merkle.Ize(out _chunks[^1], input);
            Feed(_chunks[^1]);
        }
        
        private void FeedAtLevel(UInt256 chunk, int level)
        {
            for (int i = level; i < _chunks.Length; i++)
            {
                if (IsKthBitSet(i))
                {
                    chunk = Merkle.HashConcatenation(_chunks[i], chunk, i);
                    UnsetKthBit(i);
                }
                else
                {
                    _chunks[i] = chunk;
                    SetKthBit(i);
                    break;
                }
            }
        }

        public void CalculateRoot(out UInt256 root)
        {
            root = CalculateRoot();
        }
        
        public UInt256 CalculateRoot()
        {
            int lowestSet = 0;
            while (true)
            {
                for (int i = lowestSet; i < _chunks.Length; i++)
                {
                    if (IsKthBitSet(i))
                    {
                        lowestSet = i;
                        break;
                    }
                }

                if (lowestSet == _chunks.Length - 1)
                {
                    break;
                }

                UInt256 chunk = Merkle.ZeroHashes[lowestSet];
                FeedAtLevel(chunk, lowestSet);
            }

            return _chunks[^1];
        }
    }
}