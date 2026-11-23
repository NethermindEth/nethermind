// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using ILogger = Nethermind.Logging.ILogger;

namespace Nethermind.LightClient;

internal sealed class HostedStorageTrieUpdater(IExecutionStateSource execution, ILogger logger, TimeProvider? clock = null)
{
    internal const int MaxFetchedNodes = 8192;
    internal const int MaxChangedLeaves = 8192;
    private const int MaxVisitedNodes = 32768;
    private const int BatchSize = 256;
    private static readonly TimeSpan UpdateTimeout = TimeSpan.FromSeconds(10);

    internal async Task<HostedStorageSnapshot?> TryUpdateAsync(VerifiedHead head, Address address,
        HostedStorageSnapshot previous, Hash256 targetStorageRoot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (previous.RootHash == targetStorageRoot) return previous;
        if (targetStorageRoot == Keccak.EmptyTreeHash)
            return HostedStorageSnapshot.Create(targetStorageRoot, new Dictionary<ValueHash256, UInt256>(), cancellationToken);

        using CancellationTokenSource deadline = new(UpdateTimeout, clock ?? TimeProvider.System);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        CancellationToken token = linked.Token;
        try
        {
            Queue<PendingNode> pending = new();
            pending.Enqueue(new(TreePath.Empty, targetStorageRoot, null));
            HashSet<TreePath> reused = [];
            HashSet<ValueHash256> newLeaves = [];
            Dictionary<ValueHash256, UInt256> changes = [];
            int fetchedNodes = 0, receivedNodes = 0, visitedNodes = 0;
            while (pending.Count != 0)
            {
                token.ThrowIfCancellationRequested();
                List<PendingNode> batch = [with(BatchSize)];
                List<StorageTrieNodeRequest> requests = [with(BatchSize)];
                HashSet<Hash256> requested = [];
                while (pending.Count != 0 && batch.Count < BatchSize)
                {
                    token.ThrowIfCancellationRequested();
                    if (++visitedNodes > MaxVisitedNodes) throw BudgetExceeded();
                    PendingNode item = pending.Dequeue();
                    if (previous.TryGetNode(item.Path, out TrieNode? old) && NodeHash(old) == item.Hash)
                    {
                        reused.Add(item.Path);
                        continue;
                    }
                    batch.Add(item);
                    if (item.Inline is null && requested.Add(item.Hash)) requests.Add(new(item.Path, item.Hash));
                }

                IReadOnlyDictionary<Hash256, byte[]> response = new Dictionary<Hash256, byte[]>();
                if (requests.Count != 0)
                {
                    fetchedNodes += requests.Count;
                    if (fetchedNodes > MaxFetchedNodes) throw BudgetExceeded();
                    response = await execution.GetStorageTrieNodesAsync(head, address, requests.ToArray(), token);
                    receivedNodes += response.Count;
                    if (response.Count == 0)
                        throw new ExecutionDataUnavailableException("Execution peer supplied no requested storage trie nodes.");
                    foreach ((Hash256 hash, byte[] bytes) in response)
                        if (!requested.Contains(hash) || bytes.Length is 0 or > ExecutionProofVerifier.MaxNodeBytes)
                            throw new InvalidDataException("Execution peer returned an unexpected storage trie node.");
                }

                foreach (PendingNode item in batch)
                {
                    token.ThrowIfCancellationRequested();
                    byte[]? bytes = item.Inline;
                    if (bytes is null && !response.TryGetValue(item.Hash, out bytes))
                    {
                        pending.Enqueue(item);
                        continue;
                    }
                    if (Keccak.Compute(bytes) != item.Hash)
                        throw new InvalidDataException("A storage trie node does not match its authenticated parent.");
                    TrieNode node = new(NodeType.Unknown, bytes);
                    node.ResolveNode(NullTrieNodeResolver.Instance, item.Path);
                    if (node.IsLeaf)
                    {
                        ValueHash256 key = LeafPath(item.Path, node);
                        UInt256 value = ExecutionProofVerifier.DecodeStorageRlp(node.Value.AsSpan().ToArray());
                        if (!newLeaves.Add(key)) throw new InvalidDataException("A storage trie contains duplicate leaves.");
                        if (newLeaves.Count > MaxChangedLeaves) throw BudgetExceeded();
                        if (!previous.TryGetValue(key, out UInt256 oldValue) || oldValue != value) changes.Add(key, value);
                    }
                    else if (node.IsBranch)
                    {
                        if (item.Path.Length >= 64) throw new InvalidDataException("A storage trie branch exceeds its key length.");
                        for (int i = 0; i < 16; i++) EnqueueChild(node, item.Path.Append(i), i, pending);
                    }
                    else if (node.IsExtension)
                    {
                        TreePath childPath = ExtensionPath(item.Path, node);
                        if (!EnqueueChild(node, childPath, 0, pending))
                            throw new InvalidDataException("A storage trie extension has no child.");
                    }
                    else throw new InvalidDataException("Execution peer returned an unknown storage trie node.");
                }
            }

            Stack<(TreePath Path, TrieNode Node)> oldNodes = new();
            if (previous.TryGetNode(TreePath.Empty, out TrieNode? root)) oldNodes.Push((TreePath.Empty, root));
            while (oldNodes.TryPop(out (TreePath Path, TrieNode Node) item))
            {
                token.ThrowIfCancellationRequested();
                if (++visitedNodes > MaxVisitedNodes) throw BudgetExceeded();
                if (reused.Contains(item.Path)) continue;
                if (item.Node.IsLeaf)
                {
                    ValueHash256 key = LeafPath(item.Path, item.Node);
                    if (!newLeaves.Contains(key)) changes.Add(key, UInt256.Zero);
                    if (changes.Count > MaxChangedLeaves) throw BudgetExceeded();
                }
                else if (item.Node.IsBranch)
                {
                    TreePath path = item.Path;
                    for (int i = 0; i < 16; i++)
                        if (item.Node.GetChild(NullTrieNodeResolver.Instance, ref path, i) is TrieNode child)
                            oldNodes.Push((item.Path.Append(i), child));
                }
                else if (item.Node.IsExtension)
                {
                    TreePath path = item.Path;
                    TrieNode child = item.Node.GetChild(NullTrieNodeResolver.Instance, ref path, 0)
                        ?? throw new InvalidDataException("A hosted storage extension has no child.");
                    oldNodes.Push((ExtensionPath(item.Path, item.Node), child));
                }
                else throw new InvalidDataException("The hosted storage trie is incomplete.");
            }

            HostedStorageSnapshot updated = previous.Apply(changes, targetStorageRoot, token);
            if (logger.IsDebug)
                logger.Debug($"Updated hosted storage for {address} at block {head.Number}: {receivedNodes} trie nodes fetched, {changes.Count} changed leaves.");
            return updated;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            if (logger.IsDebug)
                logger.Debug($"Could not update hosted storage for {address} with trie nodes at block {head.Number}; falling back to a complete storage download: {exception.Message}");
            return null;
        }
    }

    private static Hash256 NodeHash(TrieNode node) => node.Keccak ?? Keccak.Compute(node.FullRlp.AsSpan());

    private static ValueHash256 LeafPath(TreePath path, TrieNode node)
    {
        byte[] key = node.Key ?? throw new InvalidDataException("A storage trie leaf has no key.");
        if (path.Length + key.Length != 64) throw new InvalidDataException("A storage trie leaf has an invalid key length.");
        return path.Append(key).Path;
    }

    private static TreePath ExtensionPath(TreePath path, TrieNode node)
    {
        byte[] key = node.Key ?? throw new InvalidDataException("A storage trie extension has no key.");
        if (key.Length == 0 || path.Length + key.Length > 64)
            throw new InvalidDataException("A storage trie extension has an invalid key length.");
        return path.Append(key);
    }

    private static bool EnqueueChild(TrieNode node, TreePath path, int index, Queue<PendingNode> pending)
    {
        if (node.GetChildHash(index) is Hash256 hash)
            pending.Enqueue(new(path, hash, null));
        else if (node.GetInlineNodeRlp(index) is byte[] bytes)
        {
            if (bytes.Length >= Hash256.Size) throw new InvalidDataException("A storage trie embeds an oversized child.");
            pending.Enqueue(new(path, Keccak.Compute(bytes), bytes));
        }
        else return false;
        return true;
    }

    private static InvalidDataException BudgetExceeded() => new("Incremental hosted storage refresh exceeded its work budget.");

    private readonly record struct PendingNode(TreePath Path, Hash256 Hash, byte[]? Inline);
}
