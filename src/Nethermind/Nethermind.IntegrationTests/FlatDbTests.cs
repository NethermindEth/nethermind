using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DotNet.Testcontainers.Containers;
using NUnit.Framework;

namespace Nethermind.IntegrationTests;

[TestFixture]
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class FlatDbTests
{
    [Test]
    public async Task FlatDb_EnabledNode_PersistsAndRestoresEngineBlocks()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"nethermind-flatdb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(databasePath);

        try
        {
            IContainer firstNode = await StartNodeAsync(databasePath);
            try
            {
                string firstStartupLogs = await firstNode.GetCleanStdoutAsync();
                Assert.That(firstStartupLogs, Does.Contain("State backend: flat (fresh node, flat DB enabled)."));

                await ProduceBlocksAsync(firstNode, 2);
                Assert.That(await GetBlockNumberAsync(firstNode), Is.EqualTo("0x2"));
            }
            finally
            {
                await firstNode.DisposeAsync();
            }

            IContainer restartedNode = await StartNodeAsync(databasePath);
            try
            {
                string restartLogs = await restartedNode.GetCleanStdoutAsync();
                Assert.That(restartLogs, Does.Contain("State backend: flat (existing flat DB detected)."));
                Assert.That(await GetBlockNumberAsync(restartedNode), Is.EqualTo("0x2"));
            }
            finally
            {
                await restartedNode.DisposeAsync();
            }
        }
        finally
        {
            Directory.Delete(databasePath, recursive: true);
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
            Directory.Delete(databasePath, recursive: true);
        }
    }

    private static async Task<IContainer> StartNodeAsync(string databasePath, string[] flatDbOptions = null)
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
            "--Sync.SnapSync", "false",
            "--FlatDb.Enabled", "true",
            "--FlatDb.Layout", "Flat"
        ];

        if (flatDbOptions is not null)
        {
            command.AddRange(flatDbOptions);
        }

        IContainer container = (await Utils.BuildNethermindContainerAsync(command.ToArray(), bindMount: (databasePath, "/nethermind/nethermind_db"))).Build();
        await container.StartAsync();
        return container;
    }

    private static async Task ProduceBlocksAsync(IContainer container, int count)
    {
        string jwtSecretHex = await GetJwtSecretAsync(container);
        Uri engineUrl = new($"http://{container.Hostname}:{container.GetMappedPublicPort(8551)}");
        using HttpClient client = Utils.CreateEngineHttpClient(engineUrl, jwtSecretHex);
        await Utils.CreateBlocksAsync(client, count, 1, 1633267481L);
    }

    private static async Task<string> GetBlockNumberAsync(IContainer container)
    {
        string jwtSecretHex = await GetJwtSecretAsync(container);
        Uri engineUrl = new($"http://{container.Hostname}:{container.GetMappedPublicPort(8551)}");
        using HttpClient client = Utils.CreateEngineHttpClient(engineUrl, jwtSecretHex);
        JsonNode result = await Utils.SendEngineRequestAsync(client, "eth_blockNumber");
        return result.GetValue<string>();
    }

    private static async Task<string> GetJwtSecretAsync(IContainer container)
    {
        ExecResult result = await container.ExecAsync(["cat", "jwt.hex"]);
        Assert.That(result.ExitCode, Is.EqualTo(0));
        return result.Stdout.Trim();
    }
}
