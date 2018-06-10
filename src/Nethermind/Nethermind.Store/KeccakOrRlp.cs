/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;

namespace Nethermind.Store
{
    internal class KeccakOrRlp
    {
        public bool IsKeccak { get; }

        public KeccakOrRlp(Keccak keccak)
        {
            _keccak = keccak;
            IsKeccak = true;
        }

        public KeccakOrRlp(Rlp rlp)
        {
            if (rlp.Bytes.Length < 32)
            {
                _rlp = rlp;
            }
            else
            {
                Metrics.TreeNodeHashCalculations++;
                _keccak = Keccak.Compute(rlp);
                IsKeccak = true;
            }
        }

        private Rlp _rlp;
        private Keccak _keccak;

        public byte[] Bytes => _rlp?.Bytes ?? _keccak.Bytes;

        public Keccak GetKeccakOrThrow()
        {
            return _keccak ?? throw new InvalidOperationException("Unexpected null Keccak");
        }
        
        public Keccak GetOrComputeKeccak()
        {
            if (!IsKeccak)
            {
                Metrics.TreeNodeHashCalculations++;
                _keccak = Keccak.Compute(_rlp);
            }

            return _keccak;
        }

        public Rlp GetRlpOrThrow()
        {
            return _rlp ?? throw new InvalidOperationException("Unexpected null RLP");
        }
        
        public Rlp GetOrEncodeRlp()
        {
            return _rlp ?? (_rlp = Rlp.Encode(_keccak));
        }

        public override string ToString()
        {
            return IsKeccak
                ? _keccak.ToString(true).Substring(0, 6)
                : PatriciaTree.RlpDecode(new Rlp(Bytes)).ToString();
        }
    }
}