using System.Runtime.InteropServices;
using System.Reflection;
using System.Runtime.Loader;
using System.IO;
using System;

namespace Nethermind.Db
{
    // https://github.com/brettwooldridge/TurboPFor#function-syntax
    // https://github.com/brettwooldridge/TurboPFor/blob/master/java/jic.java
    // https://github.com/brettwooldridge/TurboPFor/blob/master/vp4.h
    // TODO: move to separate Nuget package
    public static class TurboPFor
    {
        private const string LibraryName = "ic";
        private static string? _libraryFallbackPath;

        static TurboPFor() => AssemblyLoadContext.Default.ResolvingUnmanagedDll += OnResolvingUnmanagedDll;

        [DllImport(LibraryName)]
        public static extern unsafe int vbenc32(int[] @in, int n, byte[] @out);
        [DllImport(LibraryName)]
        public static extern unsafe int vbdec32(byte[] @in, int n, int[] @out);


        [DllImport(LibraryName)]
        public extern unsafe static int vbdenc32(int[] @in, int n, byte[] @out, int start);
        [DllImport(LibraryName)]
        public extern unsafe static int vbddec32(byte[] @in, int n, int[] @out, int start);

        [DllImport(LibraryName)]
        public extern unsafe static int vbd1enc32(int[] @in, int n, byte[] @out, int start);
        [DllImport(LibraryName)]
        public extern unsafe static int vbd1dec32(byte[] @in, int n, int[] @out, int start);

        [DllImport(LibraryName)]
        public extern unsafe static int vbzenc32(int[] @in, int n, byte[] @out, int start);
        [DllImport(LibraryName)]
        public extern unsafe static int vbzdec32(byte[] @in, int n, int[] @out, int start);

        // variable simple
        [DllImport(LibraryName)]
        public extern unsafe static int vsenc32(int[] @in, int n, byte[] @out);
        [DllImport(LibraryName)]
        public extern unsafe static int vsdec32(byte[] @in, int n, int[] @out);

        //************ TurboPFor: PFor/PForDelta
        // High level API: n unlimited
        [DllImport(LibraryName)]
        public extern unsafe static int p4nenc32(int[] @in, int n, byte[] @out);
        [DllImport(LibraryName)]
        public extern unsafe static int p4ndec32(byte[] @in, int n, int[] @out);
        [DllImport(LibraryName)]
        public extern unsafe static int p4nenc128v32(int[] @in, int n, byte[] @out);
        [DllImport(LibraryName)]
        public extern unsafe static int p4ndec128v32(byte[] @in, int n, int[] @out);
        [DllImport(LibraryName)]
        public extern unsafe static int p4nenc256v32(int[] @in, int n, byte[] @out);
        [DllImport(LibraryName)]
        public extern unsafe static int p4ndec256v32(byte[] @in, int n, int[] @out);

        [DllImport(LibraryName)]
        public extern unsafe static int p4ndenc32(int[] @in, int n, byte[] @out); // delta 0: increasing integer list (sorted)
        [DllImport(LibraryName)]
        public extern unsafe static int p4nddec32(byte[] @in, int n, int[] @out);
        [DllImport(LibraryName)]
        public extern unsafe static int p4ndenc128v32(int* @in, int n, byte* @out);
        [DllImport(LibraryName)]
        public extern unsafe static int p4nddec128v32(byte* @in, int n, int* @out);
        [DllImport(LibraryName)]
        public extern unsafe static int p4ndenc256v32(int* @in, int n, byte* @out);
        [DllImport(LibraryName)]
        public extern unsafe static int p4nddec256v32(byte* @in, int n, int* @out);

        [DllImport(LibraryName)]
        public extern unsafe static int p4nd1enc32(int[] @in, int n, byte[] @out); // delta 1: strictly increasing integer list (sorted)
        [DllImport(LibraryName)]
        public extern unsafe static int p4nd1dec32(byte[] @in, int n, int[] @out);
        [DllImport(LibraryName)]
        public extern unsafe static int p4nd1enc128v32(int* @in, int n, byte* @out);
        [DllImport(LibraryName)]
        public extern unsafe static int p4nd1dec128v32(byte* @in, int n, int* @out);
        [DllImport(LibraryName)]
        public extern unsafe static int p4nd1enc256v32(int* @in, int n, byte* @out);
        [DllImport(LibraryName)]
        public extern unsafe static int p4nd1dec256v32(byte* @in, int n, int* @out);

        [DllImport(LibraryName)]
        public extern unsafe static int p4nzenc32(int[] @in, int n, byte[] @out); // zigzag: unsorted integer list
        [DllImport(LibraryName)]
        public extern unsafe static int p4nzdec32(byte[] @in, int n, int[] @out);
        [DllImport(LibraryName)]
        public extern unsafe static int p4nzenc128v32(int[] @in, int n, byte[] @out);
        [DllImport(LibraryName)]
        public extern unsafe static int p4nzdec128v32(byte[] @in, int n, int[] @out);
        [DllImport(LibraryName)]
        public extern unsafe static int p4nzenc256v32(int[] @in, int n, byte[] @out);
        [DllImport(LibraryName)]
        public extern unsafe static int p4nzdec256v32(byte[] @in, int n, int[] @out);

        // Single block: n limited to 128/256
        [DllImport(LibraryName)]
        public extern unsafe static int p4enc32(int[] @in, int n, byte[] @out);
        [DllImport(LibraryName)]
        public extern unsafe static int p4dec32(byte[] @in, int n, int[] @out);

        [DllImport(LibraryName)]
        public extern unsafe static int p4enc128v32(int[] @in, int n, byte[] @out);
        [DllImport(LibraryName)]
        public extern unsafe static int p4dec128v32(byte[] @in, int n, int[] @out);
        [DllImport(LibraryName)]
        public extern unsafe static int p4enc256v32(int[] @in, int n, byte[] @out);
        [DllImport(LibraryName)]
        public extern unsafe static int p4dec256v32(byte[] @in, int n, int[] @out);

        [DllImport(LibraryName)]
        public extern unsafe static int p4ddec32(byte[] @in, int n, int[] @out, int start);
        [DllImport(LibraryName)]
        public extern unsafe static int p4ddec128v32(byte[] @in, int n, int[] @out, int start);
        [DllImport(LibraryName)]
        public extern unsafe static int p4ddec256v32(byte[] @in, int n, int[] @out, int start);

        [DllImport(LibraryName)]
        public extern unsafe static int p4d1dec32(byte[] @in, int n, int[] @out, int start);
        [DllImport(LibraryName)]
        public extern unsafe static int p4d1dec128v32(byte[] @in, int n, int[] @out, int start);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public extern unsafe static byte* p4d1enc256v32(int* @in, int n, byte* @out, int start);
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public extern unsafe static byte* p4d1dec256v32(byte* @in, int n, int* @out, int start);

        //********** bitpack scalar
        // High level API
        [DllImport(LibraryName)]
        public extern unsafe static int bitnpack32(int[] @in, int n, byte[] @out);
        [DllImport(LibraryName)]
        public extern unsafe static int bitnunpack32(byte[] @in, int n, int[] @out);
        [DllImport(LibraryName)]
        public extern unsafe static int bitnpack128v32(int[] @in, int n, byte[] @out);
        [DllImport(LibraryName)]
        public extern unsafe static int bitnunpack128v32(byte[] @in, int n, int[] @out);
        [DllImport(LibraryName)]
        public extern unsafe static int bitnpack256v32(int[] @in, int n, byte[] @out);
        [DllImport(LibraryName)]
        public extern unsafe static int bitnunpack256v32(byte[] @in, int n, int[] @out);

        [DllImport(LibraryName)]
        public extern unsafe static int bitndpack32(int[] @in, int n, byte[] @out);
        [DllImport(LibraryName)]
        public extern unsafe static int bitndunpack32(byte[] @in, int n, int[] @out);
        [DllImport(LibraryName)]
        public extern unsafe static int bitndpack128v32(int[] @in, int n, byte[] @out);
        [DllImport(LibraryName)]
        public extern unsafe static int bitndunpack128v32(byte[] @in, int n, int[] @out);
        [DllImport(LibraryName)]
        public extern unsafe static int bitndpack256v32(int[] @in, int n, byte[] @out);
        [DllImport(LibraryName)]
        public extern unsafe static int bitndunpack256v32(byte[] @in, int n, int[] @out);

        [DllImport(LibraryName)]
        public extern unsafe static int bitnd1pack32(int[] @in, int n, byte[] @out);
        [DllImport(LibraryName)]
        public extern unsafe static int bitnd1unpack32(byte[] @in, int n, int[] @out);
        [DllImport(LibraryName)]
        public extern unsafe static int bitnd1pack128v32(int[] @in, int n, byte[] @out);
        [DllImport(LibraryName)]
        public extern unsafe static int bitnd1unpack128v32(byte[] @in, int n, int[] @out);
        [DllImport(LibraryName)]
        public extern unsafe static int bitnd1pack256v32(int[] @in, int n, byte[] @out);
        [DllImport(LibraryName)]
        public extern unsafe static int bitnd1unpack256v32(byte[] @in, int n, int[] @out);

        // Low level API - single block limited to 128/256 integers
        [DllImport(LibraryName)]
        public extern unsafe static int bitpack32(int[] @in, int n, byte[] @out, int b);
        [DllImport(LibraryName)]
        public extern unsafe static int bitunpack32(byte[] @in, int n, int[] @out, int b);
        [DllImport(LibraryName)]
        public extern unsafe static int bitpack128v32(int[] @in, int n, byte[] @out, int b);
        [DllImport(LibraryName)]
        public extern unsafe static int bitunpack128v32(byte[] @in, int n, int[] @out, int b);
        [DllImport(LibraryName)]
        public extern unsafe static int bitpack256v32(int[] @in, int n, byte[] @out, int b);
        [DllImport(LibraryName)]
        public extern unsafe static int bitunpack256v32(byte[] @in, int n, int[] @out, int b);

        [DllImport(LibraryName)]
        public extern unsafe static int bitdpack32(int[] @in, int n, byte[] @out, int start, int b);
        [DllImport(LibraryName)]
        public extern unsafe static int bitdunpack32(byte[] @in, int n, int[] @out, int start, int b);
        [DllImport(LibraryName)]
        public extern unsafe static int bitdpack128v32(int[] @in, int n, byte[] @out, int start, int b);
        [DllImport(LibraryName)]
        public extern unsafe static int bitdunpack128v32(byte[] @in, int n, int[] @out, int start, int b);
        [DllImport(LibraryName)]
        public extern unsafe static int bitdpack256v32(int[] @in, int n, byte[] @out, int start, int b);
        [DllImport(LibraryName)]
        public extern unsafe static int bitdunpack256v32(byte[] @in, int n, int[] @out, int start, int b);

        [DllImport(LibraryName)]
        public extern unsafe static int bitd1pack32(int[] @in, int n, byte[] @out, int start, int b);
        [DllImport(LibraryName)]
        public extern unsafe static int bitd1unpack32(byte[] @in, int n, int[] @out, int start, int b);
        [DllImport(LibraryName)]
        public extern unsafe static int bitd1pack128v32(int[] @in, int n, byte[] @out, int start, int b);
        [DllImport(LibraryName)]
        public extern unsafe static int bitd1unpack128v32(byte[] @in, int n, int[] @out, int start, int b);
        [DllImport(LibraryName)]
        public extern unsafe static int bitd1pack256v32(int[] @in, int n, byte[] @out, int start, int b);
        [DllImport(LibraryName)]
        public extern unsafe static int bitd1unpack256v32(byte[] @in, int n, int[] @out, int start, int b);

        // bitutil
        [DllImport(LibraryName)]
        public extern unsafe static int bit32(int[] @in, int n);
        [DllImport(LibraryName)]
        public extern unsafe static int bitd32(int[] @in, int n, int start);
        [DllImport(LibraryName)]
        public extern unsafe static int bitd132(int[] @in, int n, int start);


        private static IntPtr OnResolvingUnmanagedDll(Assembly context, string name)
        {
            if (_libraryFallbackPath is null)
            {
                string platform;

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    name = $"lib{name}.so";
                    platform = "linux";
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    name = $"lib{name}.dylib";
                    platform = "osx";
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    name = $"{name}.dll";
                    platform = "win";
                }
                else
                    throw new PlatformNotSupportedException();

                var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

                _libraryFallbackPath = Path.Combine("runtimes", $"{platform}-{arch}", "native", name);
            }

            return NativeLibrary.Load(_libraryFallbackPath, context, default);
        }
    }
}
