using System;

namespace Cortex.SimpleSerialize
{
    public class SszNode
    {
        // X - A
        //   - B - C
        //         D

        // X_summary - A
        //             B_root

        // hash(X) and hash(X_summary) are the same
        // because B_root = hash(B) = hash(C,D)
        // (B is a class, B_root is a hash)


        // ulong (Eth1Data)
        // byte array
        // class
        // child class
        // child list

    }
}
