using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Reflection;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using FluentAssertions;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;
using Newtonsoft.Json;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Benchmark;

[MemoryDiagnoser]
public class DeserializeBenchmarks
{
    private JsonRpcContext _context;
    private JsonRpcProcessor _jsonRpcProcessor;
    private ITestRpcModule _testRpcModule;
    private string payload;

    [GlobalSetup]
    public async Task Initialize()
    {
        JsonRpcConfig jsonRpcConfig = new();
        jsonRpcConfig.EnabledModules = new[] { "Engine" };

        FileSystem fileSystem = new();

        _context = new JsonRpcContext(RpcEndpoint.Http);

        _testRpcModule = Substitute.For<ITestRpcModule>();
        RpcModuleProvider moduleProvider = new(fileSystem, jsonRpcConfig, NullLogManager.Instance);
        moduleProvider.Register(new SingletonModulePool<ITestRpcModule>(new SingletonFactory<ITestRpcModule>(_testRpcModule), true));
        JsonRpcService jsonRpcService = new(moduleProvider, NullLogManager.Instance, jsonRpcConfig);

        _jsonRpcProcessor = new JsonRpcProcessor(jsonRpcService, new EthereumJsonSerializer(), jsonRpcConfig, fileSystem, NullLogManager.Instance);

        payload = await File.ReadAllTextAsync(Path.Combine(TestContext.CurrentContext.TestDirectory, "Data",
            "samplenewpayload.json"));
    }

    [Benchmark]
    public async Task BenchmarkDeserialize()
    {
        // If I run it only once, for some reason, the benchmark does not show much different, even though it seems to
        // be triggering this call multiple time
        for (int i = 0; i < 10; i++)
        {
            IList<JsonRpcResult> result = await _jsonRpcProcessor
                .ProcessAsync(new StringReader(payload), _context)
                .ToListAsync();

            result.Should().HaveCount(1);
        }
    }

    [RpcModule(ModuleType.Engine)]
    public interface ITestRpcModule : IRpcModule
    {
        [JsonRpcMethod(
            Description = "Just a test. Does nothing.",
            IsSharable = true,
            IsImplemented = true)]
        Task<ResultWrapper<bool>> test_rpcbenchmark(ExecutionPayloadV1 payload);
    }

    // Copied and modified from merge plugin
    public class ExecutionPayloadV1
    {
        public ExecutionPayloadV1()
        {
            BlockHash = Keccak.Zero;
            ParentHash = Keccak.Zero;
            FeeRecipient = Address.Zero;
            StateRoot = Keccak.Zero;
            ReceiptsRoot = Keccak.Zero;
            LogsBloom = Bloom.Empty;
            PrevRandao = Keccak.Zero;
            ExtraData = Array.Empty<byte>();
        }

        public Keccak ParentHash { get; set; } = null!;
        public Address FeeRecipient { get; set; }
        public Keccak StateRoot { get; set; } = null!;
        public Keccak ReceiptsRoot { get; set; } = null!;

        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public Bloom LogsBloom { get; set; } = Bloom.Empty;
        public Keccak PrevRandao { get; set; } = Keccak.Zero;

        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public long BlockNumber { get; set; }
        public long GasLimit { get; set; }
        public long GasUsed { get; set; }
        public ulong Timestamp { get; set; }
        public byte[] ExtraData { get; set; } = Array.Empty<byte>();
        public UInt256 BaseFeePerGas { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public Keccak? BlockHash { get; set; } = null!;

        public byte[][] Transactions { get; set; } = Array.Empty<byte[]>();
    }
}

