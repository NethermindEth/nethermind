using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Nethermind.Db
{
    /// <summary>
    /// A memory mapped map.
    /// </summary>
    public class MemoryMappedKeyValueStore : IDisposable
    {
        public const int KeyLength = KeccakLength;
        const int MaxNumberOfFiles = 2048;

        const int KeccakLength = 32;
        const int BitsInByte = 8;

        readonly string _dir;
        readonly int _keyPrefixBytesCount;
        readonly int _pageSize;
        readonly List<MemoryMappedFile> _files = new();

        readonly Map[] _maps = new Map[MaxNumberOfFiles];
        private volatile int _mapCount;

        private readonly ConcurrentQueue<WriteBatch> _batches = new();

        private Thread _flusher;
        private volatile bool _runFlusher;

        /// <summary>
        /// Configures the key value store to store data in a specific directory with a specific page size. The store will generate files big as 16MB * pageSize, which for 4k gives 64 GBs of a single file.
        /// </summary>
        /// <param name="directoryPath">The path where the database files will be located. Both jump table and the log files are included in there.</param>
        public MemoryMappedKeyValueStore(string directoryPath, int keyPrefixBytesCount, int pageSize)
        {
            _dir = directoryPath;
            _keyPrefixBytesCount = keyPrefixBytesCount;
            _pageSize = pageSize;
        }

        public void Initialize()
        {
            if (!Directory.Exists(_dir))
            {
                Directory.CreateDirectory(_dir);
            }

            string[] existingFiles = Directory.GetFiles(_dir);
            if (existingFiles.Length > 0)
            {
                foreach (string file in existingFiles)
                {
                    Map map = InitializeMap(file, _keyPrefixBytesCount, _pageSize);
                    _maps[map.Number] = map;
                }

                _mapCount = _maps.Count(m => m != null);
            }
            else
            {
                // first file
                Map map = CreateFile(0);
                _maps[map.Number] = map;
                _mapCount = 1;
            }

            _runFlusher = true;
            _flusher = new Thread(RunFlusher);
            _flusher.Start();
        }

        private void RunFlusher()
        {
            bool TryFlushDirtyMaps()
            {
                Queue<Map> toFlush = new(_maps.Where((map, index) => index < _mapCount && map.ShouldFlush));
                if (toFlush.Count == 0)
                {
                    return false;
                }

                // spin till all files are flushed
                while (toFlush.TryDequeue(out Map map))
                {
                    if (Monitor.TryEnter(map))
                    {
                        map.Flush();
                        Monitor.Exit(map);
                    }
                    else
                    {
                        toFlush.Enqueue(map);
                    }
                }

                return true;
            }

            while (_runFlusher)
            {
                if (!TryFlushDirtyMaps())
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(100));
                }
            }

            TryFlushDirtyMaps();
        }

        public bool HasNoEntriesToFlush => _batches.IsEmpty;

        Map CreateFile(int fileNumber)
        {
            return InitializeMap(Path.Combine(_dir, $"{fileNumber:D5}"), _keyPrefixBytesCount, _pageSize, true);
        }

        Map InitializeMap(string file, int keyPrefixBytesCount, int pageSize, bool clear = false)
        {
            FileStream stream = File.Open(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            if (stream.Length == 0)
            {
                // new one needs to be initialized
                stream.SetLength(Map.GetTotalFileSize(keyPrefixBytesCount, pageSize));
                stream.WriteByte(0); // this is sufficient to create an empty file.
                stream.Flush(true);
            }

            MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, false);
            _files.Add(mmf);

            string justNumber = new FileInfo(file).Name;
            int.TryParse(justNumber, out int number);
            Map map = new(stream, mmf, _keyPrefixBytesCount, _pageSize, number);
            map.Initialize(clear);

            return map;
        }

        private void Commit(WriteBatch batch)
        {
            _batches.Enqueue(batch);

            lock (_batches)
            {
                if (batch._isCommitted)
                {
                    return; // this batch is already committed
                }

                HashSet<Map> locks = new();

                while (_batches.TryPeek(out WriteBatch commit))
                {
                    foreach ((byte[] key, byte[] value) in commit.Pairs)
                    {
                        // from the oldest, to the newest, try write
                        int i = 0;
                        for (; i < _mapCount; i++)
                        {
                            Map map = _maps[i];
                            
                            if (!map.CanWrite(key, value))
                            {
                                // no place, spin again
                                continue;
                            }
                            
                            if (!locks.Contains(map))
                            {
                                // TODO: recovery and possibly skip map
                                if (Monitor.TryEnter(map))
                                {
                                    locks.Add(map);
                                }
                                else
                                {
                                    // no lock taken, move to next
                                    continue;
                                }
                            }
                            
                            // lock taken, try write and break on success
                            if (map.TryWrite(key, value))
                            {
                                break;
                            }
                        }
                        
                        if (i == _mapCount)
                        {
                            Map map = _maps[_mapCount] = CreateFile(_mapCount);
                            _mapCount = _mapCount + 1;

                            Monitor.Enter(map);
                            locks.Add(map);

                            map.TryWrite(key, value);
                        }
                    }

                    _batches.TryDequeue(out _); // dequeue the current that was Peeked at the beginning
                    commit._isCommitted = true; // mark this as committed
                }

                // release the locks
                foreach (Map @lock in locks)
                {
                    Monitor.Exit(@lock);
                }
            }
        }

      
        public void Delete(byte[] key)
        {
            using WriteBatch batch = new(this);
            batch.Delete(key);
            batch.Commit();
        }

        public void Put(byte[] key, byte[] value)
        {
            using WriteBatch batch = new(this);
            batch.Put(key, value);
            batch.Commit();
        }

        public bool KeyExists(byte[] key)
        {
            for (int i = _mapCount - 1; i >= 0; i--)
            {
                if (_maps[i].KeyExists(key))
                {
                    return true;
                }
            }

            return false;
        }
        
        public bool TryGet(byte[] key, out Span<byte> value)
        {
            for (int i = _mapCount - 1; i >= 0; i--)
            {
                if (_maps[i].TryGet(key, out value))
                {
                    return true;
                }
            }

            value = default;
            return false;
        }

        public void Dispose()
        {
            _runFlusher = false;
            _flusher.Join();

            foreach (Map map in _maps)
            {
                map?.Dispose();
            }

            foreach (MemoryMappedFile disposable in _files)
            {
                disposable.Dispose();
            }
        }

        /// <summary>
        /// A single memory-mapped file.
        /// </summary>
        /// <remarks>
        /// A single page grows in two directions:
        /// - from the beginning in chunks of [keysuffix][short: value lenght]
        /// - from the end with values
        ///
        /// This should allow faster scans for the specific key 
        /// </remarks>
        class Map : IDisposable
        {
            readonly FileStream _file;
            readonly MemoryMappedFile _mmf;
            readonly int _prefix;
            readonly int _pageSize;
            readonly int _headerSize;
            readonly MemoryMappedViewAccessor _accessor;
            volatile bool _shouldFlush;

            public int Number { get; }
            IntPtr _pointer;

            /// <summary>
            /// The number of bytes needed for each head entry.
            /// </summary>
            readonly int _headEntryLength;

            /// <summary>
            /// The actual number of bytes stored for each key.
            /// </summary>
            readonly int _keySuffixLength;

            const int ValuePrefixCountBytesCount = sizeof(short);
            
            /// <summary>
            /// The size of a single item of the header.
            /// </summary>
            const int HeaderItemSize = sizeof(int);

            /// <summary>
            /// Due to encoding of the header with <see cref="DecodeHeader"/> and <see cref="EncodeHeader"/>.
            /// </summary>
            private const int MaxPageSize = ushort.MaxValue;

            public static long GetTotalFileSize(int keyPrefixBytesCount, int pageSize)
            {
                long prefixCount = GetPrefixesCount(keyPrefixBytesCount);

                return prefixCount * HeaderItemSize + // header of the file, containing offset for each page
                       prefixCount * pageSize;      // pages
            }

            private static int GetPrefixesCount(int keyPrefixBytesCount) => 1 << (keyPrefixBytesCount * BitsInByte);

            public Map(FileStream file, MemoryMappedFile mmf, int prefix, int pageSize,  int number)
            {
                if (pageSize > MaxPageSize)
                {
                    throw new ArgumentException();
                }
                
                _file = file;
                _mmf = mmf;
                _prefix = prefix;
                _accessor = mmf.CreateViewAccessor();
                _headerSize = GetPrefixesCount(prefix) * HeaderItemSize;
                _pageSize = pageSize;
                Number = number;
                _keySuffixLength = KeyLength - _prefix;
                _headEntryLength = _keySuffixLength + ValuePrefixCountBytesCount;
            }

            public void Dispose()
            {
                if (_pointer != IntPtr.Zero)
                {
                    _pointer = IntPtr.Zero;
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }

                _accessor.Dispose();
                _mmf.Dispose();
                _file.DisposeAsync();
            }

            /// <summary>
            /// Initializes the accessor by scanning its content and setting the offset properly.
            /// </summary>
            public unsafe void Initialize(bool clear)
            {
                byte* ptr = null;
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                _pointer = new IntPtr(ptr);

                if (clear)
                {
                    // clear just headers, it's enough
                    new Span<byte>(ptr, _headerSize).Clear();
                    Flush(true);
                }
            }

            public unsafe bool CanWrite(byte[] key, byte[] value)
            {
                ref uint header = ref GetPageInfo(key, out byte* _);

                (ushort _, ushort written) = DecodeHeader(ref header);

                int leftover = _pageSize - written;
                uint neededBytes = CalculateNeededBytes(key, value);
                
                return leftover >= neededBytes;
            }

            public unsafe bool TryWrite(byte[] key, byte[] value)
            {
                ref uint header = ref GetPageInfo(key, out byte* pageStart);

                (ushort entriesCount, ushort written) = DecodeHeader(ref header);

                int leftover = _pageSize - written;
                uint neededBytes = CalculateNeededBytes(key, value);
                if (leftover >= neededBytes)
                {
                    ushort valueLength = (ushort)value.Length;

                    // write head entry: keySuffix
                    int existingHeadSpace = _headEntryLength * entriesCount;
                    byte* headStart = pageStart + existingHeadSpace;
                    Span<byte> head = new(headStart, _headEntryLength);
                    key.AsSpan(_prefix).CopyTo(head);

                    // write head entry: value length
                    Unsafe.WriteUnaligned(headStart + _keySuffixLength, valueLength);

                    // write tail entry: value content
                    int offset = _pageSize - (written - existingHeadSpace) - valueLength;
                    Span<byte> destination = new(pageStart + offset, valueLength);
                    value.CopyTo(destination);

                    // make the value visible by writing the header with the volatile.
                    Volatile.Write(ref header, EncodeHeader((ushort)(entriesCount + 1), (ushort)(written + neededBytes)));

                    _shouldFlush = true;
                    
                    return true;
                }

                return false;
            }

            // Probably no need for optimizations. TryGet is zero-copy and the only overhead is the creation of a Span.
            public bool KeyExists(byte[] key) => TryGet(key, out _);

            public unsafe bool TryGet(Span<byte> key, out Span<byte> value)
            {
                uint header = GetPageInfo(key, out byte* pageStart);

                (ushort entriesCount, ushort _) = DecodeHeader(ref header);

                if (entriesCount == 0)
                {
                    value = default;
                    return false;
                }

                // optimization for searches, to compare 8 bytes of the key stored in the page, than, if matched, search for more
                Span<byte> suffix = key.Slice(_prefix);
                
                ulong searched = Unsafe.ReadUnaligned<ulong>(ref suffix[0]);

                uint valueLengthAccumulated = 0;
                byte* search = pageStart;
                for (int i = 0; i < entriesCount; i++)
                {
                    ulong current = Unsafe.ReadUnaligned<ulong>(search);
                    ushort valueLength = Unsafe.ReadUnaligned<ushort>(search + _keySuffixLength);
                    valueLengthAccumulated += valueLength;
                    
                    if (current == searched)
                    {
                        if (suffix.SequenceEqual(new ReadOnlySpan<byte>(search, _keySuffixLength)))
                        {
                            // found! jump to the value
                            value = new Span<byte>(pageStart + _pageSize - valueLengthAccumulated, valueLength);
                            return true;
                        }
                    }

                    search += _headEntryLength;
                }

                value = default;
                return false;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private int GetPageNumber(Span<byte> key) => (int)(ReadMachineSpecificUnaligned(key) >> ((sizeof(uint) - _prefix) * BitsInByte));

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static (ushort entriesCount, ushort written) DecodeHeader(ref uint header)
            {
                uint current = Volatile.Read(ref header);
                return ((ushort)(current >> 16), (ushort)current);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static uint EncodeHeader(ushort entriesCount, ushort written) => (((uint)entriesCount) << 16) | written ;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static uint ReadMachineSpecificUnaligned(Span<byte> key) => Unsafe.ReadUnaligned<uint>(ref MemoryMarshal.GetReference(key));

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private uint CalculateNeededBytes(byte[] key, byte[] value) => (uint) (key.Length + value.Length - _prefix + ValuePrefixCountBytesCount);

            private unsafe ref uint GetPageInfo(Span<byte> key, out byte* pageStart)
            {
                int pageNo = GetPageNumber(key);
                byte* pointer = (byte*) _pointer.ToPointer();
                ref uint start = ref Unsafe.AsRef<uint>(pointer);
                ref uint header = ref Unsafe.Add(ref start, pageNo);
                pageStart = pointer + _headerSize + (pageNo * _pageSize);
                return ref header;
            }

            public void Flush(bool force = false)
            {
                if (_shouldFlush || force)
                {
                    // flush and update
                    _accessor.Flush();

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        _file.Flush(true);
                    }
                    
                    _shouldFlush = false;
                }
            }

            public bool ShouldFlush => _shouldFlush;
        }

        public IWriteBatch StartBatch() => new WriteBatch(this);

        public interface IWriteBatch : IDisposable
        {
            void Put(byte[] key, byte[] value);
            void Delete(byte[] key);
            void Commit();
        }

        class WriteBatch : IWriteBatch
        {
            private readonly MemoryMappedKeyValueStore _store;

            private static readonly byte[] s_empty = new byte[0];
            public bool _isCommitted;

            private List<(byte[], byte[])> _pairs = new();

            public WriteBatch(MemoryMappedKeyValueStore store)
            {
                _store = store;
            }

            public void Put(byte[] key, byte[] value)
            {
                if (key.Length != KeyLength)
                {
                    throw new ArgumentException($"The key should be {KeyLength} long", nameof(key));
                }

                if (value.Length > 1024)
                {
                    throw new ArgumentException($"The value length {value.Length} breaches the max size of {1024} bytes, which is over a half page long.", nameof(value));
                }

                _pairs.Add((key, value));
            }

            public IEnumerable<(byte[], byte[])> Pairs => _pairs;

            public void Commit()
            {
                if (_pairs != null)
                {
                    _store.Commit(this);
                    _pairs = null;
                }
            }

            public void Delete(byte[] key) => Put(key, null);

            public void Dispose()
            {
            }
        }
    }
}
