// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Storage;
using Nethermind.Trie;

namespace Nethermind.State.Flat.PersistedSnapshots;

internal static class PersistedSnapshotUtils
{
    internal static void DumpSnapshotToJson(Snapshot snapshot, string filename)
    {
        Dictionary<string, object> dump = [];

        // 1. Accounts
        Dictionary<string, string> accounts = [];
        foreach (KeyValuePair<HashedKey<Address>, Account?> kv in snapshot.Accounts)
        {
            Address address = kv.Key;
            accounts[address.Bytes.ToHexString(false)] = kv.Value is null
                ? ""
                : AccountDecoder.Slim.Encode(kv.Value).Bytes.ToHexString(false);
        }
        dump["accounts"] = accounts;

        // 2. Storages
        Dictionary<string, string> storages = [];
        foreach (KeyValuePair<HashedKey<(Address, UInt256)>, SlotValue?> kv in snapshot.Storages)
        {
            (Address addr, UInt256 slot) = kv.Key.Key;
            // Store slot as decimal string representation (safe for JSON)
            string key = $"{addr.Bytes.ToHexString(false)}:{slot}";
            storages[key] = kv.Value.HasValue
                ? kv.Value.Value.AsReadOnlySpan.ToHexString(false)
                : "";
        }
        dump["storages"] = storages;

        // 3. SelfDestructedStorageAddresses
        Dictionary<string, bool> selfDestructed = [];
        foreach (KeyValuePair<HashedKey<Address>, bool> kv in snapshot.SelfDestructedStorageAddresses)
        {
            Address address = kv.Key;
            selfDestructed[address.Bytes.ToHexString(false)] = kv.Value;
        }
        dump["selfDestructed"] = selfDestructed;

        // 4. StateNodes
        Dictionary<string, string> stateNodes = [];
        foreach (KeyValuePair<HashedKey<TreePath>, TrieNode> kv in snapshot.StateNodes)
        {
            if (kv.Value.FullRlp.Length == 0 && kv.Value.NodeType == NodeType.Unknown) continue;
            TreePath path = kv.Key;
            string key = $"{path.Span.ToHexString(false)}:{path.Length}";
            stateNodes[key] = kv.Value.FullRlp.AsSpan().ToHexString(false);
        }
        dump["stateNodes"] = stateNodes;

        // 5. StorageNodes
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

    internal static SnapshotContent ReadSnapshotFromJson(string jsonPath)
    {
        string jsonContent = File.ReadAllText(jsonPath);
        using JsonDocument doc = JsonDocument.Parse(jsonContent);
        JsonElement root = doc.RootElement;

        SnapshotContent content = new();

        // Deserialize accounts
        if (root.TryGetProperty("accounts", out JsonElement accountsElement))
        {
            foreach (JsonProperty prop in accountsElement.EnumerateObject())
            {
                Address addr = new(Bytes.FromHexString(prop.Name));
                string value = prop.Value.GetString() ?? "";
                if (value == "")
                {
                    content.Accounts[addr] = null;
                }
                else
                {
                    Rlp.ValueDecoderContext ctx = new(Bytes.FromHexString(value));
                    content.Accounts[addr] = AccountDecoder.Slim.Decode(ref ctx);
                }
            }
        }

        // Deserialize storages
        if (root.TryGetProperty("storages", out JsonElement storagesElement))
        {
            foreach (JsonProperty prop in storagesElement.EnumerateObject())
            {
                string[] parts = prop.Name.Split(':');
                Address addr = new(Bytes.FromHexString(parts[0]));
                // Slot is stored as decimal string
                UInt256 slot = UInt256.Parse(parts[1]);
                string value = prop.Value.GetString() ?? "";
                SlotValue? slotValue = value == "" ? null : new SlotValue(Bytes.FromHexString(value));
                content.Storages[(addr, slot)] = slotValue;
            }
        }

        // Deserialize selfDestructed
        if (root.TryGetProperty("selfDestructed", out JsonElement selfDestructElement))
        {
            foreach (JsonProperty prop in selfDestructElement.EnumerateObject())
            {
                Address addr = new(Bytes.FromHexString(prop.Name));
                bool value = prop.Value.GetBoolean();
                content.SelfDestructedStorageAddresses[addr] = value;
            }
        }

        // Deserialize stateNodes
        if (root.TryGetProperty("stateNodes", out JsonElement stateNodesElement))
        {
            foreach (JsonProperty prop in stateNodesElement.EnumerateObject())
            {
                string[] parts = prop.Name.Split(':');
                Hash256 pathHash = new(Bytes.FromHexString(parts[0]));
                int length = int.Parse(parts[1]);
                TreePath path = new(pathHash, length);
                byte[] nodeRlp = Bytes.FromHexString(prop.Value.GetString() ?? "");
                content.StateNodes[path] = new TrieNode(NodeType.Unknown, nodeRlp);
            }
        }

        // Deserialize storageNodes
        if (root.TryGetProperty("storageNodes", out JsonElement storageNodesElement))
        {
            foreach (JsonProperty prop in storageNodesElement.EnumerateObject())
            {
                string[] parts = prop.Name.Split(':');
                Hash256 hash = new(Bytes.FromHexString(parts[0]));
                Hash256 pathHash = new(Bytes.FromHexString(parts[1]));
                int length = int.Parse(parts[2]);
                TreePath path = new(pathHash, length);
                byte[] nodeRlp = Bytes.FromHexString(prop.Value.GetString() ?? "");
                content.StorageNodes[(hash, path)] = new TrieNode(NodeType.Unknown, nodeRlp);
            }
        }

        return content;
    }

    internal static void ValidatePersistedSnapshot(Snapshot snapshot, PersistedSnapshot persisted, PersistedSnapshotBloomFilterManager bloomManager, bool dumpWhenFailed = true)
    {
        string filename = $"broken.{snapshot.From.BlockNumber}.{snapshot.To.BlockNumber}.json";

        using PersistedSnapshotBloom bloom = bloomManager.LeaseOrSentinel(persisted.To);

        try
        {
            // 1. Accounts
            foreach (KeyValuePair<HashedKey<Address>, Account?> kv in snapshot.Accounts)
            {
                Address address = kv.Key;
                ValueHash256 addressHash = ValueKeccak.Compute(address.Bytes);
                if (!persisted.TryGetAccount(in addressHash, out Account? acc))
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

            // 2. Storages
            foreach (KeyValuePair<HashedKey<(Address, UInt256)>, SlotValue?> kv in snapshot.Storages)
            {
                (Address addr, UInt256 slot) = kv.Key.Key;
                ValueHash256 addrHash = ValueKeccak.Compute(addr.Bytes);
                SlotValue slotValue = default;
                if (!persisted.TryGetSlot(in addrHash, slot, ref slotValue))
                    throw new InvalidOperationException($"Storage {addr}:{slot} not found in persisted snapshot");

                SlotValue expected = kv.Value ?? default;
                if (!slotValue.AsReadOnlySpan.SequenceEqual(expected.AsReadOnlySpan))
                    throw new InvalidOperationException($"Storage {addr}:{slot} mismatch");
            }

            // 3. SelfDestructedStorageAddresses
            foreach (KeyValuePair<HashedKey<Address>, bool> kv in snapshot.SelfDestructedStorageAddresses)
            {
                Address address = kv.Key;
                ValueHash256 addressHash = ValueKeccak.Compute(address.Bytes);
                bool? flag = persisted.TryGetSelfDestructFlag(in addressHash) ?? throw new InvalidOperationException($"SelfDestruct {address} not found in persisted snapshot");
                if (flag.Value != kv.Value)
                    throw new InvalidOperationException($"SelfDestruct {address} mismatch: expected {kv.Value}, got {flag.Value}");
            }

            // 4. StateNodes
            foreach (KeyValuePair<HashedKey<TreePath>, TrieNode> kv in snapshot.StateNodes)
            {
                if (kv.Value.FullRlp.Length == 0 && kv.Value.NodeType == NodeType.Unknown) continue;
                TreePath path = kv.Key;
                if (!persisted.TryLoadStateNodeRlp(in path, out byte[]? nodeRlp))
                    throw new InvalidOperationException($"StateNode at path length {path.Length} not found in persisted snapshot");
                if (!nodeRlp!.AsSpan().SequenceEqual(kv.Value.FullRlp.AsSpan()))
                    throw new InvalidOperationException($"StateNode at path length {path.Length} RLP mismatch");
            }

            // 5. StorageNodes
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
    private sealed class ThrowingPersistenceReader : IPersistence.IPersistenceReader
    {
        public void Dispose() { }
        public Account? GetAccount(Address address) =>
            throw new InvalidOperationException("Value not found in source snapshots");
        public bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue) =>
            throw new InvalidOperationException("Value not found in source snapshots");
        public StateId CurrentState => new(0, Keccak.EmptyTreeHash);
        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) =>
            throw new InvalidOperationException("Value not found in source snapshots");
        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) =>
            throw new InvalidOperationException("Value not found in source snapshots");
        public byte[]? GetAccountRaw(in ValueHash256 addrHash) =>
            throw new InvalidOperationException("Value not found in source snapshots");
        public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref SlotValue value) =>
            throw new InvalidOperationException("Value not found in source snapshots");
        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) =>
            throw new InvalidOperationException("Value not found in source snapshots");
        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) =>
            throw new InvalidOperationException("Value not found in source snapshots");
        public bool IsPreimageMode => false;
    }
}
