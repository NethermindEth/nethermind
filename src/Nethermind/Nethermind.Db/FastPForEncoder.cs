
using System;
using System.Runtime.Intrinsics;

namespace Nethermind.Db
{
    public sealed unsafe class FastPForEncoder : ILogEncoder<byte, byte>
    {
        private int _blocksize;

        public FastPForEncoder(int blocksize)
        {
            _blocksize = blocksize;
        }
        public void Encode(Span<byte> value, byte[] output)
        {

            var prev = Vector64.Create((sbyte)value[0]);

            int i = 0;


            fixed (byte* _entries = value)
            fixed (byte* _entriesbytes = &output[0])
            {
                byte* _tmpout = _entriesbytes;
                for (; i + _blocksize <= value.Length; i += _blocksize)
                {
                    int j = 0;

                    for (; j < _blocksize; j += Vector64<sbyte>.Count)
                    {
                        var cur = Vector64.Load((sbyte*)_entries + i + j);

                        var mixed = Vector64.Shuffle(cur, Vector64.Create(0, 0, 1, 2, 3, 4, 5, 6)) & Vector64.Create(0, -1, -1, -1, -1, -1, -1, -1) |
                                Vector64.Shuffle(prev, Vector64.Create(7, 7, 7, 7, 7, 7, 7, 7)) & Vector64.Create(-1, 0, 0, 0, 0, 0, 0, 0);
                        prev = cur;
                        var delta = cur - mixed;

                        var _entriesSbyte = (sbyte*)_tmpout;
                        // You can use the pointer in here.
                        delta.Store(_entriesSbyte);
                        _tmpout += Vector64<byte>.Count;
                    }
                }
                _entriesbytes[0] = (byte)value[0];
            }


            // store the remaining without using SIMD
            if (i < value.Length)
            {
            }


        }
    }
}


























