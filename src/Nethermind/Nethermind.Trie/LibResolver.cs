using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Native;

namespace Nethermind.Trie
{
    public static class LibResolver
    {
        private static int _done;        
        
        public static void Setup()
        {
            if (Interlocked.CompareExchange(ref _done, 1, 0) == 0)
            {
                NativeLibrary.SetDllImportResolver(typeof(RustVerkleLib).Assembly, NativeLib.ImportResolver);
            }
        }
    }
}
