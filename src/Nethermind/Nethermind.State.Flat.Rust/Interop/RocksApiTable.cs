// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using System.Text;

namespace Nethermind.State.Flat.Rust.Interop;

/// <summary>
/// The <c>RocksApi</c> function table of native/crates/flatdb-core/src/kv/rocks_api.rs: a header of the ABI version
/// and the function count, then the address of every RocksDB C API function the native library lists, in its order.
/// </summary>
/// <remarks>
/// The addresses come from the one librocksdb the process mapped when C# opened a database, never from a library
/// looked up by name: the bindings resolve their library through their own resolver, so a name lookup could find
/// a second copy, and a database must only be driven by the code that opened it. The table and the library stay
/// loaded for the life of the process.
/// </remarks>
internal sealed unsafe class RocksApiTable(nint pointer)
{
    private const string LibraryFilePrefix = "librocksdb";

    private static readonly Lock _lock = new();
    private static RocksApiTable? _shared;

    /// <summary>The table of the librocksdb of this process, built on first use; a RocksDB database must be open by then.</summary>
    /// <exception cref="InvalidOperationException">No or several librocksdb are mapped, or one misses a function.</exception>
    public static RocksApiTable Shared
    {
        get
        {
            lock (_lock)
            {
                return _shared ??= Create(FindLoadedLibrary(File.ReadLines("/proc/self/maps")));
            }
        }
    }

    public nint Pointer => pointer;

    /// <summary>Size of a table with <paramref name="functionCount"/> functions.</summary>
    public static int SizeOf(int functionCount) => 2 * sizeof(uint) + functionCount * sizeof(nint);

    /// <summary>The path of the single librocksdb in <paramref name="maps"/>, the lines of <c>/proc/self/maps</c>.</summary>
    /// <exception cref="InvalidOperationException">No librocksdb, or more than one, is mapped.</exception>
    public static string FindLoadedLibrary(IEnumerable<string> maps)
    {
        HashSet<string> paths = [];
        foreach (string line in maps)
        {
            // address perms offset dev inode pathname: the path is the sixth field and may contain spaces.
            string[] fields = line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 6) continue;

            string path = fields[5].Trim();
            if (Path.GetFileName(path).StartsWith(LibraryFilePrefix, StringComparison.Ordinal)) paths.Add(path);
        }

        return paths.Count switch
        {
            1 => paths.First(),
            0 => throw new InvalidOperationException($"No {LibraryFilePrefix} is loaded; open a RocksDB database before the Rust flat DB backend."),
            _ => throw new InvalidOperationException($"Several {LibraryFilePrefix} are loaded ({string.Join(", ", paths)}); the Rust flat DB backend cannot tell which one owns the database."),
        };
    }

    private static RocksApiTable Create(string libraryPath)
    {
        nint library = NativeLibrary.Load(libraryPath);
        int count = (int)NativeMethods.fdb_rocks_api_symbol_count();
        uint* table = (uint*)NativeMemory.AllocZeroed((nuint)SizeOf(count));
        try
        {
            table[0] = FfiAbi.Version;
            table[1] = (uint)count;

            nint* functions = (nint*)(table + 2);
            for (int i = 0; i < count; i++)
            {
                string symbol = SymbolName(i);
                if (!NativeLibrary.TryGetExport(library, symbol, out functions[i]))
                {
                    throw new InvalidOperationException($"{libraryPath} does not export {symbol}, which the Rust flat DB backend needs.");
                }
            }

            return new RocksApiTable((nint)table);
        }
        catch
        {
            NativeMemory.Free(table);
            throw;
        }
    }

    private static string SymbolName(int index)
    {
        byte* name;
        nuint length;
        StatusMap.ThrowIfFailed(NativeMethods.fdb_rocks_api_symbol((uint)index, &name, &length));
        return Encoding.ASCII.GetString(name, (int)length);
    }
}
