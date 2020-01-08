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
using System.Collections;
using System.Collections.Generic;
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
        
        public void Feed(byte[]? value)
        {
            if (value is null)
            {
                return;
            }
            
            Merkle.Ize(out _chunks[^1], value);
            Feed(_chunks[^1]);
        }
        
        public void FeedBits(byte[]? value, uint limit)
        {
            if (value is null)
            {
                return;
            }
            
            Merkle.IzeBits(out _chunks[^1], value, limit);
            Feed(_chunks[^1]);
        }

        public void FeedBitvector(BitArray bitArray)
        {
            // bitfield_bytes
            byte[] bytes = new byte[(bitArray.Length + 7) / 8];
            bitArray.CopyTo(bytes, 0);
            
            Merkle.Ize(out _chunks[^1], bytes);
            Feed(_chunks[^1]);
        }

        public void Feed(BlsPublicKey? value)
        {
            if (value is null)
            {
                return;
            }
            
            Merkle.Ize(out _chunks[^1], value.Bytes);
            Feed(_chunks[^1]);
        }
        
        public void Feed(BlsSignature? value)
        {
            if (value is null)
            {
                return;
            }
            
            Merkle.Ize(out _chunks[^1], value.Bytes);
            Feed(_chunks[^1]);
        }
        
        public void Feed(ValidatorIndex value)
        {
            Merkle.Ize(out _chunks[^1], value);
            Feed(_chunks[^1]);
        }

        public void Feed(IReadOnlyList<ProposerSlashing> value, ulong maxLength)
        {
            if (value is null)
            {
                return;
            }
            
            UInt256[] subRoots = new UInt256[value.Count];
            for (int i = 0; i < value.Count; i++)
            {
                Merkle.Ize(out subRoots[i], value[i]);
            }

            Merkle.Ize(out _chunks[^1], subRoots, maxLength);
            Merkle.MixIn(ref _chunks[^1], value.Count);
            Feed(_chunks[^1]);
        }
        
        public void Feed(IReadOnlyList<AttesterSlashing> value, ulong maxLength)
        {
            if (value is null)
            {
                return;
            }
            
            UInt256[] subRoots = new UInt256[value.Count];
            for (int i = 0; i < value.Count; i++)
            {
                Merkle.Ize(out subRoots[i], value[i]);
            }

            Merkle.Ize(out _chunks[^1], subRoots, maxLength);
            Merkle.MixIn(ref _chunks[^1], value.Count);
            Feed(_chunks[^1]);
        }
        
        public void Feed(IReadOnlyList<Validator> value, ulong maxLength)
        {
            if (value is null)
            {
                return;
            }
            
            UInt256[] subRoots = new UInt256[value.Count];
            for (int i = 0; i < value.Count; i++)
            {
                Merkle.Ize(out subRoots[i], value[i]);
            }

            Merkle.Ize(out _chunks[^1], subRoots, maxLength);
            Merkle.MixIn(ref _chunks[^1], value.Count);
            Feed(_chunks[^1]);
        }

        public void Feed(IReadOnlyList<Attestation> value, ulong maxLength)
        {
            if (value is null)
            {
                return;
            }
            
            UInt256[] subRoots = new UInt256[value.Count];
            for (int i = 0; i < value.Count; i++)
            {
                Merkle.Ize(out subRoots[i], value[i]);
            }

            Merkle.Ize(out _chunks[^1], subRoots, maxLength);
            Merkle.MixIn(ref _chunks[^1], value.Count);
            Feed(_chunks[^1]);
        }
        
        public void Feed(IReadOnlyList<PendingAttestation> value, ulong maxLength)
        {
            if (value is null)
            {
                return;
            }
            
            UInt256[] subRoots = new UInt256[value.Count];
            for (int i = 0; i < value.Count; i++)
            {
                Merkle.Ize(out subRoots[i], value[i]);
            }

            Merkle.Ize(out _chunks[^1], subRoots, maxLength);
            Merkle.MixIn(ref _chunks[^1], value.Count);
            Feed(_chunks[^1]);
        }
        
        public void Feed(IReadOnlyList<VoluntaryExit> value, ulong maxLength)
        {
            if (value is null)
            {
                return;
            }
            
            UInt256[] subRoots = new UInt256[value.Count];
            for (int i = 0; i < value.Count; i++)
            {
                Merkle.Ize(out subRoots[i], value[i]);
            }

            Merkle.Ize(out _chunks[^1], subRoots, maxLength);
            Merkle.MixIn(ref _chunks[^1], value.Count);
            Feed(_chunks[^1]);
        }
        
        public void Feed(Eth1Data[]? value, uint maxLength)
        {
            if (value is null)
            {
                return;
            }
            
            UInt256[] subRoots = new UInt256[value.Length];
            for (int i = 0; i < value.Length; i++)
            {
                Merkle.Ize(out subRoots[i], value[i]);
            }

            Merkle.Ize(out _chunks[^1], subRoots, maxLength);
            Merkle.MixIn(ref _chunks[^1], value.Length);
            Feed(_chunks[^1]);
        }
        
        public void Feed(IReadOnlyList<Deposit> value, uint maxLength)
        {
            if (value is null)
            {
                return;
            }
            
            UInt256[] subRoots = new UInt256[value.Count];
            for (int i = 0; i < value.Count; i++)
            {
                Merkle.Ize(out subRoots[i], value[i]);
            }

            Merkle.Ize(out _chunks[^1], subRoots, maxLength);
            Merkle.MixIn(ref _chunks[^1], value.Count);
            Feed(_chunks[^1]);
        }
        
        public void Feed(ValidatorIndex[]? value, uint maxLength)
        {
            if (value is null)
            {
                return;
            }
            
            Merkle.Ize(out _chunks[^1], MemoryMarshal.Cast<ValidatorIndex, ulong>(value.AsSpan()), maxLength);
            Merkle.MixIn(ref _chunks[^1], value.Length);
            Feed(_chunks[^1]);
        }
        
        public void Feed(Gwei[]? value, ulong maxLength)
        {
            if (value is null)
            {
                return;
            }
            
            Merkle.Ize(out _chunks[^1], MemoryMarshal.Cast<Gwei, ulong>(value.AsSpan()), maxLength);
            Merkle.MixIn(ref _chunks[^1], value.Length);
            Feed(_chunks[^1]);
        }
        
        public void Feed(Gwei[]? value)
        {
            if (value is null)
            {
                return;
            }
            
            Merkle.Ize(out _chunks[^1], MemoryMarshal.Cast<Gwei, ulong>(value.AsSpan()));
            Feed(_chunks[^1]);
        }
        
        public void Feed(Hash32[]? value, ulong maxLength)
        {
            if (value is null)
            {
                return;
            }
            
            UInt256[] subRoots = new UInt256[value.Length];
            for (int i = 0; i < value.Length; i++)
            {
                Merkle.Ize(out subRoots[i], value[i]);
            }

            Merkle.Ize(out _chunks[^1], subRoots, maxLength);
            Merkle.MixIn(ref _chunks[^1], value.Length);
            Feed(_chunks[^1]);
        }
        
        public void Feed(CommitteeIndex value)
        {
            Merkle.Ize(out _chunks[^1], value.Number);
            Feed(_chunks[^1]);
        }
        
        public void Feed(Epoch value)
        {
            Merkle.Ize(out _chunks[^1], value);
            Feed(_chunks[^1]);
        }
        
        public void Feed(Fork? value)
        {
            if (value is null)
            {
                return;
            }
            
            Merkle.Ize(out _chunks[^1], value);
            Feed(_chunks[^1]);
        }
        
        public void Feed(Eth1Data? value)
        {
            if (value is null)
            {
                return;
            }
            
            Merkle.Ize(out _chunks[^1], value);
            Feed(_chunks[^1]);
        }
        
        public void Feed(Checkpoint? value)
        {
            if (value is null)
            {
                return;
            }
            
            Merkle.Ize(out _chunks[^1], value);
            Feed(_chunks[^1]);
        }
        
        public void Feed(BeaconBlockHeader? value)
        {
            if (value is null)
            {
                return;
            }
            
            Merkle.Ize(out _chunks[^1], value);
            Feed(_chunks[^1]);
        }
        
        public void Feed(BeaconBlockBody? value)
        {
            if (value is null)
            {
                return;
            }
            
            Merkle.Ize(out _chunks[^1], value);
            Feed(_chunks[^1]);
        }
        
        public void Feed(AttestationData? value)
        {
            if (value is null)
            {
                return;
            }
            
            Merkle.Ize(out _chunks[^1], value);
            Feed(_chunks[^1]);
        }
        
        public void Feed(IndexedAttestation? value)
        {
            if (value is null)
            {
                return;
            }
            
            Merkle.Ize(out _chunks[^1], value);
            Feed(_chunks[^1]);
        }
        
        public void Feed(DepositData? value)
        {
            if (value is null)
            {
                return;
            }
            
            Merkle.Ize(out _chunks[^1], value);
            Feed(_chunks[^1]);
        }
        
        public void Feed(ForkVersion value)
        {
            Span<byte> padded = stackalloc byte[32];
            value.AsSpan().CopyTo(padded);
            Merkle.Ize(out _chunks[^1], padded);
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
        
        public void Feed(Bytes32 value)
        {
            Feed(MemoryMarshal.Cast<byte, UInt256>(value.AsSpan())[0]);
        }

        public void Feed(Hash32 value)
        {
            Feed(MemoryMarshal.Cast<byte, UInt256>(value.Bytes)[0]);
        }
        
//        public void Feed(ReadOnlySpan<Hash32> value)
//        {
//            if (value == null)
//            {
//                return;
//            }
//            
//            UInt256[] input = new UInt256[value.Length];
//            for (int i = 0; i < value.Length; i++)
//            {
//                UInt256.CreateFromLittleEndian(out input[i], value[i].Bytes ?? Hash32.Zero.Bytes);
//            }
//            
//            Merkle.Ize(out _chunks[^1], input);
//            Feed(_chunks[^1]);
//        }

        public void Feed(IReadOnlyList<Hash32> value)
        {
            if (value == null)
            {
                return;
            }
            
            UInt256[] input = new UInt256[value.Count];
            for (int i = 0; i < value.Count; i++)
            {
                UInt256.CreateFromLittleEndian(out input[i], value[i].Bytes ?? Hash32.Zero.Bytes);
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