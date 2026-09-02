// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Test;

/// <summary>Drives <see cref="TrieUpdater"/> over a persistent variable-length complete-key store.</summary>
internal sealed class PbtTreeHarness(PbtNodeLayout layout = PbtNodeLayout.Record)
{
    private PbtPhysicalNodeStore _store = new(layout);

    public ValueHash256 RootHash { get; private set; }
    public PbtNodeLayout Layout
    {
        get => _store.Layout;
        set => _store.Layout = value;
    }

    public IReadOnlyList<PbtNodeRecord> Nodes => _store.EnumerateRecords();
    public IReadOnlyList<PbtPhysicalPayload> PhysicalPayloads => _store.ExportPhysicalPayloads();

    public ValueHash256 ApplyBatch(IEnumerable<(byte[] Key, byte[]? Value)> writes)
    {
        PbtWriteBatch batch = new();
        foreach ((byte[] key, byte[]? value) in writes)
        {
            PbtFullKey fullKey = new(key);
            if (value is null) batch.Delete(fullKey);
            else batch.Set(fullKey, new ValueHash256(value));
        }
        RootHash = TrieUpdater.UpdateRoot(_store, RootHash, batch);
        return RootHash;
    }

    public bool TryGetNode(PbtNodeLocator locator, out byte[]? encoding)
    {
        encoding = _store.GetNode(locator);
        return encoding is not null;
    }

    public void Reopen() =>
        _store = PbtPhysicalNodeStore.FromPhysicalPayloads(Layout, RootHash, PhysicalPayloads);

    public string[] CanonicalRecords()
    {
        IReadOnlyList<PbtNodeRecord> records = Nodes;
        string[] result = new string[records.Count];
        for (int index = 0; index < result.Length; index++)
        {
            PbtNodeRecord record = records[index];
            result[index] = Convert.ToHexString(record.Locator.Encode()) + Convert.ToHexString(record.Encoding.Span);
        }
        return result;
    }
}
