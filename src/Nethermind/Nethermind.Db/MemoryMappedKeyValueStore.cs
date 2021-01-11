using System;
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
        public const int KeyLength = 32;
        const int MaxValueLength = NullValueLength - 1;
        const int NullValueLength = short.MaxValue;
        const int MaxNumberOfFiles = 2048;
        const byte EndMarker = 0; // a marker that when written as the first byte of the length in a file marks it as 0

        /// <summary>
        /// This is an important settings as it heavily affects the size of the jump file. For 2 bytes, it will be 512KB long. For 3 bytes it will be 512MB
        /// </summary>
        const int NumberOfBytesForJumps = 2;
        const int AddressSize = sizeof(ulong);
        const int JumpsCount = 1 << (NumberOfBytesForJumps * BitsInByte);
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
        readonly ConcurrentDictionary<byte[], byte[]> _values = new ConcurrentDictionary<byte[], byte[]>(new EqualityComparer());
        readonly CancellationTokenSource _cts;

        static readonly byte[] s_nullMarker = new byte[0];

        Thread _flusher;
        long _cursor;
        long _flushFrom;
        Map _jumps;

        class EqualityComparer : IEqualityComparer<byte[]>
        {
            public bool Equals(byte[] x, byte[] y)
            {
                // assumes aligned
                ref long xb = ref Unsafe.As<byte, long>(ref x[0]);
                ref long yb = ref Unsafe.As<byte, long>(ref y[0]);

                return xb == yb &&
                       Unsafe.Add(ref xb, 1) == Unsafe.Add(ref yb, 1) &&
                       Unsafe.Add(ref xb, 2) == Unsafe.Add(ref yb, 2) &&
                       Unsafe.Add(ref xb, 3) == Unsafe.Add(ref yb, 3);
            }

            public int GetHashCode(byte[] obj)
            {
                return Unsafe.ReadUnaligned<int>(ref obj[0]);
            }
        }

        public MemoryMappedKeyValueStore(string directoryPath, int logFileSize = 256 * 1024 * 1024, int maxBatchFlushSize = 10000)
        {
            _dir = directoryPath;
            _logFileSize = logFileSize;
            _maxBatchFlushSize = maxBatchFlushSize;
            _cts = new CancellationTokenSource();
        }

        public void Initialize()
        {
            string jumpsPath = Path.Combine(_dir, JumpsFileName);
            _jumps = InitializeMap(jumpsPath, JumpsFileSize);

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
            _cursor = current.Number * _logFileSize + current.Offset;

            _flushFrom = _cursor;
            _flusher = new Thread(RunFlusher);
            _flusher.Start();
        }

        unsafe void RunFlusher()
        {
            Span<long> jumpTable = new Span<long>(_jumps.Pointer, JumpsCount);
            List<byte[]> mapped = new List<byte[]>();

            SpinWait sw = default;
            while (!_cts.IsCancellationRequested || !_values.IsEmpty)
            {
                int fileNumber = (int)(_flushFrom / _logFileSize);
                ref Map current = ref _maps[fileNumber];

                if (current == null)
                {
                    Volatile.Write(ref current, CreateLogFile(fileNumber));
                }

                if (_values.IsEmpty)
                {
                    sw.SpinOnce();
                    continue; // nothing to do, nothing to flush
                }

                int batchCount = 0;
                using (IEnumerator<KeyValuePair<byte[], byte[]>> enumerator = _values.GetEnumerator())
                {
                    while (batchCount < _maxBatchFlushSize && enumerator.MoveNext())
                    {
                        (byte[] keccak, byte[] value) = enumerator.Current;

                        int index = ReadJumpIndex(keccak);
                        ref long jump = ref jumpTable[index];
                        long header = BuildHeader(ReferenceEquals(value, s_nullMarker) ? NullValueLength : value.Length, jump);

                        int length = HeaderLength + KeccakLength + Align(value.Length, ByteAlignment);
                        int position = (int)(_flushFrom % _logFileSize);

                        int leftover = _logFileSize - position;
                        Span<byte> span = new Span<byte>(current.Pointer + position, leftover);
                        if (length > leftover)
                        {
                            // left zeros, bump up the flushFrom and break
                            _flushFrom += leftover;
                            break;
                        }

                        // write down to span
                        BinaryPrimitives.WriteInt64LittleEndian(span, header);
                        keccak.CopyTo(span.Slice(HeaderLength));
                        value.CopyTo(span.Slice(HeaderLength + KeccakLength));

                        // write to map with volatile making it eventually visible
                        Volatile.Write(ref jump, _flushFrom);

                        // write to span
                        mapped.Add(keccak);

                        _flushFrom += length;
                        batchCount++;
                    }
                }

                if (_flushFrom % _logFileSize == 0)
                {
                    // seal up the current, it written till the limit of the file
                    current.Flush(_logFileSize);
                }
                else
                {
                    // flush till the flush from points to
                    current.Flush((int)(_flushFrom % _logFileSize));
                }

                // log is flushed, flush map
                _jumps.Flush(JumpsFileSize);

                // remove all mapped already
                foreach (byte[] key in mapped)
                {
                    _values.TryRemove(key, out _);
                }
                mapped.Clear();
            }
        }

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
        static int ReadJumpIndex(byte[] key)
        {
            const int mask = JumpsCount - 1;
            const int shift = (4 - NumberOfBytesForJumps) * BitsInByte;
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(key);
            return (int)((value >> shift) & mask);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Align(int value, int alignment) => (value + (alignment - 1)) & -alignment;

        Map CreateLogFile(int fileNumber)
        {
            return InitializeMap(Path.Combine(_dir, GetFileName(fileNumber)), _logFileSize);
        }

        static string GetFileName(int number) => $"{Prefix}{number:D6}";

        Map InitializeMap(string file, int size)
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

            string justNumber = new FileInfo(file).Name.Replace(Prefix, "");
            int.TryParse(justNumber, out int number);
            Map map = new Map(stream, mmf, file, number);
            map.Initialize();
            return map;
        }

        public bool Flushed => _values.IsEmpty;

        public void Delete(byte[] key)
        {
            if (key.Length != KeyLength)
            {
                throw new ArgumentException($"The key should be {KeyLength} long", nameof(key));
            }

            _values[key] = s_nullMarker;
        }

        public void Set(byte[] key, byte[] value)
        {
            int length = value.Length;

            if (length > MaxValueLength)
            {
                throw new ArgumentException($"The value breached {MaxValueLength}", nameof(value));
            }

            if (key.Length != KeyLength)
            {
                throw new ArgumentException($"The key should be {KeyLength} long", nameof(key));
            }

            _values[key] = value;
        }

        public unsafe bool TryGet(byte[] key, out Slice value)
        {
            if (_values.TryGetValue(key, out byte[] array))
            {
                if (array == s_nullMarker)
                {
                    value = default;
                    return false;
                }

                value = new Slice(array);
                return true;
            }

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
            _cts.Cancel();
            _flusher?.Join();

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
    }
}
