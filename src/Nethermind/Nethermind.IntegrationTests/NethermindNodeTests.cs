using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.IntegrationTests;

[TestFixture]
public class NethermindNodeTests
{
    private IContainer _nethermindContainer;

    private async Task StartContainerAsync(string[] commandOverride = null, bool waitForInit = true)
    {
        var image = Environment.GetEnvironmentVariable("NETHERMIND_IMAGE") ?? "nethermindeth/nethermind:latest";
        var defaultCommand = new[] { "--config", "sepolia", "--JsonRpc.Enabled", "true", "--JsonRpc.Host", "0.0.0.0", "--JsonRpc.Port", "8545", "--JsonRpc.EnginePort", "8551", "--JsonRpc.EngineHost", "0.0.0.0", "--JsonRpc.JwtSecretFile", "jwt.hex" };
        var command = commandOverride ?? defaultCommand;

            var builder = new ContainerBuilder()
                .WithImage(image)
                .WithCommand(command)
                .WithPortBinding(8545, true)
                .WithPortBinding(8551, true);

        if (waitForInit)
        {
            builder = builder.WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Initialization Completed"));
        }

        _nethermindContainer = builder.Build();
        
        try 
        {
            await _nethermindContainer.StartAsync();
        } 
        catch 
        {
            // Ignore start failures in negative tests; assertions will be made on container state/logs
        }
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_nethermindContainer != null)
        {
            await _nethermindContainer.DisposeAsync();
        }
    }

    [Test]
    public async Task Nethermind_ShouldRespondTo_EthBlockNumber()
    {
        await StartContainerAsync();
        _nethermindContainer.State.Should().Be(TestcontainersStates.Running);

        var rpcUrl = new Uri($"http://{_nethermindContainer.Hostname}:{_nethermindContainer.GetMappedPublicPort(8545)}");
        
        using var rpcClient = new BasicJsonRpcClient(rpcUrl, new EthereumJsonSerializer(), LimboLogs.Instance);
        var response = await rpcClient.Post("eth_blockNumber");
        
        response.Should().NotBeNullOrEmpty();
        response.Should().Contain("\"result\":");
    }

    [Test]
    public async Task Nethermind_ShouldLog_ExpectedMessages()
    {
        await StartContainerAsync();
        
        var cleanStdout = await _nethermindContainer.GetCleanStdoutAsync();
        
        cleanStdout.Should().Contain("Nethermind is starting up");
        cleanStdout.Should().Contain("Initialization Completed");
    }

    [Test]
    public async Task Nethermind_ShouldFail_WhenNoConfigProvided()
    {
        var commandWithoutConfig = new[] { "--config", "nonexistent.json" };
        
        await StartContainerAsync(commandWithoutConfig, waitForInit: false);
        
        // Let it attempt to start and fail
        await Task.Delay(2000);
        
        var cleanStdout = await _nethermindContainer.GetCleanStdoutAsync();
        var cleanStderr = await _nethermindContainer.GetCleanStderrAsync();
        
        var combinedLogs = cleanStdout + cleanStderr;
        combinedLogs.Should().Contain("Configuration file not found");
    }

    [Test]
    public async Task Nethermind_ShouldProduceBlock_ViaEngineApi()
    {
        var command = new[] 
        { 
            "--config", "sepolia", 
            "--JsonRpc.Enabled", "true", 
            "--JsonRpc.Host", "0.0.0.0", 
            "--JsonRpc.Port", "8545", 
            "--JsonRpc.EnginePort", "8551", 
            "--JsonRpc.EngineHost", "0.0.0.0", 
            "--JsonRpc.JwtSecretFile", "jwt.hex",
            "--Merge.TerminalTotalDifficulty", "0",
            "--Merge.Enabled", "true",
            "--JsonRpc.EngineEnabledModules", "[Engine,Eth]",
            "--Sync.NetworkingEnabled", "false",
            "--Sync.SynchronizationEnabled", "false",
            "--Sync.FastSync", "false",
            "--Sync.SnapSync", "false"
        };
        await StartContainerAsync(command);
        _nethermindContainer.State.Should().Be(TestcontainersStates.Running);

        // Read the JWT secret from the container
        var execResult = await _nethermindContainer.ExecAsync(new[] { "cat", "jwt.hex" });
        execResult.ExitCode.Should().Be(0);
        var jwtSecretHex = execResult.Stdout.Trim();
        jwtSecretHex.Should().NotBeNullOrEmpty();

        // Create JWT token using standard JWT generation if Nethermind utilities are not available
        // Simple JWT HS256 generation using the secret
        var secretBytes = Convert.FromHexString(jwtSecretHex.StartsWith("0x") ? jwtSecretHex.Substring(2) : jwtSecretHex);
        
        var header = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{{\"iat\":{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}}}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        
        string signature;
        using (var hmac = new System.Security.Cryptography.HMACSHA256(secretBytes))
        {
            var signatureBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{header}.{payload}"));
            signature = Convert.ToBase64String(signatureBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        
        var jwtToken = $"{header}.{payload}.{signature}";

        var engineUrl = new Uri($"http://{_nethermindContainer.Hostname}:{_nethermindContainer.GetMappedPublicPort(8551)}");
        
        using var httpClient = new HttpClient { BaseAddress = engineUrl };
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwtToken);
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        async Task<JsonNode> SendEngineRequestAsync(string method, params object[] parameters)
        {
            var request = new
            {
                jsonrpc = "2.0",
                method = method,
                @params = parameters,
                id = 1
            };
            
            var jsonString = JsonSerializer.Serialize(request);
            Console.WriteLine($"Sending {method}: {jsonString}");
            var content = new StringContent(jsonString, Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync("", content);
            response.EnsureSuccessStatusCode();
            var responseBody = await response.Content.ReadAsStringAsync();
            var json = JsonNode.Parse(responseBody);
            
            if (json["error"] != null)
            {
                var logs = await _nethermindContainer.GetLogsAsync();
                throw new Exception($"Engine API error: {json["error"]}\nContainer logs:\n{logs.Stdout}\n{logs.Stderr}");
            }
            
            return json["result"];
        }

        // 1. Get current head block (genesis)
        var block0 = await SendEngineRequestAsync("eth_getBlockByNumber", "0x0", false);
        if (block0 == null) throw new Exception($"Block 0 is null.");
        var genesisHash = block0["hash"].GetValue<string>();
        
        // 2. Initial forkchoiceUpdatedV1 (set head to genesis and start building payload)
        var forkchoiceState = new
        {
            headBlockHash = genesisHash,
            safeBlockHash = genesisHash,
            finalizedBlockHash = genesisHash
        };
        
        var payloadAttributes = new
        {
            timestamp = "0x6159af23",
            prevRandao = "0x0000000000000000000000000000000000000000000000000000000000000000",
            suggestedFeeRecipient = "0x0000000000000000000000000000000000000000"
        };

        var fcuResult1 = await SendEngineRequestAsync("engine_forkchoiceUpdatedV1", forkchoiceState, payloadAttributes);
        var payloadStatus = fcuResult1["payloadStatus"]["status"].GetValue<string>();
        if (payloadStatus != "VALID")
        {
            var logs = await _nethermindContainer.GetLogsAsync();
            Console.WriteLine($"Container logs: {logs.Stdout}\n{logs.Stderr}");
            Console.WriteLine($"FCU1 Failed: {fcuResult1.ToJsonString()}");
        }
        payloadStatus.Should().Be("VALID");
        var payloadId = fcuResult1["payloadId"].GetValue<string>();
        payloadId.Should().NotBeNullOrEmpty();

        // 3. Get payload
        var getPayloadResult = await SendEngineRequestAsync("engine_getPayloadV1", payloadId);
        var executionPayload = getPayloadResult;
        
        // 4. New payload
        var newPayloadResult = await SendEngineRequestAsync("engine_newPayloadV1", executionPayload);
        var newPayloadStatus = newPayloadResult["status"].GetValue<string>();
        newPayloadStatus.Should().BeOneOf("VALID", "SYNCING", "ACCEPTED");
        
        // 5. Final forkchoiceUpdatedV1 (update head to new block)
        var newBlockHash = executionPayload["blockHash"].GetValue<string>();
        var finalForkchoiceState = new
        {
            headBlockHash = newBlockHash,
            safeBlockHash = newBlockHash,
            finalizedBlockHash = newBlockHash
        };
        var fcuResult2 = await SendEngineRequestAsync("engine_forkchoiceUpdatedV1", finalForkchoiceState);
        fcuResult2["payloadStatus"]["status"].GetValue<string>().Should().BeOneOf("VALID", "SYNCING", "ACCEPTED");

        // 6. Verify block is produced using standard RPC
        var currentBlockStr = await SendEngineRequestAsync("eth_blockNumber");
        var currentBlock = currentBlockStr.GetValue<string>();
        if (currentBlock != "0x1")
        {
            var logs = await _nethermindContainer.GetLogsAsync();
            Console.WriteLine($"Container logs at failure: {logs.Stdout}\n{logs.Stderr}");
        }
        currentBlock.Should().Be("0x1");
    }
}
