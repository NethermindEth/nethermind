// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using BenchmarkDotNet.Attributes;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Benchmark
{
    [MemoryDiagnoser]
    public class JsonRpcResponseWriterBenchmarks
    {
        private readonly ArrayBufferWriter<byte> _writer = new(1024);
        private readonly ResultWrapper<long> _longResponse = CreateResponse(123L);
        private readonly ResultWrapper<ulong> _ulongResponse = CreateResponse(123UL);
        private readonly ResultWrapper<UInt256> _uint256Response = CreateResponse((UInt256)123);
        private readonly ResultWrapper<bool> _boolResponse = CreateResponse(true);
        private readonly ResultWrapper<string> _stringResponse = CreateResponse("Nethermind/v1.0.0");
        private readonly ResultWrapper<int> _intResponse = CreateResponse(123);
        private readonly ResultWrapper<PayloadStatusV1> _payloadStatusResponse = CreateResponse(PayloadStatusV1.Syncing);

        [Benchmark]
        public int ResultWrapperLong() => Write(_longResponse);

        [Benchmark]
        public int ResultWrapperUlong() => Write(_ulongResponse);

        [Benchmark]
        public int ResultWrapperUInt256() => Write(_uint256Response);

        [Benchmark]
        public int ResultWrapperBool() => Write(_boolResponse);

        [Benchmark]
        public int ResultWrapperString() => Write(_stringResponse);

        [Benchmark]
        public int ResultWrapperInt() => Write(_intResponse);

        [Benchmark]
        public int ResultWrapperPayloadStatus() => Write(_payloadStatusResponse);

        private int Write(JsonRpcResponse response)
        {
            _writer.Clear();
            JsonRpcResponseWriter.Write(_writer, response, EthereumJsonSerializer.JsonOptions);
            return _writer.WrittenCount;
        }

        private static ResultWrapper<T> CreateResponse<T>(T data)
        {
            ResultWrapper<T> response = ResultWrapper<T>.Success(data);
            response.Id = JsonRpcId.FromObject(1);
            return response;
        }
    }
}
