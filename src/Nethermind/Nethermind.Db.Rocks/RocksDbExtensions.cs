using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using RocksDbSharp;

namespace Nethermind.Db.Rocks
{
    internal static class RocksDbExtensions
    {
        private static ReadOptions DefaultReadOptions { get; } = new ReadOptions();

        public static unsafe Span<byte> GetSpan(this RocksDb db, byte[] key, ColumnFamilyHandle? cf = null)
        {
            var instance = RocksDbSharp.Native.Instance;
            var read_options = DefaultReadOptions.Handle;
            var keyLength = key.GetLongLength(0);
            keyLength = keyLength == 0 ? key.Length : keyLength;

            UIntPtr skLength = (UIntPtr)keyLength;

            var resultPtr = cf == null
                ? instance.rocksdb_get(db.Handle, read_options, key, skLength, out UIntPtr valueLength, out var errptr)
                : instance.rocksdb_get_cf(db.Handle, read_options, cf.Handle, key, skLength, out valueLength, out errptr);

            if (errptr != IntPtr.Zero)
                return null;
            if (resultPtr == IntPtr.Zero)
                return null;
            Span<byte> span = new Span<byte>((void*)resultPtr, (int)valueLength);

            if (errptr != IntPtr.Zero)
                throw new RocksDbException(errptr);

            return span;
        }

        public static unsafe void DangerousReleaseMemory(this RocksDb db, in Span<byte> span)
        {
            ref byte ptr = ref MemoryMarshal.GetReference(span);
            IntPtr intPtr = new IntPtr(Unsafe.AsPointer(ref ptr));

            var instance = RocksDbSharp.Native.Instance;
            instance.rocksdb_free(intPtr);
        }
    }
}
