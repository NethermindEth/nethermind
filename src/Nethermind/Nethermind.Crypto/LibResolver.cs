using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Crypto.ZkSnarks;
using Nethermind.Native;

namespace Nethermind.Crypto
{
    public static class LibResolver
    {
        private static int _done = 0;        
        
        public static void Setup()
        {
            if (Interlocked.CompareExchange(ref _done, 1, 0) == 0)
            {
                NativeLibrary.SetDllImportResolver(typeof(Bn256).Assembly, NativeLib.ImportResolver);
            }
        }
    }
}