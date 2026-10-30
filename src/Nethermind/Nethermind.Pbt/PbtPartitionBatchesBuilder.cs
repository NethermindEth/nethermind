// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>Routes complete-key leaf changes to their partition's builder and builds them into one <see cref="PbtPartitionBatches"/>.</summary>
/// <remarks>Does not own the builders; <see cref="Dispose"/> disposes them for a caller that does not pool them.</remarks>
public readonly struct PbtPartitionBatchesBuilder(
    PbtWriteBatchBuilder<PbtPath> account,
    PbtWriteBatchBuilder<PbtPath> code,
    PbtWriteBatchBuilder<PbtStoragePath> storage) : IDisposable
{
    public PbtWriteBatchBuilder<PbtPath> Account => account;
    public PbtWriteBatchBuilder<PbtPath> Code => code;
    public PbtWriteBatchBuilder<PbtStoragePath> Storage => storage;

    public int Count => account.Count + code.Count + storage.Count;

    /// <summary>Sets an account-zone or code-zone leaf; <see langword="null"/> deletes it.</summary>
    public void SetLeaf(PbtPath key, ValueHash256? value)
    {
        PbtPartition? partition = PbtPartitions.PartitionOf(key);
        if (partition == PbtPartition.Account) account.SetLeaf(key, value);
        else if (partition == PbtPartition.Code) code.SetLeaf(key, value);
        else throw new ArgumentException("A canonical account or code key is required.", nameof(key));
    }

    /// <summary>Sets a storage-zone leaf; <see langword="null"/> deletes it.</summary>
    public void SetLeaf(in PbtStoragePath key, ValueHash256? value) => storage.SetLeaf(key, value);

    /// <summary>Sets the leaf of any canonical account, code or storage key.</summary>
    public void Set(in PbtVariableTreeKey key, in ValueHash256 value)
    {
        PbtPartition? partition = PbtPartitions.PartitionOf(key);
        if (partition == PbtPartition.Storage) storage.Set((PbtStoragePath)key, value);
        else if (partition == PbtPartition.Code) code.Set((PbtPath)key, value);
        else if (partition == PbtPartition.Account) account.Set((PbtPath)key, value);
        else throw new InvalidDataException($"A canonical account, code or storage key is required: {key}.");
    }

    /// <summary>Builds the pending changes of every partition; the builders keep them until <see cref="Reset"/>.</summary>
    public PbtPartitionBatches Build()
    {
        PbtPartitionBatches changes = new();
        try
        {
            // The fold appends the code zone to the account zone's operations.
            changes.Account = account.Build(code.Count);
            changes.Code = code.Build();
            changes.Storage = storage.Build();
            return changes;
        }
        catch
        {
            changes.Dispose();
            throw;
        }
    }

    public void Reset()
    {
        account.Reset();
        code.Reset();
        storage.Reset();
    }

    public void Dispose()
    {
        account.Dispose();
        code.Dispose();
        storage.Dispose();
    }
}
