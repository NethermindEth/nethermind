using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;

namespace Nethermind.Db
{
    /// <summary>
    /// A memory mapped map. It keeps the data in a concurrent dictionary, trying to flush them as soon as possible.
    /// </summary>
    /// <remarks>
    /// A single log entry consists of:
    /// - 8 bytes header (ulong)
    ///  - 2 high bytes for the length of value
    ///  - 6 low bytes for the jumps address
    /// - 32 bytes of Keccak
    /// - N bytes of the value (aligned to 8 byte boundary)
    /// </remarks>
    public class MemoryMappedKeyValueStore : IDisposable
    {
        public const int KeyLength = KeccakLength;
        const int MaxNumberOfFiles = 2048;

        const int PrefixBytesCount = 3;

        const int KeccakLength = 32;
        const int BitsInByte = 8;

        readonly string _dir;
        private readonly int _pageSize;
        private readonly long _fileSize;
        readonly List<MemoryMappedFile> _files = new();

        private readonly HashSet<Map> _dirtyMaps = new();
        readonly Map[] _maps = new Map[MaxNumberOfFiles];
        private volatile int _mapCount;

        private readonly ConcurrentQueue<WriteBatch> _batches = new();

        private readonly AutoResetEvent _wait = new(false);

        private Thread _flusher;
        private volatile bool _runFlusher;

        /// <summary>
        /// Configures the key value store.
        /// </summary>
        /// <param name="directoryPath">The path where the database files will be located. Both jump table and the log files are included in there.</param>
        /// <param name="pageSize">The size of the page that stores values prefixed with the same </param>
        public MemoryMappedKeyValueStore(string directoryPath, int pageSize = 4 * 1024)
        {
            _dir = directoryPath;
            _pageSize = pageSize;
            _fileSize = (1L << (PrefixBytesCount * 8)) * pageSize;
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
                    Map map = InitializeMap(file, _fileSize);
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
            void FlushAllDirtyPages()
            {
                // use lock just to find dirty pages, flush asynchronously
                Map[] toFlush;
                lock (_dirtyMaps)
                {
                    toFlush = _dirtyMaps.ToArray();
                    _dirtyMaps.Clear();
                }

                foreach (Map map in toFlush)
                {
                    map.Flush();
                }
            }

            while (_runFlusher)
            {
                _wait.WaitOne(TimeSpan.FromSeconds(1));
                FlushAllDirtyPages();
            }

            FlushAllDirtyPages();
        }

        public bool HasNoEntriesToFlush => _batches.IsEmpty;

        Map CreateFile(int fileNumber)
        {
            return InitializeMap(Path.Combine(_dir, $"{fileNumber:D5}"), _fileSize);
        }

        Map InitializeMap(string file, long size)
        {
            FileStream stream = File.Open(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            if (stream.Length == 0)
            {
                // new one needs to be initialized
                stream.SetLength(size);
                stream.WriteByte(0); // this is sufficient to create an empty file.
                stream.Flush(true);
            }

            MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, false);
            _files.Add(mmf);

            string justNumber = new FileInfo(file).Name;
            int.TryParse(justNumber, out int number);
            Map map = new(stream, mmf, file, number, _pageSize);
            map.Initialize();

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

                Map
                    map = _maps[
                        _mapCount -
                        1]; // use last map for writing. This could result in using files in an suboptimal way as there might be some pages that were not filled totally.

                HashSet<Map> dirty = new();

                while (_batches.TryPeek(out WriteBatch commit))
                {
                    foreach ((byte[] key, byte[] value) in commit.Pairs)
                    {
                        int pageNo = GetPageNumber(key);

                        Page page = map.GetPage(pageNo);
                        if (!page.TryWrite(key, value))
                        {
                            _maps[_mapCount] = map = CreateFile(_mapCount);
                            _mapCount = _mapCount + 1;

                            map.GetPage(pageNo).TryWrite(key, value);
                        }

                        dirty.Add(map);
                    }

                    _batches.TryDequeue(out _); // dequeue the current that was Peeked at the beginning
                    commit._isCommitted = true; // mark this as committed
                }

                if (dirty.Count > 0)
                {
                    // mark as dirty
                    lock (_dirtyMaps)
                    {
                        _dirtyMaps.UnionWith(dirty);
                    }

                    _wait.Set();
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetPageNumber(Span<byte> key) => (int)(BinaryPrimitives.ReadUInt32LittleEndian(key) >> ((sizeof(uint) - PrefixBytesCount) * BitsInByte));

        public void Delete(byte[] key)
        {
            using WriteBatch batch = new(this);
            batch.Delete(key);
            batch.Commit();
        }

        public void Set(byte[] key, byte[] value)
        {
            using WriteBatch batch = new(this);
            batch.Put(key, value);
            batch.Commit();
        }

        public bool TryGet(byte[] key, out Span<byte> value)
        {
            Span<byte> keySuffix = key.AsSpan(PrefixBytesCount);

            int pageNumber = GetPageNumber(key);

            for (int i = _mapCount - 1; i >= 0; i--)
            {
                Page page = _maps[i].GetPage(pageNumber);
                if (page.TryGet(keySuffix, out value))
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

        class Map : IDisposable
        {
            readonly FileStream _file;
            readonly MemoryMappedFile _mmf;
            private readonly int _pageSize;
            readonly MemoryMappedViewAccessor _accessor;

            public string Path { get; }
            public int Number { get; }

            IntPtr _pointer;

            public Map(FileStream file, MemoryMappedFile mmf, string path, int number, int pageSize)
            {
                _file = file;
                _mmf = mmf;
                _pageSize = pageSize;
                _accessor = mmf.CreateViewAccessor();
                Path = path;
                Number = number;
            }

            public void Dispose()
            {
                if (_pointer != IntPtr.Zero)
                {
                    _pointer = IntPtr.Zero;
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }

                _accessor?.Dispose();
                _mmf.Dispose();
                _file.DisposeAsync();
            }

            /// <summary>
            /// Initializes the accessor by scanning its content and setting the offset properly.
            /// </summary>
            public unsafe void Initialize()
            {
                // scan to find last written
                byte* ptr = null;
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                _pointer = new IntPtr(ptr);
            }

            public void Flush()
            {
                // flush and update
                _accessor.Flush();
                _file.Flush(true);
            }

            public unsafe Page GetPage(int pageNo) => new(((byte*)_pointer.ToPointer()) + (pageNo * _pageSize), _pageSize);
        }

        /// <summary>
        /// A single page represents a chunk of memory that is used to write and read entries with the common key prefix.
        /// This should align well with memory mapped approach and reduce the number of hops needed.
        /// </summary>
        /// <remarks>
        /// The current implementation uses the following structure:
        /// - 8 bytes - header, used to write how many bytes were written to the page
        /// - entries consisting of:
        ///   - <see cref="SuffixLength"/> bytes for the key
        ///   - <see cref="ValuePrefixCountBytesCount"/> for the value prefix
        ///   - N bytes for the actual value
        /// </remarks>
        readonly unsafe struct Page
        {
            // TODO: potential improvements:
            // 1. scans could be faster if keys were stored at the beginning of the page and values at the end. This would allow to scan through the keys without getting prefixes and dealing with complex jump arithmetic

            const int ValuePrefixCountBytesCount = sizeof(short);
            const int SuffixLength = KeyLength - PrefixBytesCount;
            const int PageHeaderSize = 8;

            private readonly byte* _payload;
            private readonly int _pageSize;

            public Page(byte* payload, int pageSize)
            {
                _payload = payload;
                _pageSize = pageSize;
            }

            public bool TryWrite(byte[] key, byte[] value)
            {
                int neededBytes = key.Length + value.Length - PrefixBytesCount + ValuePrefixCountBytesCount;

                ref long header = ref Unsafe.AsRef<long>(_payload);

                if (_pageSize - header - PageHeaderSize >= neededBytes)
                {
                    Span<byte> destination = new(_payload + header + PageHeaderSize, _pageSize - PageHeaderSize);

                    // key first, beside first PrefixBytesCount bytes
                    key.AsSpan(PrefixBytesCount).CopyTo(destination);
                    destination = destination.Slice(KeyLength - PrefixBytesCount);

                    // value length as short
                    BinaryPrimitives.WriteInt16LittleEndian(destination, (short)value.Length);
                    destination = destination.Slice(ValuePrefixCountBytesCount);

                    // value itself
                    value.CopyTo(destination);

                    // make the value visible by writing the header with the volatile.
                    Volatile.Write(ref header, header + neededBytes);

                    return true;
                }

                return false;
            }

            public bool TryGet(in Span<byte> keySuffix, out Span<byte> value)
            {
                long used = Volatile.Read(ref Unsafe.AsRef<long>(_payload));

                Span<byte> bytes = new(_payload + PageHeaderSize, (int)used);

                while (!bytes.IsEmpty)
                {
                    short valueLength = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(SuffixLength));
                    if (bytes.StartsWith(keySuffix))
                    {
                        value = bytes.Slice(SuffixLength + ValuePrefixCountBytesCount, valueLength);
                        return true;
                    }

                    // jump over the value to the next
                    bytes = bytes.Slice(SuffixLength + ValuePrefixCountBytesCount + valueLength);
                }

                value = default;
                return false;
            }
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
