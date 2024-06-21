using System;
using System.Runtime.Intrinsics;

namespace Nethermind.Db
{
    public class FastPForDecoder : ILogDecoder<byte, byte>
    {
        private int _blockSize;


        public FastPForDecoder(int blocksize)
        {
            _blockSize = blocksize;
        }
        public unsafe void Decode(byte[] encoded, Span<byte> output)
        {

            var prev = Vector64.Create((sbyte)0);


            int i = 0;

            fixed (byte* _entries = encoded)
            fixed (byte* _entriesbytes = &output[0])
            {
                byte* _tmpout = _entriesbytes;
                for (; i + Vector64<byte>.Count <= encoded.Length; i += Vector64<byte>.Count)
                {

                    var cur = Vector64.Load((sbyte*)_entries + i);

                    cur += Vector64.Shuffle(cur, Vector64.Create(0, 0, 1, 2, 3, 4, 5, 6)) & Vector64.Create(0, -1, -1, -1, -1, -1, -1, -1);
                    cur += Vector64.Shuffle(cur, Vector64.Create(0, 0, 0, 1, 2, 3, 4, 5)) & Vector64.Create(0, 0, -1, -1, -1, -1, -1, -1);
                    cur += Vector64.Shuffle(cur, Vector64.Create(0, 0, 0, 0, 0, 1, 2, 3)) & Vector64.Create(0, 0, 0, 0, -1, -1, -1, -1);
                    cur += prev;

                    prev = Vector64.Shuffle(cur, Vector64.Create(7, 7, 7, 7, 7, 7, 7, 7));

                    var _entriesSByte = (sbyte*)_tmpout;
                    // You can use the pointer in here.
                    cur.Store(_entriesSByte);
                    _tmpout += Vector64<byte>.Count;
                }
            }


        }
    }
}
