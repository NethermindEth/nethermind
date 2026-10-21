// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Snap;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;

namespace Nethermind.LightClient;

internal static class HostedStorageDownload
{
    internal sealed record Page(KeyValuePair<ValueHash256, UInt256>[] Slots, bool More);

    internal static async Task<IReadOnlyDictionary<ValueHash256, UInt256>> DownloadAsync(
        Hash256 storageRoot, Func<ValueHash256, CancellationToken, Task<Page>> fetch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Dictionary<ValueHash256, UInt256> storage = [];
        if (storageRoot == Keccak.EmptyTreeHash) return storage;

        ValueHash256 start = ValueKeccak.Zero;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Page page = await fetch(start, cancellationToken);
            ValueHash256 last = start;
            foreach ((ValueHash256 path, UInt256 value) in page.Slots)
            {
                if (path < start || !storage.TryAdd(path, value))
                    throw new InvalidDataException("Hosted storage download did not advance its verified range.");
                last = path;
            }
            if (!page.More) break;
            if (page.Slots.Length == 0 || last == ValueKeccak.MaxValue)
                throw new InvalidDataException("Hosted storage download made no progress.");
            start = last.IncrementPath();
        }

        ExecutionProofVerifier.VerifyStorageSnapshot(storageRoot, storage, cancellationToken);
        return storage;
    }

    internal static Page VerifyPage(PathWithAccount account, ValueHash256 start, SlotsAndProofs response, ILogManager logs)
    {
        if (response.PathsAndSlots.Count > 1 || response.Proofs.Count > 256)
            throw new InvalidDataException("Execution peer returned an invalid hosted storage range size.");
        for (int i = 0; i < response.Proofs.Count; i++)
            if (response.Proofs[i].Length is 0 or > ExecutionProofVerifier.MaxNodeBytes)
                throw new InvalidDataException("Hosted storage range contains an invalid proof node.");
        IReadOnlyList<PathWithStorageSlot> slots = response.PathsAndSlots.Count == 0 ? [] : response.PathsAndSlots[0];
        if (slots.Count > 131072)
            throw new InvalidDataException("Hosted storage range contains too many slots.");
        int bytes = 0;
        foreach (PathWithStorageSlot slot in slots)
        {
            if (slot.SlotRlpValue.Length is 0 or > 33)
                throw new InvalidDataException("Hosted storage range contains an invalid storage value.");
            bytes = checked(bytes + 32 + slot.SlotRlpValue.Length);
            if (bytes > 3 * 1024 * 1024)
                throw new InvalidDataException("Hosted storage range exceeds the response limit.");
        }
        using MemDb db = new();
        PatriciaSnapTrieFactory factory = new(new NodeStorage(db), NullDb.Instance, logs);
        (AddRangeResult result, bool more, _, _) = SnapProviderHelper.AddStorageRange(
            factory, account, slots, start, ValueKeccak.MaxValue, response.Proofs);
        if (result != AddRangeResult.OK)
            throw new InvalidDataException("Execution peer returned an invalid hosted storage range proof.");

        KeyValuePair<ValueHash256, UInt256>[] owned = new KeyValuePair<ValueHash256, UInt256>[slots.Count];
        for (int i = 0; i < slots.Count; i++)
            owned[i] = new(slots[i].Path, ExecutionProofVerifier.DecodeStorageRlp(slots[i].SlotRlpValue));
        return new(owned, more);
    }
}
