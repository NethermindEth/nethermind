// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Flat.PersistedSnapshots;

internal static class PersistedSnapshotUtils
{
    internal static void DumpSnapshotToJson(Snapshot snapshot, string filename)
    {
        Dictionary<string, object> dump = [];

        Dictionary<string, string> accounts = [];
        foreach (KeyValuePair<HashedKey<Address>, Account?> kv in snapshot.Accounts)
        {
            Address address = kv.Key;
            accounts[address.Bytes.ToHexString(false)] = kv.Value is null
                ? ""
                : AccountDecoder.Slim.Encode(kv.Value).Bytes.ToHexString(false);
        }
        dump["accounts"] = accounts;

        Dictionary<string, string> storages = [];
        foreach (KeyValuePair<HashedKey<(Address, UInt256)>, SlotValue?> kv in snapshot.Storages)
        {
            (Address addr, UInt256 slot) = kv.Key.Key;
            // Slot serialized as decimal so it survives JSON round-trips without ambiguity.
            string key = $"{addr.Bytes.ToHexString(false)}:{slot}";
            storages[key] = kv.Value.HasValue
                ? kv.Value.Value.AsReadOnlySpan.ToHexString(false)
                : "";
        }
        dump["storages"] = storages;

        Dictionary<string, bool> selfDestructed = [];
        foreach (KeyValuePair<HashedKey<Address>, bool> kv in snapshot.SelfDestructedStorageAddresses)
        {
            Address address = kv.Key;
            selfDestructed[address.Bytes.ToHexString(false)] = kv.Value;
        }
        dump["selfDestructed"] = selfDestructed;

        Dictionary<string, string> stateNodes = [];
        foreach (KeyValuePair<HashedKey<TreePath>, TrieNode> kv in snapshot.StateNodes)
        {
            if (kv.Value.FullRlp.Length == 0 && kv.Value.NodeType == NodeType.Unknown) continue;
            TreePath path = kv.Key;
            string key = $"{path.Span.ToHexString(false)}:{path.Length}";
            stateNodes[key] = kv.Value.FullRlp.AsSpan().ToHexString(false);
        }
        dump["stateNodes"] = stateNodes;

        Dictionary<string, string> storageNodes = [];
        foreach (KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode> kv in snapshot.StorageNodes)
        {
            if (kv.Value.FullRlp.Length == 0 && kv.Value.NodeType == NodeType.Unknown) continue;
            (Hash256 hash, TreePath path) = kv.Key.Key;
            string key = $"{hash.Bytes.ToHexString(false)}:{path.Span.ToHexString(false)}:{path.Length}";
            storageNodes[key] = kv.Value.FullRlp.AsSpan().ToHexString(false);
        }
        dump["storageNodes"] = storageNodes;

        File.WriteAllText(filename, JsonSerializer.Serialize(dump));
    }

    internal static void ValidatePersistedSnapshot(Snapshot snapshot, PersistedSnapshot persisted, bool dumpWhenFailed = true)
    {
        string filename = $"broken.{snapshot.From.BlockNumber}.{snapshot.To.BlockNumber}.json";

        try
        {
            foreach (KeyValuePair<HashedKey<Address>, Account?> kv in snapshot.Accounts)
            {
                Address address = kv.Key;
                if (!persisted.TryGetAccount(address, out Account? acc))
                    throw new InvalidOperationException($"Account {address} not found in persisted snapshot");

                if (kv.Value is null)
                {
                    if (acc is not null)
                        throw new InvalidOperationException($"Account {address} should be null but has RLP data");
                }
                else
                {
                    if (acc is null || acc.Balance != kv.Value.Balance || acc.Nonce != kv.Value.Nonce
                        || acc.CodeHash != kv.Value.CodeHash || acc.StorageRoot != kv.Value.StorageRoot)
                    {
                        throw new InvalidOperationException($"Account {address} mismatch");
                    }
                }
            }

            foreach (KeyValuePair<HashedKey<(Address, UInt256)>, SlotValue?> kv in snapshot.Storages)
            {
                (Address addr, UInt256 slot) = kv.Key.Key;
                SlotValue slotValue = default;
                if (!persisted.TryGetSlot(addr, slot, ref slotValue))
                    throw new InvalidOperationException($"Storage {addr}:{slot} not found in persisted snapshot");

                SlotValue expected = kv.Value ?? default;
                if (!slotValue.AsReadOnlySpan.SequenceEqual(expected.AsReadOnlySpan))
                    throw new InvalidOperationException($"Storage {addr}:{slot} mismatch");
            }

            foreach (KeyValuePair<HashedKey<Address>, bool> kv in snapshot.SelfDestructedStorageAddresses)
            {
                Address address = kv.Key;
                bool? flag = persisted.TryGetSelfDestructFlag(address) ?? throw new InvalidOperationException($"SelfDestruct {address} not found in persisted snapshot");
                if (flag.Value != kv.Value)
                    throw new InvalidOperationException($"SelfDestruct {address} mismatch: expected {kv.Value}, got {flag.Value}");
            }

            foreach (KeyValuePair<HashedKey<TreePath>, TrieNode> kv in snapshot.StateNodes)
            {
                if (kv.Value.FullRlp.Length == 0 && kv.Value.NodeType == NodeType.Unknown) continue;
                TreePath path = kv.Key;
                if (!persisted.TryLoadStateNodeRlp(in path, out byte[]? nodeRlp))
                    throw new InvalidOperationException($"StateNode at path length {path.Length} not found in persisted snapshot");
                if (!nodeRlp!.AsSpan().SequenceEqual(kv.Value.FullRlp.AsSpan()))
                    throw new InvalidOperationException($"StateNode at path length {path.Length} RLP mismatch");
            }

            foreach (KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode> kv in snapshot.StorageNodes)
            {
                if (kv.Value.FullRlp.Length == 0 && kv.Value.NodeType == NodeType.Unknown) continue;
                (Hash256 hash, TreePath path) = kv.Key.Key;
                ValueHash256 hashStruct = hash.ValueHash256;
                if (!persisted.TryLoadStorageNodeRlp(in hashStruct, path, out byte[]? nodeRlp))
                    throw new InvalidOperationException($"StorageNode {hash} at path length {path.Length} not found in persisted snapshot");
                if (!nodeRlp!.AsSpan().SequenceEqual(kv.Value.FullRlp.AsSpan()))
                    throw new InvalidOperationException($"StorageNode {hash} at path length {path.Length} RLP mismatch");
            }
        }
        catch (InvalidOperationException ex)
        {
            if (dumpWhenFailed) DumpSnapshotToJson(snapshot, filename);
            throw new InvalidOperationException($"{ex.Message}. Dumped snapshot to {filename}", ex);
        }
    }
}
