using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DotNet.Testcontainers.Containers;
using Nethermind.Core;
using Nethermind.Crypto;
using NUnit.Framework;

namespace Nethermind.IntegrationTests;

[TestFixture]
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class FlatDbTests
{
    private const ulong SepoliaChainId = 11155111UL;
    private const string TestAccountPrivateKeyHex = "4646464646464646464646464646464646464646464646464646464646464646";
    private const string RecipientAddress = "0x0000000000000000000000000000000000001234";

    [Test]
    public async Task FlatDb_EnabledNode_PersistsAndRestoresAccountState()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"nethermind-flatdb-{Guid.NewGuid():N}");
        string chainspecPath = Utils.ExtractEmbeddedChainspec("sepolia-with-test-account.json");
        Directory.CreateDirectory(databasePath);

        try
        {
            IContainer firstNode = await StartNodeAsync(databasePath, chainspecPath: chainspecPath);
            try
            {
                string firstStartupLogs = await firstNode.GetCleanStdoutAsync();
                Assert.That(firstStartupLogs, Does.Contain("State backend: flat (fresh node, flat DB enabled)."));

                string transactionHash = await SendTransferAsync(firstNode);
                await ProduceBlocksAsync(firstNode, 2);
                JsonNode transactionReceipt = await WaitForTransactionReceiptAsync(firstNode, transactionHash, TimeSpan.FromSeconds(30));

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(await GetBlockNumberAsync(firstNode), Is.EqualTo("0x2"));
                    Assert.That(transactionReceipt, Is.Not.Null);
                    Assert.That(await GetBalanceAsync(firstNode, RecipientAddress), Is.EqualTo("0x2a"));
                }
            }
            finally
            {
                await firstNode.DisposeAsync();
            }

            IContainer restartedNode = await StartNodeAsync(databasePath, chainspecPath: chainspecPath);
            try
            {
                string restartLogs = await restartedNode.GetCleanStdoutAsync();
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(restartLogs, Does.Contain("State backend: flat (existing flat DB detected)."));
                    Assert.That(await GetBlockNumberAsync(restartedNode), Is.EqualTo("0x2"));
                    Assert.That(await GetBalanceAsync(restartedNode, RecipientAddress), Is.EqualTo("0x2a"));
                }
            }
            finally
            {
                await restartedNode.DisposeAsync();
            }
        }
        finally
        {
            TryDeleteDirectory(databasePath);
            TryDeleteFile(chainspecPath);
        }
    }

    [Test]
    public async Task FlatDb_DoesNotReplaceAnExistingPatriciaStateDatabase()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"nethermind-flatdb-patricia-{Guid.NewGuid():N}");
        Directory.CreateDirectory(databasePath);

        try
        {
            IContainer patriciaNode = await StartNodeAsync(databasePath, ["--FlatDb.Enabled", "false"]);
            try
            {
                Assert.That(await patriciaNode.GetCleanStdoutAsync(), Does.Contain("State backend: patricia (flat DB disabled)."));
                await ProduceBlocksAsync(patriciaNode, 1);
            }
            finally
            {
                await patriciaNode.DisposeAsync();
            }

            IContainer restartedNode = await StartNodeAsync(databasePath);
            try
            {
                string restartLogs = await restartedNode.GetCleanStdoutAsync();
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(restartLogs, Does.Contain("State backend: patricia (existing patricia state detected)."));
                    Assert.That(await GetBlockNumberAsync(restartedNode), Is.EqualTo("0x1"));
                }
            }
            finally
            {
                await restartedNode.DisposeAsync();
            }
        }
        finally
        {
            TryDeleteDirectory(databasePath);
        }
    }

    [TestCase("3", "1048576", "Compact size must be a power of 2")]
    [TestCase("4", "2", "Persisted snapshot max compact size must not be smaller than CompactSize")]
    [TestCase("2", "3", "Persisted snapshot max compact size must be a power of 2")]
    public async Task FlatDb_InvalidCompactionConfiguration_PreventsStartup(
        string compactSize,
        string persistedSnapshotMaxCompactSize,
        string expectedDiagnostic)
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"nethermind-flatdb-invalid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(databasePath);

        try
        {
            IContainer node = await StartNodeAsync(
                databasePath,
                [
                    "--FlatDb.CompactSize", compactSize,
                    "--FlatDb.PersistedSnapshotMaxCompactSize", persistedSnapshotMaxCompactSize
                ],
                waitForInit: false,
                suppressStartFailures: true);
            try
            {
                string logs = await WaitForLogAsync(node, expectedDiagnostic, TimeSpan.FromSeconds(60));

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(logs, Does.Contain(expectedDiagnostic));
                    Assert.That(logs, Does.Not.Contain("Initialization Completed"));
                }
            }
            finally
            {
                await node.DisposeAsync();
            }
        }
        finally
        {
            TryDeleteDirectory(databasePath);
        }
    }

    [Test]
    public async Task FlatDb_ConfigurationProperties_AreApplied()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"nethermind-flatdb-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(databasePath);

        try
        {
            string[] flatDbOptions =
            [
                "--FlatDb.BlockCacheSizeBudget", "1048576",
                "--FlatDb.CompactSize", "2",
                "--FlatDb.CompactionOffset", "0",
                "--FlatDb.EnablePreimageRecording", "true",
                "--FlatDb.InlineCompaction", "true",
                "--FlatDb.MaxInFlightCompactJob", "1",
                "--FlatDb.MaxReorgDepth", "16",
                "--FlatDb.MinReorgDepth", "8",
                "--FlatDb.RegenerateCompactionOffset", "true",
                "--FlatDb.TrieCacheMemoryBudget", "1048576",
                "--FlatDb.TrieWarmerWorkerCount", "0"
            ];
            IContainer node = await StartNodeAsync(databasePath, flatDbOptions);
            try
            {
                string startupLogs = await node.GetCleanStdoutAsync();
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(startupLogs, Does.Contain("FlatDb.BlockCacheSizeBudget = \"0x100000\""));
                    Assert.That(startupLogs, Does.Contain("FlatDb.CompactSize = \"0x2\""));
                    Assert.That(startupLogs, Does.Contain("FlatDb.CompactionOffset = \"0x0\""));
                    Assert.That(startupLogs, Does.Contain("FlatDb.EnablePreimageRecording = true"));
                    Assert.That(startupLogs, Does.Contain("FlatDb.InlineCompaction = true"));
                    Assert.That(startupLogs, Does.Contain("FlatDb.MaxInFlightCompactJob = 1"));
                    Assert.That(startupLogs, Does.Contain("FlatDb.MaxReorgDepth = \"0x10\""));
                    Assert.That(startupLogs, Does.Contain("FlatDb.MinReorgDepth = \"0x8\""));
                    Assert.That(startupLogs, Does.Contain("FlatDb.RegenerateCompactionOffset = true"));
                    Assert.That(startupLogs, Does.Contain("FlatDb.TrieCacheMemoryBudget = \"0x100000\""));
                    Assert.That(startupLogs, Does.Contain("FlatDb.TrieWarmerWorkerCount = 0"));
                }
            }
            finally
            {
                await node.DisposeAsync();
            }
        }
        finally
        {
            TryDeleteDirectory(databasePath);
        }
    }

    private static async Task<IContainer> StartNodeAsync(
        string databasePath,
        string[] flatDbOptions = null,
        string chainspecPath = null,
        bool waitForInit = true,
        bool suppressStartFailures = false)
    {
        List<string> command =
        [
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
        ];

        if (flatDbOptions?.Contains("--FlatDb.Enabled") != true)
        {
            command.AddRange(["--FlatDb.Enabled", "true"]);
        }
        if (flatDbOptions?.Contains("--FlatDb.Layout") != true)
        {
            command.AddRange(["--FlatDb.Layout", "Flat"]);
        }
        if (flatDbOptions is not null)
        {
            command.AddRange(flatDbOptions);
        }

        List<(string HostPath, string ContainerPath)> bindMounts =
        [
            (databasePath, "/nethermind/nethermind_db")
        ];
        if (chainspecPath is not null)
        {
            const string containerChainspecPath = "/test-sepolia.json";
            bindMounts.Add((chainspecPath, containerChainspecPath));
            command.AddRange(["--Init.ChainSpecPath", containerChainspecPath]);
        }

        IContainer container = (await Utils.BuildNethermindContainerAsync(command.ToArray(), waitForInit, bindMounts: bindMounts)).Build();
        try
        {
            await container.StartAsync();
        }
        catch (Exception ex) when (suppressStartFailures)
        {
            TestContext.Progress.WriteLine($"expected node startup failure: {ex.Message}");
        }
        return container;
    }

    private static async Task<string> WaitForLogAsync(IContainer container, string expectedLog, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        string logs = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            logs = await container.GetCleanStdoutAsync() + await container.GetCleanStderrAsync();
            if (logs.Contains(expectedLog, StringComparison.Ordinal))
            {
                break;
            }

            await Task.Delay(200);
        }

        return logs;
    }

    private static async Task<string> SendTransferAsync(IContainer container)
    {
        using HttpClient client = CreateJsonRpcClient(container);
        PrivateKey signer = new(TestAccountPrivateKeyHex);
        Transaction transaction = new()
        {
            Type = TxType.Legacy,
            Nonce = 0,
            GasLimit = 21000,
            GasPrice = 20_000_000_000UL,
            To = new Address(RecipientAddress),
            Value = 42,
        };

        return await Utils.SignAndSendTransactionAsync(client, signer, transaction, SepoliaChainId);
    }

    private static async Task<JsonNode> GetTransactionReceiptAsync(IContainer container, string transactionHash)
    {
        using HttpClient client = CreateJsonRpcClient(container);
        return await Utils.SendJsonRpcRequestAsync(client, "eth_getTransactionReceipt", transactionHash);
    }

    private static async Task<JsonNode> WaitForTransactionReceiptAsync(IContainer container, string transactionHash, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        JsonNode receipt;
        do
        {
            receipt = await GetTransactionReceiptAsync(container, transactionHash);
            if (receipt is not null)
            {
                return receipt;
            }

            await Task.Delay(200);
        }
        while (DateTime.UtcNow < deadline);

        return null;
    }

    private static async Task<string> GetBalanceAsync(IContainer container, string address)
    {
        using HttpClient client = CreateJsonRpcClient(container);
        JsonNode result = await Utils.SendJsonRpcRequestAsync(client, "eth_getBalance", address, "latest");
        return result.GetValue<string>();
    }

    private static HttpClient CreateJsonRpcClient(IContainer container)
    {
        Uri jsonRpcUrl = new($"http://{container.Hostname}:{container.GetMappedPublicPort(8545)}");
        HttpClient client = new() { BaseAddress = jsonRpcUrl };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static async Task ProduceBlocksAsync(IContainer container, int count)
    {
        string jwtSecretHex = await GetJwtSecretAsync(container);
        Uri engineUrl = new($"http://{container.Hostname}:{container.GetMappedPublicPort(8551)}");
        using HttpClient client = Utils.CreateEngineHttpClient(engineUrl, jwtSecretHex);
        await Utils.CreateBlocksAsync(client, count, 1, 1633267481L, payloadBuildDelay: TimeSpan.FromSeconds(1));
    }

    private static async Task<string> GetBlockNumberAsync(IContainer container)
    {
        using HttpClient client = CreateJsonRpcClient(container);
        JsonNode result = await Utils.SendJsonRpcRequestAsync(client, "eth_blockNumber");
        return result.GetValue<string>();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            TestContext.Progress.WriteLine($"cleanup of {path} failed: {ex.Message}");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            TestContext.Progress.WriteLine($"cleanup of {path} failed: {ex.Message}");
        }
    }

    private static async Task<string> GetJwtSecretAsync(IContainer container)
    {
        ExecResult result = await container.ExecAsync(["cat", "jwt.hex"]);
        Assert.That(result.ExitCode, Is.EqualTo(0));
        return result.Stdout.Trim();
    }
}
