// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.State.Tracing;

namespace Nethermind.Benchmarks.State
{
    public class StorageCellBenchmark
    {
        private static IStorageTracer _tracer;
        private static StorageCell _cell = new(Address.SystemUser, new UInt256(1, 2, 3, 4));

        private const int OperationsPerInvoke = 1000;


        [GlobalSetup]
        public void Setup()
        {
            _tracer = new StorageTracer();
        }

        [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
        public void Parameter_Passing()
        {
            for (int i = 0; i < OperationsPerInvoke / 10; i++)
            {
                _tracer.ReportStorageRead(_cell);
                _tracer.ReportStorageRead(_cell);
                _tracer.ReportStorageRead(_cell);
                _tracer.ReportStorageRead(_cell);
                _tracer.ReportStorageRead(_cell);
                _tracer.ReportStorageRead(_cell);
                _tracer.ReportStorageRead(_cell);
                _tracer.ReportStorageRead(_cell);
                _tracer.ReportStorageRead(_cell);
                _tracer.ReportStorageRead(_cell);
            }
        }

        private class StorageTracer : IStorageTracer
        {
            public bool IsTracingStorage => throw new NotImplementedException();

            public void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value) =>
                throw new NotImplementedException();

            [MethodImpl(MethodImplOptions.NoInlining)]
            public void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after) { }

            [MethodImpl(MethodImplOptions.NoInlining)]
            public void ReportStorageRead(in StorageCell storageCell) { }
        }
    }
}
