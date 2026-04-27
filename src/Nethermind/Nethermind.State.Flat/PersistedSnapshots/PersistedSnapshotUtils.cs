// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.BlockRangeTrieForest;
using Nethermind.State.Flat.Persistence;
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

    internal static void ValidatePersistedSnapshot(Snapshot snapshot, PersistedSnapshot persisted, bool dumpWhenFailed = true)
    {
        string filename = $"broken.{snapshot.From.BlockNumber}.{snapshot.To.BlockNumber}.json";

        try
        {
            // 1. Accounts
            foreach (KeyValuePair<HashedKey<Address>, Account?> kv in snapshot.Accounts)
            {
                Address address = kv.Key;
                if (!persisted.TryGetAccount(address, out ReadOnlySpan<byte> rlp))
                    throw new InvalidOperationException($"Account {address} not found in persisted snapshot");

                if (kv.Value is null)
                {
                    if (!rlp.IsEmpty)
                        throw new InvalidOperationException($"Account {address} should be null but has RLP data");
                }
                else
                {
                    Rlp.ValueDecoderContext ctx = new(rlp);
                    Account? acc = AccountDecoder.Slim.Decode(ref ctx);
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
                if (!persisted.TryGetSlot(addr, slot, out ReadOnlySpan<byte> slotBytes))
                    throw new InvalidOperationException($"Storage {addr}:{slot} not found in persisted snapshot");

                ReadOnlySpan<byte> expected = kv.Value.HasValue
                    ? kv.Value.Value.AsReadOnlySpan.WithoutLeadingZeros()
                    : [];
                if (!slotBytes.SequenceEqual(expected))
                    throw new InvalidOperationException($"Storage {addr}:{slot} mismatch");
            }

            // 3. SelfDestructedStorageAddresses
            foreach (KeyValuePair<HashedKey<Address>, bool> kv in snapshot.SelfDestructedStorageAddresses)
            {
                Address address = kv.Key;
                bool? flag = persisted.TryGetSelfDestructFlag(address) ?? throw new InvalidOperationException($"SelfDestruct {address} not found in persisted snapshot");
                if (flag.Value != kv.Value)
                    throw new InvalidOperationException($"SelfDestruct {address} mismatch: expected {kv.Value}, got {flag.Value}");
            }

            // 4. StateNodes
            foreach (KeyValuePair<HashedKey<TreePath>, TrieNode> kv in snapshot.StateNodes)
            {
                if (kv.Value.FullRlp.Length == 0 && kv.Value.NodeType == NodeType.Unknown) continue;
                TreePath path = kv.Key;
                if (!persisted.TryLoadStateNodeRlp(path, out ReadOnlySpan<byte> nodeRlp))
                    throw new InvalidOperationException($"StateNode at path length {path.Length} not found in persisted snapshot");
                if (!nodeRlp.SequenceEqual(kv.Value.FullRlp.AsSpan()))
                    throw new InvalidOperationException($"StateNode at path length {path.Length} RLP mismatch");
            }

            // 5. StorageNodes
            foreach (KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode> kv in snapshot.StorageNodes)
            {
                if (kv.Value.FullRlp.Length == 0 && kv.Value.NodeType == NodeType.Unknown) continue;
                (Hash256 hash, TreePath path) = kv.Key.Key;
                if (!persisted.TryLoadStorageNodeRlp(hash, path, out ReadOnlySpan<byte> nodeRlp))
                    throw new InvalidOperationException($"StorageNode {hash} at path length {path.Length} not found in persisted snapshot");
                if (!nodeRlp.SequenceEqual(kv.Value.FullRlp.AsSpan()))
                    throw new InvalidOperationException($"StorageNode {hash} at path length {path.Length} RLP mismatch");
            }
        }
        catch (InvalidOperationException ex)
        {
            if (dumpWhenFailed) DumpSnapshotToJson(snapshot, filename);
            throw new InvalidOperationException($"{ex.Message}. Dumped snapshot to {filename}", ex);
        }
    }

    internal static void ValidateCompactedPersistedSnapshot(
        PersistedSnapshot compactedSnapshot,
        PersistedSnapshotList snapshots,
        bool dumpWhenFailed)
    {
        StateId from = snapshots[0].From;
        StateId to = snapshots[^1].To;
        string filename = $"broken.compacted.{from.BlockNumber}.{to.BlockNumber}.json";

        // Build a new PersistedSnapshotList with leases for the bundle
        PersistedSnapshotList bundleSnapshots = new(snapshots.Count);
        for (int i = 0; i < snapshots.Count; i++)
        {
            if (!snapshots[i].TryAcquire())
                throw new InvalidOperationException($"Cannot acquire lease for source snapshot {i}");
            bundleSnapshots.Add(snapshots[i]);
        }

        using ReadOnlySnapshotBundle bundle = new(
            SnapshotPooledList.Empty(),
            new ThrowingPersistenceReader(),
            false,
            bundleSnapshots,
            NullBlockRangeTrieForest.Instance,
            0);

        try
        {
            ReadOnlySpan<byte> compactedData = compactedSnapshot.GetSpan();
            Hsst.Hsst outer = new(compactedData);

            // Determine if this compacted snapshot has NodeRefs by checking metadata flag
            bool hasNodeRefs = false;
            if (outer.TryGet(PersistedSnapshot.MetadataTag, out ReadOnlySpan<byte> metaCol))
            {
                Hsst.Hsst metaHsst = new(metaCol);
                hasNodeRefs = metaHsst.TryGet("noderefs"u8, out _);
            }

            // Build transitive lookup including referenced snapshots from compacted sources
            Dictionary<int, PersistedSnapshot> snapshotLookup = [];
            for (int i = 0; i < snapshots.Count; i++)
            {
                snapshotLookup.TryAdd(snapshots[i].Id, snapshots[i]);
                if (snapshots[i].ReferencedSnapshots is { } refs)
                {
                    foreach (PersistedSnapshot refSnapshot in refs)
                        snapshotLookup.TryAdd(refSnapshot.Id, refSnapshot);
                }
            }

            // Unified Account Column (0x01): address → per-address HSST { slots, self-destruct, account }
            if (outer.TryGet(PersistedSnapshot.AccountColumnTag, out ReadOnlySpan<byte> accountColumn))
            {
                Span<byte> slotBytes = stackalloc byte[32];
                Hsst.Hsst addressLevel = new(accountColumn);
                Hsst.Hsst.Enumerator addrEnum = addressLevel.GetEnumerator();
                while (addrEnum.MoveNext())
                {
                    ReadOnlySpan<byte> addrKey = addrEnum.Current.Key;
                    Address address = new(addrKey.ToArray());
                    Hsst.Hsst perAddr = new(addrEnum.Current.Value);

                    // Validate account sub-tag (0x03)
                    if (perAddr.TryGet(PersistedSnapshot.AccountSubTag, out ReadOnlySpan<byte> accountRlp))
                    {
                        Account? bundleAccount = bundle.GetAccount(address);
                        if (accountRlp.IsEmpty)
                        {
                            if (bundleAccount is not null)
                                throw new InvalidOperationException($"Account {address}: compacted=deleted but bundle={bundleAccount}");
                        }
                        else
                        {
                            Rlp.ValueDecoderContext ctx = new(accountRlp);
                            Account? decoded = AccountDecoder.Slim.Decode(ref ctx) ?? throw new InvalidOperationException($"Account {address}: failed to decode compacted RLP");
                            if (bundleAccount is null)
                                throw new InvalidOperationException($"Account {address}: compacted={decoded} but bundle=null");
                            if (decoded.Balance != bundleAccount.Balance || decoded.Nonce != bundleAccount.Nonce ||
                                decoded.CodeHash != bundleAccount.CodeHash || decoded.StorageRoot != bundleAccount.StorageRoot)
                            {
                                throw new InvalidOperationException($"Account {address}: mismatch");
                            }
                        }
                    }

                    // Validate self-destruct sub-tag (0x02)
                    if (perAddr.TryGet(PersistedSnapshot.SelfDestructSubTag, out ReadOnlySpan<byte> sdValue))
                    {
                        bool actual = !sdValue.IsEmpty; // true = new account (0x01), false = destructed (empty)

                        bool? expected = null;
                        for (int i = 0; i < snapshots.Count; i++)
                        {
                            bool? flag = snapshots[i].TryGetSelfDestructFlag(address);
                            if (flag is null) continue;
                            if (expected is null)
                                expected = flag;
                            else if (flag == false)
                                expected = false;
                        }

                        if (expected is null)
                            throw new InvalidOperationException($"SelfDestruct {address}: in compacted but not in any source snapshot");
                        if (expected.Value != actual)
                            throw new InvalidOperationException($"SelfDestruct {address}: expected={expected.Value}, actual={actual}");
                    }

                    // Validate storage sub-tag (0x01)
                    if (perAddr.TryGet(PersistedSnapshot.SlotSubTag, out ReadOnlySpan<byte> slotData))
                    {
                        Hsst.Hsst prefixLevel = new(slotData);
                        Hsst.Hsst.Enumerator prefixEnum = prefixLevel.GetEnumerator();
                        while (prefixEnum.MoveNext())
                        {
                            ReadOnlySpan<byte> prefixKey = prefixEnum.Current.Key;
                            ReadOnlySpan<byte> suffixData = prefixEnum.Current.Value;

                            Hsst.Hsst suffixLevel = new(suffixData);
                            Hsst.Hsst.Enumerator suffixEnum = suffixLevel.GetEnumerator();
                            while (suffixEnum.MoveNext())
                            {
                                ReadOnlySpan<byte> suffixKey = suffixEnum.Current.Key;
                                ReadOnlySpan<byte> slotValue = suffixEnum.Current.Value;

                                prefixKey.CopyTo(slotBytes);
                                suffixKey.CopyTo(slotBytes[30..]);
                                UInt256 slot = new(slotBytes, true);

                                byte[]? bundleSlot = bundle.GetSlot(address, slot, -1);
                                ReadOnlySpan<byte> expectedSlot = bundleSlot ?? ReadOnlySpan<byte>.Empty;

                                if (!slotValue.SequenceEqual(expectedSlot))
                                    throw new InvalidOperationException($"Storage {address}:{slot}: mismatch");
                            }
                        }
                    }
                }
            }

            // StateTopNodes (0x05): key = 3-byte encoded TreePath (length 0-5)
            if (outer.TryGet(PersistedSnapshot.StateTopNodesTag, out ReadOnlySpan<byte> topNodeColumn))
            {
                Hsst.Hsst topHsst = new(topNodeColumn);
                Hsst.Hsst.Enumerator e = topHsst.GetEnumerator();
                while (e.MoveNext())
                {
                    ReadOnlySpan<byte> key = e.Current.Key;
                    ReadOnlySpan<byte> value = ResolveNodeRefForValidation(e.Current.Value, snapshotLookup, hasNodeRefs);
                    TreePath path = DecodeWith3Byte(key);

                    byte[]? bundleRlp = bundle.TryLoadStateRlp(path, Keccak.Zero, ReadFlags.None);
                    if (!value.SequenceEqual(bundleRlp ?? ReadOnlySpan<byte>.Empty))
                        throw new InvalidOperationException($"StateTopNode path {path}: RLP mismatch. Got {value.ToHexString()}, Expected: {bundleRlp?.ToHexString()}");
                }
            }

            // StateNodes (0x03): key = 8-byte encoded TreePath (length 6-15)
            if (outer.TryGet(PersistedSnapshot.StateNodeTag, out ReadOnlySpan<byte> stateNodeColumn))
            {
                Hsst.Hsst stateHsst = new(stateNodeColumn);
                Hsst.Hsst.Enumerator e = stateHsst.GetEnumerator();
                while (e.MoveNext())
                {
                    ReadOnlySpan<byte> key = e.Current.Key;
                    ReadOnlySpan<byte> value = ResolveNodeRefForValidation(e.Current.Value, snapshotLookup, hasNodeRefs);
                    TreePath path = DecodeWith8Byte(key);

                    byte[]? bundleRlp = bundle.TryLoadStateRlp(path, Keccak.Zero, ReadFlags.None);
                    if (!value.SequenceEqual(bundleRlp ?? ReadOnlySpan<byte>.Empty))
                        throw new InvalidOperationException($"StateNode path length {path.Length}: RLP mismatch");
                }
            }

            // StateNodeFallback (0x06): key = 33 bytes (32-byte path + 1-byte length)
            if (outer.TryGet(PersistedSnapshot.StateNodeFallbackTag, out ReadOnlySpan<byte> fallbackColumn))
            {
                Hsst.Hsst fallbackHsst = new(fallbackColumn);
                Hsst.Hsst.Enumerator e = fallbackHsst.GetEnumerator();
                while (e.MoveNext())
                {
                    ReadOnlySpan<byte> key = e.Current.Key;
                    ReadOnlySpan<byte> value = ResolveNodeRefForValidation(e.Current.Value, snapshotLookup, hasNodeRefs);
                    TreePath path = new(new Hash256(key[..32].ToArray()), key[32]);

                    byte[]? bundleRlp = bundle.TryLoadStateRlp(path, Keccak.Zero, ReadFlags.None);
                    if (!value.SequenceEqual(bundleRlp ?? ReadOnlySpan<byte>.Empty))
                        throw new InvalidOperationException($"StateNodeFallback path length {key[32]}: RLP mismatch");
                }
            }

            // StorageNodes (0x07): nested HSST. addr hash prefix(20) → 8-byte encoded TreePath → RLP/NodeRef
            if (outer.TryGet(PersistedSnapshot.StorageNodeTag, out ReadOnlySpan<byte> storageNodeColumn))
            {
                Span<byte> fullHashBytes = stackalloc byte[32];
                Hsst.Hsst addrLevel = new(storageNodeColumn);
                Hsst.Hsst.Enumerator addrEnum = addrLevel.GetEnumerator();
                while (addrEnum.MoveNext())
                {
                    ReadOnlySpan<byte> addrHashPrefix = addrEnum.Current.Key;
                    ReadOnlySpan<byte> innerData = addrEnum.Current.Value;

                    fullHashBytes.Clear();
                    addrHashPrefix.CopyTo(fullHashBytes);
                    Hash256 addrHash = new(fullHashBytes.ToArray());

                    Hsst.Hsst innerHsst = new(innerData);
                    Hsst.Hsst.Enumerator innerEnum = innerHsst.GetEnumerator();
                    while (innerEnum.MoveNext())
                    {
                        ReadOnlySpan<byte> pathKey = innerEnum.Current.Key;
                        ReadOnlySpan<byte> nodeRlp = ResolveNodeRefForValidation(innerEnum.Current.Value, snapshotLookup, hasNodeRefs);
                        TreePath path = DecodeWith8Byte(pathKey);

                        byte[]? bundleRlp = bundle.TryLoadStorageRlp(addrHash, path, Keccak.Zero, ReadFlags.None);
                        if (!nodeRlp.SequenceEqual(bundleRlp ?? ReadOnlySpan<byte>.Empty))
                            throw new InvalidOperationException($"StorageNode {addrHash} path length {path.Length}: RLP mismatch");
                    }
                }
            }

            // StorageNodeFallback (0x08): nested HSST. addr hash prefix(20) → 33-byte TreePath → RLP/NodeRef
            if (outer.TryGet(PersistedSnapshot.StorageNodeFallbackTag, out ReadOnlySpan<byte> storageNodeFallbackColumn))
            {
                Span<byte> fullHashBytesFb = stackalloc byte[32];
                Hsst.Hsst addrLevel = new(storageNodeFallbackColumn);
                Hsst.Hsst.Enumerator addrEnum = addrLevel.GetEnumerator();
                while (addrEnum.MoveNext())
                {
                    ReadOnlySpan<byte> addrHashPrefix = addrEnum.Current.Key;
                    ReadOnlySpan<byte> innerData = addrEnum.Current.Value;

                    fullHashBytesFb.Clear();
                    addrHashPrefix.CopyTo(fullHashBytesFb);
                    Hash256 addrHash = new(fullHashBytesFb.ToArray());

                    Hsst.Hsst innerHsst = new(innerData);
                    Hsst.Hsst.Enumerator innerEnum = innerHsst.GetEnumerator();
                    while (innerEnum.MoveNext())
                    {
                        ReadOnlySpan<byte> pathKey = innerEnum.Current.Key;
                        ReadOnlySpan<byte> nodeRlp = ResolveNodeRefForValidation(innerEnum.Current.Value, snapshotLookup, hasNodeRefs);
                        TreePath path = new(new Hash256(pathKey[..32].ToArray()), pathKey[32]);

                        byte[]? bundleRlp = bundle.TryLoadStorageRlp(addrHash, path, Keccak.Zero, ReadFlags.None);
                        if (!nodeRlp.SequenceEqual(bundleRlp ?? ReadOnlySpan<byte>.Empty))
                            throw new InvalidOperationException($"StorageNodeFallback {addrHash} path length {pathKey[32]}: RLP mismatch");
                    }
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            if (dumpWhenFailed) DumpPersistedSnapshotsToJson(snapshots, filename);
            throw new InvalidOperationException($"{ex.Message}. Dumped snapshots to {filename}", ex);
        }
    }

    internal static void DumpPersistedSnapshotsToJson(PersistedSnapshotList snapshots, string filename)
    {
        List<string> base64List = [];
        for (int i = 0; i < snapshots.Count; i++)
            base64List.Add(Convert.ToBase64String(snapshots[i].GetSpan()));
        File.WriteAllText(filename, JsonSerializer.Serialize(base64List));
    }

    /// <summary>
    /// Resolve a NodeRef value by finding the referenced snapshot and reading the entry.
    /// Returns the original value if <paramref name="hasNodeRefs"/> is false.
    /// </summary>
    private static ReadOnlySpan<byte> ResolveNodeRefForValidation(
        ReadOnlySpan<byte> value, Dictionary<int, PersistedSnapshot> snapshotLookup, bool hasNodeRefs)
    {
        if (!hasNodeRefs) return value;
        NodeRef nodeRef = NodeRef.Read(value);
        if (!snapshotLookup.TryGetValue(nodeRef.SnapshotId, out PersistedSnapshot? snapshot))
            throw new InvalidOperationException($"Referenced snapshot {nodeRef.SnapshotId} not found during validation");
        return PersistedSnapshot.ResolveValue(snapshot.GetSpan(), nodeRef.ValueLengthOffset);
    }

    private static TreePath DecodeWith3Byte(ReadOnlySpan<byte> key) =>
        TreePath.DecodeWith3Byte(key);

    private static TreePath DecodeWith8Byte(ReadOnlySpan<byte> key) =>
        TreePath.DecodeWith8Byte(key);

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
