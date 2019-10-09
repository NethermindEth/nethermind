using System;
using System.Runtime.InteropServices;

namespace Cortex.SimpleSerialize
{
    public abstract class SszElement
    {
        public abstract SszElementType ElementType { get; }

        protected byte[] ToLittleEndianBytes<T>(ReadOnlySpan<T> value, int basicSize)
            where T : struct
        {
            var bytes = MemoryMarshal.Cast<T, byte>(value);
            if (BitConverter.IsLittleEndian)
            {
                return bytes.ToArray();
            }
            else
            {
                var result = new byte[bytes.Length];
                for (var index1 = 0; index1 < bytes.Length; index1 += basicSize)
                {
                    for (var index2 = 0; index2 < basicSize; index2++)
                    {
                        result[index1 + index2] = bytes[index1 + basicSize - index2 - 1];
                    }
                }
                return result;
            }
        }
    }
}
