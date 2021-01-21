using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

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
        public const string Prefix = "log_";
        public const string JumpsFileName = "jumps";
        public const int KeyLength = KeccakLength;
        const int MaxValueLength = NullValueLength - 1;
        const int NullValueLength = short.MaxValue;
        const int MaxNumberOfFiles = 2048;
        const byte EndMarker = 0; // a marker that when written as the first byte of the length in a file marks it as 0

        /// <summary>
        /// This is an important settings as it heavily affects the size of the jump file and the lookup speed.
        /// The bigger it is, the bigger the jump file, the more buckets for colliding keys and the smaller linked lists for each bucket.
        /// </summary>
        const int PrefixBits = 24; // 3bytes
        const int AddressSize = sizeof(ulong);
        const int JumpsCount = 1 << PrefixBits;
        const int JumpsFileSize = AddressSize * JumpsCount;
        
        const int ValueLengthShift = 6 * BitsInByte;
        const int BitsInByte = 8;

        const int ByteAlignment = 8;
        const int HeaderLength = 8;
        const int KeccakLength = 32;

        readonly string _dir;
        readonly int _logFileSize;
        readonly int _maxBatchFlushSize;
        readonly List<MemoryMappedFile> _files = new List<MemoryMappedFile>();
        readonly Map[] _maps = new Map[MaxNumberOfFiles];

        private readonly ConcurrentQueue<WriteBatch> _batches = new ConcurrentQueue<WriteBatch>();
        private readonly Dictionary<int, long> _jumpCache = new Dictionary<int, long>(1024);

        private long _flushFrom;
        private Map _jumps;
        private Thread _flusher;
        private volatile bool _runFlusher;

        public MemoryMappedKeyValueStore(string directoryPath, int logFileSize = 256 * 1024 * 1024, int maxBatchFlushSize = 10 * 1024 * 1024)
        {
            _dir = directoryPath;
            _logFileSize = logFileSize;
            _maxBatchFlushSize = maxBatchFlushSize;
        }

        public void Initialize()
        {
            if (!Directory.Exists(_dir))
            {
                Directory.CreateDirectory(_dir);
            }

            string jumpsPath = Path.Combine(_dir, JumpsFileName);
            _jumps = InitializeMap(jumpsPath, JumpsFileSize, cleanOnCreate: true);

            string[] existingFiles = Directory.GetFiles(_dir, Prefix + "*.*");
            if (existingFiles.Length > 0)
            {
                foreach (string file in existingFiles)
                {
                    Map map = InitializeMap(file, _logFileSize);
                    _maps[map.Number] = map;
                }
            }
            else
            {
                // first file
                Map map = CreateLogFile(1);
                _maps[map.Number] = map;
            }

            Map current = _maps.Last(m => m != null);
            _flushFrom = current.Number * _logFileSize + current.Offset;

            _runFlusher = true;
            _flusher = new Thread(RunFlusher);
            _flusher.Start();
        }

        private void RunFlusher()
        {
            void Flush()
            {
                lock (_batches)
                {
                    int fileNumber = GetFileAndPosition(out int position);
                    if (position > 0)
                    {
                        Map map = _maps[fileNumber];
                        map?.Flush(position);
                    }

                }
            }

            TimeSpan flushPeriod = TimeSpan.FromSeconds(5);

            while (_runFlusher)
            {
                Thread.Sleep(flushPeriod);
                Flush();
            }

            Flush();
        }

        public bool HasNoEntriesToFlush => _batches.IsEmpty;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static long BuildHeader(int valueLength, long jump) => ((long)valueLength << ValueLengthShift) | jump;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static (int valueLength, long jump) ParseHeader(long header)
        {
            const long jumpMask = (1L << ValueLengthShift) - 1;
            const int lengthMask = 0xFFFF; // should be calculated.

            return ((int)((header >> ValueLengthShift) & lengthMask), header & jumpMask);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int ReadJumpIndex(ReadOnlySpan<byte> key)
        {
            const int mask = (PrefixBits << 1) - 1;
            const int shift = 32 - PrefixBits ;
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(key);
            return (int)((value >> shift) & mask);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Align(int value) => (value + (ByteAlignment - 1)) & -ByteAlignment;

        Map CreateLogFile(int fileNumber)
        {
            return InitializeMap(Path.Combine(_dir, GetFileName(fileNumber)), _logFileSize);
        }

        static string GetFileName(int number) => $"{Prefix}{number:D6}";

        Map InitializeMap(string file, int size, bool cleanOnCreate = false)
        {
            bool created = false;
            FileStream stream = File.Open(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            if (stream.Length == 0)
            {
                // new one needs to be initialized
                stream.SetLength(size);
                stream.WriteByte(0); // this is sufficient to create an empty file.
                stream.Flush(true);
                created = true;
            }

            MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, false);
            _files.Add(mmf);

            string justNumber = new FileInfo(file).Name.Replace(Prefix, "");
            int.TryParse(justNumber, out int number);
            Map map = new Map(stream, mmf, file, number);
            map.Initialize();

            if (created && cleanOnCreate)
            {
                unsafe
                {
                    new Span<byte>(map.Pointer, size).Clear();

                    map.Flush(0);
                }
            }

            return map;
        }

        private unsafe void Commit(WriteBatch batch)
        {
            _batches.Enqueue(batch);

            lock (_batches)
            {
                if (batch._isCommitted)
                {
                    return; // this batch is already committed
                }

                Span<long> jumpTable = new Span<long>(_jumps.Pointer, JumpsCount);

                int writtenSoFar = 0;

                while (_batches.TryPeek(out WriteBatch commit) && writtenSoFar < _maxBatchFlushSize)
                {
                    int fileNumber = GetFileAndPosition(out int position);

                    ref Map file = ref _maps[fileNumber];

                    if (file == null)
                    {
                        Volatile.Write(ref file, CreateLogFile(fileNumber));
                    }

                    Span<byte> data = commit.Data;

                    int leftover = _logFileSize - position;
                    Span<byte> destination = new Span<byte>(file.Pointer + position, leftover);

                    if (data.Length > destination.Length)
                    {
                        // Leave zeros, bump up the flushFrom and re-roll the loop
                        _flushFrom += leftover;

                        // Seal up the current, it written till the limit of the file.
                        file.Flush(_logFileSize);

                        // Flush jumps only when the log is being sealed. When recovery is introduced, this will require to scan only up to one log to recover when needed. 
                        // For regular usage this should limit the flushes
                        _jumps.Flush(JumpsFileSize);

                        // spin again to peek the commit again
                        continue;
                    }

                    // revisit data, writing proper jumps
                    Span<byte> toRewrite = data;
                    while (!toRewrite.IsEmpty)
                    {
                        // parse existing header to obtain the length
                        (int valueLength, _) = ParseHeader(BinaryPrimitives.ReadInt64LittleEndian(toRewrite));

                        // retrieve the right jump
                        int jumpIndex = ReadJumpIndex(toRewrite.Slice(HeaderLength));
                        if (!_jumpCache.TryGetValue(jumpIndex, out long jump))
                        {
                            // this value was not overwritten by this flush, it needs to be read back from the table
                            jump = jumpTable[jumpIndex];
                        }

                        // write down the header
                        BinaryPrimitives.WriteInt64LittleEndian(toRewrite, BuildHeader(valueLength, jump));

                        int length = HeaderLength + KeccakLength + (valueLength == NullValueLength ? 0 : Align(valueLength));

                        // overwrite the jump to make it flushable AFTER the file is flushed
                        _jumpCache[jumpIndex] = _flushFrom + writtenSoFar;

                        writtenSoFar += length;

                        toRewrite = toRewrite.Slice(length);
                    }

                    data.CopyTo(destination);   // copy data
                    _flushFrom += data.Length;  // set proper cursor _flushFrom

                    _batches.TryDequeue(out _); // dequeue the current that was Peeked at the beginning
                    commit._isCommitted = true;// mark this as committed
                }

                // write down jumps with volatile to ensure that once the jump is visible the data are visible as well
                foreach ((int key, long jump) in _jumpCache)
                {
                    Volatile.Write(ref jumpTable[key], jump);
                }

                _jumpCache.Clear(); // not needed anymore, clear as the same entries might not be reused
            }
        }

        private int GetFileAndPosition(out int position)
        {
            int fileNumber = (int)Math.DivRem(_flushFrom, _logFileSize, out long pos);
            position = (int)pos;
            return fileNumber;
        }

        public void Delete(byte[] key)
        {
            using WriteBatch batch = new WriteBatch(this);
            batch.Delete(key);
            batch.Commit();
        }

        public void Set(byte[] key, byte[] value)
        {
            using WriteBatch batch = new WriteBatch(this);
            batch.Put(key, value);
            batch.Commit();
        }

        public unsafe bool TryGet(byte[] key, out Slice value)
        {
            int index = ReadJumpIndex(key);
            Span<long> jumpTable = new Span<long>(_jumps.Pointer, JumpsCount);

            long jump = Volatile.Read(ref jumpTable[index]);

            while (jump != 0)
            {
                int file = (int)Math.DivRem(jump, _logFileSize, out long position);

                Map map = Volatile.Read(ref _maps[file]);
                if (map == null)
                {
                    value = default;
                    return false;
                }

                byte* pointer = map.Pointer + position;
                long header = Unsafe.Read<long>(pointer);
                (int valueLength, long nextJump) = ParseHeader(header);

                Span<byte> actualKey = new Span<byte>(pointer + HeaderLength, KeccakLength);
                if (actualKey.StartsWith(key))
                {
                    if (valueLength != NullValueLength)
                    {
                        value = new Slice(pointer + HeaderLength + KeccakLength, valueLength);
                        return true;
                    }

                    value = default;
                    return false;
                }

                jump = nextJump;
            }

            value = default;
            return false;
        }

        public readonly unsafe struct Slice
        {
            private readonly byte[] _bytes;
            private readonly byte* _pointer;
            private readonly int _length;

            public Slice(byte* pointer, int length)
            {
                _pointer = pointer;
                _length = length;
                _bytes = null;
            }

            public Slice(byte[] bytes)
            {
                _pointer = null;
                _length = 0;
                _bytes = bytes;
            }

            public Span<byte> Span => _bytes ?? new Span<byte>(_pointer, _length);

            public byte[] ToArray() => _bytes ?? new Span<byte>(_pointer, _length).ToArray();
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
            readonly MemoryMappedViewAccessor _accessor;

            public string Path { get; }
            public int Number { get; }

            volatile int _offset;
            IntPtr _pointer;

            public Map(FileStream file, MemoryMappedFile mmf, string path, int number)
            {
                _file = file;
                _mmf = mmf;
                _accessor = mmf.CreateViewAccessor();
                Path = path;
                Number = number;
            }

            public int Offset => _offset;

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

                int length;

                int cursor = 0;
                while (cursor < _accessor.Capacity && (length = Unsafe.ReadUnaligned<byte>(ptr + cursor)) != EndMarker)
                {
                    cursor += 1;
                    if (length == byte.MaxValue)
                    {
                        length += Unsafe.ReadUnaligned<byte>(ptr + cursor);
                        cursor += 1;
                    }

                    cursor += length;
                }

                _offset = cursor;
            }

            public unsafe byte* Pointer => (byte*)_pointer.ToPointer();

            public void Flush(int nextOffsetPosition)
            {
                // flush and update
                _accessor.Flush();
                _file.Flush(true);

                _offset = nextOffsetPosition;
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
            private int _written;
            private byte[] _buffer;
            private const int InitialLength = 256 * 1024;

            private static readonly byte[] s_empty = new byte[0];

            public bool _isCommitted;

            public WriteBatch(MemoryMappedKeyValueStore store)
            {
                _store = store;
                _buffer = ArrayPool<byte>.Shared.Rent(InitialLength);
            }

            public void Put(byte[] key, byte[] value)
            {
                // the jump to previous is not known now,
                const int unknownJumpForNow = 0;

                if (key.Length != KeyLength)
                {
                    throw new ArgumentException($"The key should be {KeyLength} long", nameof(key));
                }

                long header;

                if (value != null)
                {
                    if (value.Length > MaxValueLength)
                    {
                        throw new ArgumentException($"The value breached {MaxValueLength}", nameof(value));
                    }

                    header = BuildHeader(value.Length, unknownJumpForNow);
                }
                else
                {
                    header = BuildHeader(NullValueLength, unknownJumpForNow); // set 0 for now as the jump is unknown
                    value = s_empty;
                }

                int length = HeaderLength + KeccakLength + Align(value.Length);
                Ensure(length);

                Span<byte> span = new Span<byte>(_buffer, _written, _buffer.Length - _written);

                BinaryPrimitives.WriteInt64LittleEndian(span, header);
                key.CopyTo(span.Slice(HeaderLength));
                value.CopyTo(span.Slice(HeaderLength + KeccakLength));

                _written += length;
            }

            public void Commit()
            {
                if (_written > 0)
                {
                    _store.Commit(this);
                }
            }

            public void Delete(byte[] key) => Put(key, null);

            public void Dispose()
            {
                if (_buffer != null)
                {
                    ArrayPool<byte>.Shared.Return(_buffer);
                    _buffer = null;
                }
            }

            public Span<byte> Data => new Span<byte>(_buffer, 0, _written);

            private void Ensure(int length)
            {
                if (_buffer.Length - _written < length)
                {
                    byte[] bytes = ArrayPool<byte>.Shared.Rent(_buffer.Length * 2);
                    _buffer.CopyTo(bytes, 0);
                    ArrayPool<byte>.Shared.Return(_buffer);
                    _buffer = bytes;
                }
            }
        }
    }
}
