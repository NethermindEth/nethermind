// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using DotNetty.Buffers;
using Nethermind.Core.Buffers;

namespace Nethermind.Network.Benchmarks
{
    /// <summary>
    /// Compares the old DotNetty pooled message buffers against the new
    /// <see cref="PooledBuffer"/> channel buffers on the same rent/write/handoff/release workload.
    /// </summary>
    public class PooledBufferBenchmarks
    {
        private static readonly byte[] Fill = CreateFill();
        private static readonly IByteBufferAllocator NettyAllocator = PooledByteBufferAllocator.Default;

        private static byte[] CreateFill()
        {
            byte[] fill = new byte[65536];
            new Random(42).NextBytes(fill);
            return fill;
        }

        [Params(256, 65536)]
        public int Size { get; set; }

        [Benchmark(Baseline = true)]
        public void NettyRentWriteRelease()
        {
            IByteBuffer buffer = NettyAllocator.Buffer(Size);
            try
            {
                buffer.WriteBytes(Fill, 0, Size);
            }
            finally
            {
                buffer.Release();
            }
        }

        [Benchmark]
        public void PooledRentWriteRelease()
        {
            using PooledBuffer buffer = PooledBuffer.Rent(Size);
            Fill.AsSpan(0, Size).CopyTo(buffer.Span);
        }

        [Benchmark]
        public void NettyRentWriteSliceRelease()
        {
            IByteBuffer buffer = NettyAllocator.Buffer(Size);
            try
            {
                buffer.WriteBytes(Fill, 0, Size);
                IByteBuffer slice = buffer.ReadRetainedSlice(Size / 2);
                try
                {
                    slice.SetReaderIndex(0);
                }
                finally
                {
                    slice.Release();
                }
            }
            finally
            {
                buffer.Release();
            }
        }

        [Benchmark]
        public void PooledRentWriteSliceRelease()
        {
            using PooledBuffer buffer = PooledBuffer.Rent(Size);
            Fill.AsSpan(0, Size).CopyTo(buffer.Span);
            using PooledBuffer.Slice slice = buffer[..(Size / 2)];
        }
    }
}
