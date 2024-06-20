
using System;
using System.Runtime.Intrinsics;

namespace Nethermind.Db
{
<<<<<<< HEAD
    public sealed unsafe class FastPForEncoder : ILogEncoder<byte[]>
=======
    public sealed unsafe class FastPForEncoder : ILogEncoder<long, byte>
>>>>>>> b9db80c16 (WIP FastPForEncoder)
    {
        private int _blocksize;

        public FastPForEncoder(int blocksize)
        {
            _blocksize = blocksize;
        }
<<<<<<< HEAD
        public void Encode(Span<byte> value, byte[] output)
=======
        public void Encode(Span<long> value, byte[] output)
>>>>>>> b9db80c16 (WIP FastPForEncoder)
        {

            var prev = Vector256.Create((long)value[0]);

            int i = 0;

            fixed (long* _entries = value)
            fixed (byte* _entriesbytes = output)
            {
                for (; i + _blocksize <= value.Length; i += _blocksize)
                {
                    int j = 0;

                    for (; j < _blocksize; j += Vector256<long>.Count)
                    {
                        var cur = Vector256.Load(_entries + i + j);

                        var curShuffled = Vector256.Shuffle(cur, Vector256.Create(0, 0, 1, 2)) & Vector256.Create(0, -1, -1, -1);
                        var prevShuffled = Vector256.Shuffle(prev, Vector256.Create(3, 3, 3, 3)) & Vector256.Create(-1, 0, 0, 0);

                        var mixed = Vector256.Shuffle(cur, Vector256.Create(0, 0, 1, 2)) & Vector256.Create(0, -1, -1, -1) |
                                Vector256.Shuffle(prev, Vector256.Create(3, 3, 3, 3)) & Vector256.Create(-1, 0, 0, 0);
                        prev = cur;
                        var delta = cur - mixed;
                        Console.WriteLine("checking delta");
                        Console.WriteLine(delta.ToString());
                        var deltaInts = Vector256.Shuffle(delta.AsUInt32(), Vector256.Create(0u, 2, 4, 6, 0, 0, 0, 0));

                        var _entriesInts = (uint*)_entriesbytes;
                        // You can use the pointer in here.
                        deltaInts.Store(_entriesInts);


                    }
                }
            }

            if (i < value.Length)
            {
                var cur = Vector256.Create((long)value[i]);
                var mixed = Vector256.Shuffle(cur, Vector256.Create(0, 0, 1, 2)) & Vector256.Create(0, -1, -1, -1) |
                        Vector256.Shuffle(prev, Vector256.Create(3, 3, 3, 3)) & Vector256.Create(-1, 0, 0, 0);
                prev = cur;
                var delta = cur - mixed;
                var deltaInts = Vector256.Shuffle(delta.AsUInt32(), Vector256.Create(0u, 2, 4, 6, 0, 0, 0, 0));
                fixed (byte* _entriesbytes = output)
                {
                    var _entriesInts = (uint*)_entriesbytes;
                    // You can use the pointer in here.
                    deltaInts.Store(_entriesInts);

                }
            }


        }
    }
}


























