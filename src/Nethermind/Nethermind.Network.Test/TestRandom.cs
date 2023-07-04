// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Crypto;

namespace Nethermind.Network.Test
{
    public class TestRandom : ICryptoRandom
    {
        private readonly Func<int, int> _nextIntFunc;

        private readonly Func<int, byte[]> _nextRandomBytesFunc;

        private readonly Queue<byte[]> _nextRandomBytesQueue = new();

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
