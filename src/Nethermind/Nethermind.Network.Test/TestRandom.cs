using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.Test
{
    public class TestRandom : ICryptoRandom
    {
        private readonly Func<int, int> _nextIntFunc;

        private readonly Func<int, byte[]> _nextRandomBytesFunc;

        private readonly Queue<byte[]> _nextRandomBytesQueue = new Queue<byte[]>();

        public TestRandom()
            : this(i => throw new NotImplementedException(), (Func<int, byte[]>)null)
        {
        }

        public TestRandom(params byte[][] randomBytesInQueue)
            : this(i => throw new NotImplementedException(), randomBytesInQueue)
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
            return new Hex(_nextRandomBytesFunc(length));
        }

        public int NextInt(int max)
        {
            return _nextIntFunc(max);
        }

        public void EnqueueRandomBytes(Hex randomBytes)
        {
            _nextRandomBytesQueue.Enqueue(randomBytes);
        }
    }
}