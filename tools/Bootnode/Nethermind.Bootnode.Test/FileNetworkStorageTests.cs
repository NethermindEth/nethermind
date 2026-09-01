// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Config;
using Nethermind.Crypto;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Bootnode.Test;

[TestFixture]
public class FileNetworkStorageTests
{
    [Test]
    public void Commit_replaces_persisted_nodes_with_batch_contents()
    {
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "discovery-nodes.json");

        try
        {
            using PrivateKeyGenerator generator = new();
            using PrivateKey oldKey = generator.Generate();
            using PrivateKey currentKey = generator.Generate();
            NetworkNode oldNode = CreateNetworkNode(oldKey, 30303);
            NetworkNode currentNode = CreateNetworkNode(currentKey, 30304);
            FileNetworkStorage storage = new(path, LimboLogs.Instance);

            storage.UpdateNode(oldNode);
            storage.StartBatch();
            storage.UpdateNode(currentNode);
            storage.Commit();

            FileNetworkStorage reloadedStorage = new(path, LimboLogs.Instance);
            string[] persistedNodeIds = GetPersistedNodeIds(reloadedStorage);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(reloadedStorage.PersistedNodesCount, Is.EqualTo(1));
                Assert.That(persistedNodeIds, Is.EquivalentTo(new[] { currentNode.NodeId.ToString(false) }));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Load_retains_valid_nodes_when_entries_are_invalid()
    {
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "discovery-nodes.json");

        try
        {
            using PrivateKeyGenerator generator = new();
            using PrivateKey validKey = generator.Generate();
            using PrivateKey addedKey = generator.Generate();
            NetworkNode validNode = CreateNetworkNode(validKey, 30303, reputation: 7);
            NetworkNode addedNode = CreateNetworkNode(addedKey, 30304, reputation: 11);
            string persistedNodes = JsonSerializer.Serialize(new object[]
            {
                new { Node = validNode.ToString(), validNode.Reputation },
                new { Node = "invalid", Reputation = 0L },
                new { Node = validNode.ToString(), Reputation = "invalid" }
            });
            File.WriteAllText(path, persistedNodes);

            FileNetworkStorage storage = new(path, LimboLogs.Instance);
            NetworkNode[] loadedNodes = storage.GetPersistedNodes();
            storage.UpdateNode(addedNode);

            FileNetworkStorage reloadedStorage = new(path, LimboLogs.Instance);
            string[] persistedNodeIds = GetPersistedNodeIds(reloadedStorage);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(loadedNodes, Has.Length.EqualTo(1));
                Assert.That(loadedNodes[0].NodeId, Is.EqualTo(validNode.NodeId));
                Assert.That(loadedNodes[0].Reputation, Is.EqualTo(validNode.Reputation));
                Assert.That(reloadedStorage.PersistedNodesCount, Is.EqualTo(2));
                Assert.That(persistedNodeIds, Is.EquivalentTo(new[]
                {
                    validNode.NodeId.ToString(false),
                    addedNode.NodeId.ToString(false)
                }));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string[] GetPersistedNodeIds(FileNetworkStorage storage)
    {
        NetworkNode[] nodes = storage.GetPersistedNodes();
        string[] nodeIds = new string[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            nodeIds[i] = nodes[i].NodeId.ToString(false);
        }

        return nodeIds;
    }

    private static NetworkNode CreateNetworkNode(PrivateKey privateKey, int port, long reputation = 0) =>
        new(privateKey.PublicKey, "127.0.0.1", port, reputation);
}
