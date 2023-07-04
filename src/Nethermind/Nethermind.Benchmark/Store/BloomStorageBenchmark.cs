// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Logging;

namespace Nethermind.Benchmarks.Store;

public class BloomStorageBenchmark
{
    [Benchmark]
    public int Improved()
    {
        string basePath = Path.Combine(Path.GetTempPath(), DbNames.Bloom + "Improved");
        return Benchmark(new FixedSizeFileStoreFactory(basePath, DbNames.Bloom, Bloom.ByteLength), basePath);
    }

    private static int Benchmark(IFileStoreFactory fileStorageFactory, string basePath)
    {
        int maxBlock = ushort.MaxValue * 8 + 7;
        BloomConfig config = new() { IndexLevelBucketSizes = new[] { 16, 16, 16 } };
        try
        {
            using BloomStorage storage = new(config, new MemDb(), fileStorageFactory);

            Parallel.For(0, maxBlock + 1,
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 16 },
                i =>
                {
                    Bloom bloom = new();
                    bloom.Set(i % Bloom.BitLength);
                    storage.Store(i, bloom);
                });

            IBloomEnumeration blooms = storage.GetBlooms(0, maxBlock);

            int i = 0;
            foreach (Bloom _ in blooms)
            {
                i++;
                blooms.TryGetBlockNumber(out long _);
            }

            return i;
        }
        finally
        {
            Directory.Delete(basePath, true);
        }
    }

    [Benchmark(Baseline = true)]
    public int Old()
    {
        string basePath = Path.Combine(Path.GetTempPath(), DbNames.Bloom + "Old");
        return Benchmark(new FixedSizeFileStoreOldFactory(basePath, DbNames.Bloom, Bloom.ByteLength), basePath);
    }


    private class FixedSizeFileStoreOldFactory : IFileStoreFactory
    {
        private readonly string _basePath;
        private readonly string _extension;
        private readonly int _elementSize;

        public FixedSizeFileStoreOldFactory(string basePath, string extension, int elementSize)
        {
            _basePath = string.Empty.GetApplicationResourcePath(basePath);
            _extension = extension;
            _elementSize = elementSize;
            Directory.CreateDirectory(_basePath);
        }

        public IFileStore Create(string name) => new FixedSizeFileStoreOld(Path.Combine(_basePath, name + "." + _extension), _elementSize);
    }

    private class FixedSizeFileStoreOld : IFileStore
    {
        private readonly string _path;
        private readonly int _elementSize;
        private readonly Stream _fileWrite;
        private readonly Stream _fileRead;
        private int _needsFlush;

        public FixedSizeFileStoreOld(string path, int elementSize)
        {
            _path = path;
            _elementSize = elementSize;
            _fileWrite = File.Open(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
            _fileRead = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }

        public void Write(long index, ReadOnlySpan<byte> element)
        {
            if (element.Length != _elementSize)
            {
                throw new ArgumentException($"Element size incorrect. Only elements of length {_elementSize} are acceptable.");
            }

            try
            {
                lock (_fileWrite)
                {
                    SeekIndex(_fileWrite, index);
                    _fileWrite.Write(element);
                    Interlocked.Exchange(ref _needsFlush, 1);
                }
            }
            catch (ArgumentOutOfRangeException e)
            {
                long position = GetPosition(index);
                throw new InvalidOperationException($"Bloom storage tried to write a file that is too big for file system. " +
                                                    $"Trying to write data at index {index} with size {_elementSize} at file position {position} to file {_path}", e)
                {
                    Data =
                    {
                        {"Index", index},
                        {"Size", _elementSize},
                        {"Position", position},
                        {"Path", _path}
                    }
                };
            }
        }

        public int Read(long index, Span<byte> element)
        {
            EnsureFlushed();

            lock (_fileRead)
            {
                SeekIndex(_fileRead, index);
                return _fileRead.Read(element);
            }
        }

        public IFileReader CreateFileReader()
        {
            EnsureFlushed();
            return new FileReader(_path, _elementSize);
        }

        private void EnsureFlushed()
        {
            if (Interlocked.CompareExchange(ref _needsFlush, 0, 1) == 1)
            {
                lock (_fileWrite)
                {
                    _fileWrite.Flush();
                }
            }
        }

        private void SeekIndex(Stream file, long index)
        {
            long seekPosition = GetPosition(index);
            if (file.Position != seekPosition)
            {
                file.Position = seekPosition;
            }
        }

        private long GetPosition(long index) => index * _elementSize;

        public void Dispose()
        {
            _fileWrite.Dispose();
            _fileRead.Dispose();
        }
    }
}
