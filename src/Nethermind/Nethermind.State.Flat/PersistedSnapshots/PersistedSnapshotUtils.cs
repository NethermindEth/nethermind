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
                if (!persisted.TryGetAccount(bloom, address, out Account? acc))
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
                SlotValue slotValue = default;
                if (!persisted.TryGetSlot(bloom, addr, slot, ref slotValue))
                    throw new InvalidOperationException($"Storage {addr}:{slot} not found in persisted snapshot");

                SlotValue expected = kv.Value ?? default;
                if (!slotValue.AsReadOnlySpan.SequenceEqual(expected.AsReadOnlySpan))
                    throw new InvalidOperationException($"Storage {addr}:{slot} mismatch");
            }

            // 3. SelfDestructedStorageAddresses
            foreach (KeyValuePair<HashedKey<Address>, bool> kv in snapshot.SelfDestructedStorageAddresses)
            {
                Address address = kv.Key;
                bool? flag = persisted.TryGetSelfDestructFlag(bloom, address) ?? throw new InvalidOperationException($"SelfDestruct {address} not found in persisted snapshot");
                if (flag.Value != kv.Value)
                    throw new InvalidOperationException($"SelfDestruct {address} mismatch: expected {kv.Value}, got {flag.Value}");
            }

            // 4. StateNodes
            foreach (KeyValuePair<HashedKey<TreePath>, TrieNode> kv in snapshot.StateNodes)
            {
                if (kv.Value.FullRlp.Length == 0 && kv.Value.NodeType == NodeType.Unknown) continue;
                TreePath path = kv.Key;
                if (!persisted.TryLoadStateNodeRlp(bloom, path, out byte[]? nodeRlp))
                    throw new InvalidOperationException($"StateNode at path length {path.Length} not found in persisted snapshot");
                if (!nodeRlp!.AsSpan().SequenceEqual(kv.Value.FullRlp.AsSpan()))
                    throw new InvalidOperationException($"StateNode at path length {path.Length} RLP mismatch");
            }

            // 5. StorageNodes
            foreach (KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode> kv in snapshot.StorageNodes)
            {
                if (kv.Value.FullRlp.Length == 0 && kv.Value.NodeType == NodeType.Unknown) continue;
                (Hash256 hash, TreePath path) = kv.Key.Key;
                if (!persisted.TryLoadStorageNodeRlp(bloom, hash, path, out byte[]? nodeRlp))
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
        ArrayPoolList<PersistedSnapshotBloom> bundleBlooms = new(snapshots.Count);
        for (int i = 0; i < snapshots.Count; i++)
        {
            if (!snapshots[i].TryAcquire())
                throw new InvalidOperationException($"Cannot acquire lease for source snapshot {i}");
            bundleSnapshots.Add(snapshots[i]);
            bundleBlooms.Add(PersistedSnapshotBloom.AlwaysTrue);
        }

        using ReadOnlySnapshotBundle bundle = new(
            SnapshotPooledList.Empty(),
            new ThrowingPersistenceReader(),
            false,
            bundleSnapshots,
            bundleBlooms);

        try
        {
            using WholeReadSession compactedSession = compactedSnapshot.BeginWholeReadSession();
            ReadOnlySpan<byte> compactedData = compactedSession.GetSpan();
            SpanByteReader reader = new(compactedData);

            // Determine if this compacted snapshot has NodeRefs by checking metadata flag
            bool hasNodeRefs = false;
            if (TryGet(compactedData, PersistedSnapshot.MetadataTag, out ReadOnlySpan<byte> metaCol))
                hasNodeRefs = TryGet(metaCol, "noderefs"u8, out _);

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
            {
                HsstReader<SpanByteReader, NoOpPin> outerReader = new(in reader);
                if (outerReader.TrySeek(PersistedSnapshot.AccountColumnTag, out _))
                {
                    Span<byte> slotBytes = stackalloc byte[32];
                    Bound accountColumnBound = outerReader.GetBound();
                    using HsstEnumerator<SpanByteReader, NoOpPin> addrEnum = new(in reader, accountColumnBound);
                    while (addrEnum.MoveNext())
                    {
                        ReadOnlySpan<byte> addrKey = SliceFromBound(compactedData, addrEnum.Current.KeyBound);
                        Address address = new(addrKey);
                        ReadOnlySpan<byte> perAddrSpan = SliceFromBound(compactedData, addrEnum.Current.ValueBound);

                        // Validate account sub-tag (0x03). Presence-marker encoding under
                        // DenseByteIndex: length 0 = absent (gap-filled), [0x00] = deleted,
                        // RLP-bytes = present.
                        if (TryGet(perAddrSpan, PersistedSnapshot.AccountSubTag, out ReadOnlySpan<byte> accountRlp)
                            && accountRlp.Length > 0)
                        {
                            Account? bundleAccount = bundle.GetAccount(address);
                            if (accountRlp.Length == 1 && accountRlp[0] == 0x00)
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

                        // Validate self-destruct sub-tag (0x02). Presence-marker encoding:
                        // length 0 = absent, [0x00] = destructed, [0x01] = new account.
                        if (TryGet(perAddrSpan, PersistedSnapshot.SelfDestructSubTag, out ReadOnlySpan<byte> sdValue)
                            && sdValue.Length > 0)
                        {
                            bool actual = sdValue[0] != 0x00; // true = new account, false = destructed

                            bool? expected = null;
                            for (int i = 0; i < snapshots.Count; i++)
                            {
                                bool? flag = snapshots[i].TryGetSelfDestructFlag(PersistedSnapshotBloom.AlwaysTrue, address);
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
                        if (TryGetBound(perAddrSpan, PersistedSnapshot.SlotSubTag, out int slotOff, out int slotLen))
                        {
                            // slotOff/slotLen are relative to perAddrSpan; reframe to compactedData
                            long perAddrAbs = addrEnum.Current.ValueBound.Offset;
                            Bound slotBound = new(perAddrAbs + slotOff, slotLen);
                            using HsstEnumerator<SpanByteReader, NoOpPin> prefixEnum = new(in reader, slotBound);
                            while (prefixEnum.MoveNext())
                            {
                                ReadOnlySpan<byte> prefixKey = SliceFromBound(compactedData, prefixEnum.Current.KeyBound);
                                Bound suffixBound = prefixEnum.Current.ValueBound;

                                using HsstEnumerator<SpanByteReader, NoOpPin> suffixEnum = new(in reader, suffixBound);
                                while (suffixEnum.MoveNext())
                                {
                                    ReadOnlySpan<byte> suffixKey = SliceFromBound(compactedData, suffixEnum.Current.KeyBound);
                                    ReadOnlySpan<byte> slotValue = SliceFromBound(compactedData, suffixEnum.Current.ValueBound);

                                    prefixKey.CopyTo(slotBytes);
                                    suffixKey.CopyTo(slotBytes[31..]);
                                    UInt256 slot = new(slotBytes, true);

                                    byte[]? bundleSlot = bundle.GetSlot(address, slot, -1);
                                    ReadOnlySpan<byte> expectedSlot = bundleSlot ?? ReadOnlySpan<byte>.Empty;

                                    // The two paths use different "zero" encodings: compacted stores the slot
                                    // value via WithoutLeadingZeros() — a fully-zero slot collapses to empty.
                                    // bundle.GetSlot routes through SlotValue.ToEvmBytes() which encodes zero
                                    // as a single 0x00 byte. Normalise both to zero-stripped form before
                                    // comparing so this isn't a spurious mismatch.
                                    ReadOnlySpan<byte> compactedNorm = slotValue.WithoutLeadingZeros();
                                    ReadOnlySpan<byte> expectedNorm = expectedSlot.WithoutLeadingZeros();
                                    if (!compactedNorm.SequenceEqual(expectedNorm))
                                    {
                                        // Probe each source independently — bypass the bundle's bloom/short-circuit
                                        // so we can tell apart "compactor wrote wrong value" from "bundle/bloom
                                        // hides the real value". For each source we report: bloom verdict,
                                        // post-bloom TryGetSlot result, and a raw HsstReader seek (bloom-free).
                                        System.Text.StringBuilder sb = new();
                                        sb.Append($"Storage {address}:{slot}: mismatch. ")
                                          .Append($"compactedValue={slotValue.ToHexString()} (len={slotValue.Length}); ")
                                          .Append($"bundleValue={(bundleSlot is null ? "<null>" : bundleSlot.AsSpan().ToHexString())} (len={(bundleSlot?.Length ?? 0)}); ")
                                          .Append($"prefixKey={prefixKey.ToHexString()} suffixKey={suffixKey.ToHexString()} ");
                                        for (int i = 0; i < snapshots.Count; i++)
                                        {
                                            SlotValue sv = default;
                                            bool tryGetOk = snapshots[i].TryGetSlot(PersistedSnapshotBloom.AlwaysTrue, address, slot, ref sv);
                                            sb.Append($"src[{i}](id={snapshots[i].Id} {snapshots[i].From.BlockNumber}->{snapshots[i].To.BlockNumber}): ");
                                            sb.Append($"TryGetSlot={tryGetOk}");
                                            if (tryGetOk) sb.Append($"={sv.AsReadOnlySpan.ToHexString()}");
                                            sb.Append("; ");
                                        }
                                        if (dumpWhenFailed) DumpPersistedSnapshotsToJson(snapshots, filename);
                                        throw new InvalidOperationException(sb.ToString());
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // StateTopNodes (0x05): key = 3-byte encoded TreePath (length 0-5)
            {
                HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
                if (r.TrySeek(PersistedSnapshot.StateTopNodesTag, out _))
                {
                    using HsstEnumerator<SpanByteReader, NoOpPin> e = new(in reader, r.GetBound());
                    while (e.MoveNext())
                    {
                        ReadOnlySpan<byte> key = SliceFromBound(compactedData, e.Current.KeyBound);
                        ReadOnlySpan<byte> rawValue = SliceFromBound(compactedData, e.Current.ValueBound);
                        ReadOnlySpan<byte> value = ResolveNodeRefForValidation(rawValue, snapshotLookup, hasNodeRefs);
                        TreePath path = DecodeWith3Byte(key);

                        byte[]? bundleRlp = bundle.TryLoadStateRlp(path, Keccak.Zero, ReadFlags.None);
                        if (!value.SequenceEqual(bundleRlp ?? ReadOnlySpan<byte>.Empty))
                            throw new InvalidOperationException($"StateTopNode path {path}: RLP mismatch. Got {value.ToHexString()}, Expected: {bundleRlp?.ToHexString()}");
                    }
                }
            }

            // StateNodes (0x03): key = 8-byte encoded TreePath (length 6-15)
            {
                HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
                if (r.TrySeek(PersistedSnapshot.StateNodeTag, out _))
                {
                    using HsstEnumerator<SpanByteReader, NoOpPin> e = new(in reader, r.GetBound());
                    while (e.MoveNext())
                    {
                        ReadOnlySpan<byte> key = SliceFromBound(compactedData, e.Current.KeyBound);
                        ReadOnlySpan<byte> rawValue = SliceFromBound(compactedData, e.Current.ValueBound);
                        ReadOnlySpan<byte> value = ResolveNodeRefForValidation(rawValue, snapshotLookup, hasNodeRefs);
                        TreePath path = DecodeWith8Byte(key);

                        byte[]? bundleRlp = bundle.TryLoadStateRlp(path, Keccak.Zero, ReadFlags.None);
                        if (!value.SequenceEqual(bundleRlp ?? ReadOnlySpan<byte>.Empty))
                            throw new InvalidOperationException($"StateNode path length {path.Length}: RLP mismatch");
                    }
                }
            }

            // StateNodeFallback (0x06): key = 33 bytes (32-byte path + 1-byte length)
            {
                HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
                if (r.TrySeek(PersistedSnapshot.StateNodeFallbackTag, out _))
                {
                    using HsstEnumerator<SpanByteReader, NoOpPin> e = new(in reader, r.GetBound());
                    while (e.MoveNext())
                    {
                        ReadOnlySpan<byte> key = SliceFromBound(compactedData, e.Current.KeyBound);
                        ReadOnlySpan<byte> rawValue = SliceFromBound(compactedData, e.Current.ValueBound);
                        ReadOnlySpan<byte> value = ResolveNodeRefForValidation(rawValue, snapshotLookup, hasNodeRefs);
                        TreePath path = new(new Hash256(key[..32]), key[32]);

                        byte[]? bundleRlp = bundle.TryLoadStateRlp(path, Keccak.Zero, ReadFlags.None);
                        if (!value.SequenceEqual(bundleRlp ?? ReadOnlySpan<byte>.Empty))
                            throw new InvalidOperationException($"StateNodeFallback path length {key[32]}: RLP mismatch");
                    }
                }
            }

            // StorageNodes (0x07): nested HSST. addr hash prefix(20) → 8-byte encoded TreePath → RLP/NodeRef
            {
                HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
                if (r.TrySeek(PersistedSnapshot.StorageNodeTag, out _))
                {
                    Span<byte> fullHashBytes = stackalloc byte[32];
                    using HsstEnumerator<SpanByteReader, NoOpPin> addrEnum = new(in reader, r.GetBound());
                    while (addrEnum.MoveNext())
                    {
                        ReadOnlySpan<byte> addrHashPrefix = SliceFromBound(compactedData, addrEnum.Current.KeyBound);
                        Bound innerBound = addrEnum.Current.ValueBound;

                        fullHashBytes.Clear();
                        addrHashPrefix.CopyTo(fullHashBytes);
                        Hash256 addrHash = new(fullHashBytes);

                        using HsstEnumerator<SpanByteReader, NoOpPin> innerEnum = new(in reader, innerBound);
                        while (innerEnum.MoveNext())
                        {
                            ReadOnlySpan<byte> pathKey = SliceFromBound(compactedData, innerEnum.Current.KeyBound);
                            ReadOnlySpan<byte> rawValue = SliceFromBound(compactedData, innerEnum.Current.ValueBound);
                            ReadOnlySpan<byte> nodeRlp = ResolveNodeRefForValidation(rawValue, snapshotLookup, hasNodeRefs);
                            TreePath path = DecodeWith8Byte(pathKey);

                            byte[]? bundleRlp = bundle.TryLoadStorageRlp(addrHash, path, Keccak.Zero, ReadFlags.None);
                            if (!nodeRlp.SequenceEqual(bundleRlp ?? ReadOnlySpan<byte>.Empty))
                                throw new InvalidOperationException($"StorageNode {addrHash} path length {path.Length}: RLP mismatch");
                        }
                    }
                }
            }

            // StorageNodeFallback (0x08): nested HSST. addr hash prefix(20) → 33-byte TreePath → RLP/NodeRef
            {
                HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
                if (r.TrySeek(PersistedSnapshot.StorageNodeFallbackTag, out _))
                {
                    Span<byte> fullHashBytesFb = stackalloc byte[32];
                    using HsstEnumerator<SpanByteReader, NoOpPin> addrEnum = new(in reader, r.GetBound());
                    while (addrEnum.MoveNext())
                    {
                        ReadOnlySpan<byte> addrHashPrefix = SliceFromBound(compactedData, addrEnum.Current.KeyBound);
                        Bound innerBound = addrEnum.Current.ValueBound;

                        fullHashBytesFb.Clear();
                        addrHashPrefix.CopyTo(fullHashBytesFb);
                        Hash256 addrHash = new(fullHashBytesFb);

                        using HsstEnumerator<SpanByteReader, NoOpPin> innerEnum = new(in reader, innerBound);
                        while (innerEnum.MoveNext())
                        {
                            ReadOnlySpan<byte> pathKey = SliceFromBound(compactedData, innerEnum.Current.KeyBound);
                            ReadOnlySpan<byte> rawValue = SliceFromBound(compactedData, innerEnum.Current.ValueBound);
                            ReadOnlySpan<byte> nodeRlp = ResolveNodeRefForValidation(rawValue, snapshotLookup, hasNodeRefs);
                            TreePath path = new(new Hash256(pathKey[..32]), pathKey[32]);

                            byte[]? bundleRlp = bundle.TryLoadStorageRlp(addrHash, path, Keccak.Zero, ReadFlags.None);
                            if (!nodeRlp.SequenceEqual(bundleRlp ?? ReadOnlySpan<byte>.Empty))
                                throw new InvalidOperationException($"StorageNodeFallback {addrHash} path length {pathKey[32]}: RLP mismatch");
                        }
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
        {
            using WholeReadSession session = snapshots[i].BeginWholeReadSession();
            base64List.Add(Convert.ToBase64String(session.GetSpan()));
        }
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
        return snapshot.ReadRlpItem(nodeRef.RlpDataOffset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryGet(ReadOnlySpan<byte> data, scoped ReadOnlySpan<byte> key, out ReadOnlySpan<byte> value)
    {
        SpanByteReader r = new(data);
        HsstReader<SpanByteReader, NoOpPin> hsst = new(in r);
        if (!hsst.TrySeek(key, out _)) { value = default; return false; }
        Bound b = hsst.GetBound();
        value = data.Slice(checked((int)b.Offset), checked((int)b.Length));
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryGetBound(ReadOnlySpan<byte> data, scoped ReadOnlySpan<byte> key, out int offset, out int length)
    {
        SpanByteReader r = new(data);
        HsstReader<SpanByteReader, NoOpPin> hsst = new(in r);
        if (!hsst.TrySeek(key, out _)) { offset = 0; length = 0; return false; }
        Bound b = hsst.GetBound();
        offset = checked((int)b.Offset);
        length = checked((int)b.Length);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> SliceFromBound(ReadOnlySpan<byte> data, Bound b) =>
        data.Slice(checked((int)b.Offset), checked((int)b.Length));

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
