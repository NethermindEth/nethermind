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
// 

using System;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Crypto
{
    public class KeccakRlpStream : RlpStream
    {
        private readonly KeccakHash _keccakHash;

        public KeccakRlpStream(KeccakHash keccakHash)
        {
            _keccakHash = keccakHash;
        }

        public override void Write(Span<byte> bytesToWrite)
        {
            _keccakHash.Update(bytesToWrite, 0, bytesToWrite.Length);
        }

        public override void WriteByte(byte byteToWrite)
        {
            _keccakHash.Update(MemoryMarshal.CreateSpan(ref byteToWrite, 1), 0, 1);
        }

        protected override void WriteZero(int length)
        {
            Span<byte> zeros = stackalloc byte[length]; 
            Write(zeros);
        }

        public override byte ReadByte()
        {
            throw new NotSupportedException("Cannot read form Keccak");
        }

        protected override Span<byte> Read(int length)
        {
            throw new NotSupportedException("Cannot read form Keccak");
        }

        public override byte PeekByte()
        {
            throw new NotSupportedException("Cannot read form Keccak");
        }

        protected override byte PeekByte(int offset)
        {
            throw new NotSupportedException("Cannot read form Keccak");
        }

        protected override void SkipBytes(int length)
        {
            WriteZero(length);
        }

        public override int Position
        {
            get => throw new NotSupportedException("Cannot read form Keccak");
            set => throw new NotSupportedException("Cannot read form Keccak");
        }

        public override int Length => throw new NotSupportedException("Cannot read form Keccak");

        protected override string Description => "|KeccakRlpSTream|description missing|";
    }
}
