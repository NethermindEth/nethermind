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
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;

namespace Nethermind.Network.Test
{
    public class TestRandom : ICryptoRandom
    {
        private readonly Func<int, int> _nextIntFunc;

        private readonly Func<int, byte[]> _nextRandomBytesFunc;

        private readonly Queue<byte[]> _nextRandomBytesQueue = new Queue<byte[]>();

        public TestRandom()
            : this(i => i / 2, (Func<int, byte[]>)null)
        {
        }

        public TestRandom(params byte[][] randomBytesInQueue)
            : this(i => i / 2, randomBytesInQueue)
        {
        }

        public TestRandom(Func<int, int> nextIntFunc, params byte[][] randomBytesInQueue)
            : this(nextIntFunc, (Func<int, byte[]>)null)
        {
            for (int i = 0; i < randomBytesInQueue.Length; i++)
            {
                _nextRandomBytesQueue.Enqueue(randomBytesInQueue[i]);
            }
        }

        public TestRandom(Func<int, int> nextIntFunc, Func<int, byte[]> nextRandomBytesFuncFunc)
        {
            _nextIntFunc = nextIntFunc;
            _nextRandomBytesFunc = nextRandomBytesFuncFunc ?? (i => _nextRandomBytesQueue.Dequeue());
        }

        public byte[] GenerateRandomBytes(int length)
        {
            return _nextRandomBytesFunc(length);
        }

        public void GenerateRandomBytes(Span<byte> bytes)
        {
            GenerateRandomBytes(bytes.Length).CopyTo(bytes);
        }

        public int NextInt(int max)
        {
            return _nextIntFunc(max);
        }

        public void EnqueueRandomBytes(params byte[][] randomBytesInQueue)
        {
            foreach (var randomBytes in randomBytesInQueue)
            {
                _nextRandomBytesQueue.Enqueue(randomBytes);
            }
        }

        public void Dispose()
        {
            
        }
    }
}
