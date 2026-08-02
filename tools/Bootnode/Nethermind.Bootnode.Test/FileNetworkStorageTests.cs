// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            string[] persistedNodeIds = reloadedStorage.GetPersistedNodes()
                .Select(static node => node.NodeId.ToString(false))
                .ToArray();

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

    private static NetworkNode CreateNetworkNode(PrivateKey privateKey, int port) =>
        new(privateKey.PublicKey, "127.0.0.1", port);
}
