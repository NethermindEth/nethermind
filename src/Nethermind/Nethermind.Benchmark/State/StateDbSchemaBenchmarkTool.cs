// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using Paprika.Store;

namespace Nethermind.Benchmarks.State;

public static class StateDbSchemaBenchmarkTool
{
    private const string TrieControlSchema = "trie-control";
    private const string FlatInTrieSchema = "flat-in-trie";
    private const string FlatSchema = "flat";
    private const string PaprikaSchema = "paprika";
    private const int ManifestVersion = 3;
    private const int DefaultStoragePercent = 50;
    private const int DefaultSlotsPerStorageAccount = 4;
    private const int DefaultBatchAccounts = 100_000;
    private const int DefaultAccountCount = 300_000;
    private const int DefaultReadOps = 250_000;
    private const int DefaultWriteOps = 100_000;
    private const int DefaultTrieNodeBytes = 64;
    private const byte DefaultPaprikaHistoryDepth = 128;
    private const long DefaultVerifyProgressInterval = 1_000_000;
    private const string RandomWritesMarkerFileName = "random-writes.dirty";

    public static int Run(string[] args)
    {
        Options options = Options.Parse(args);
        if (options.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        options.Normalize();
        Directory.CreateDirectory(options.BasePath);
        Console.WriteLine("State DB schema benchmark");
        Console.WriteLine($"Base path: {options.BasePath}");
        Console.WriteLine($"Schemas: {options.Schemas}");
        if (ContainsTrieControlSchema(options.Schemas))
        {
            Console.WriteLine("Schema note: trie-control is a synthetic flat-in-trie control, not a full regular MPT benchmark.");
        }

        Console.WriteLine($"Accounts: {(options.TargetBytes.HasValue ? "until target size" : options.AccountCount.ToString(CultureInfo.InvariantCulture))}");
        Console.WriteLine($"Target bytes: {FormatBytes(options.TargetBytes ?? 0)}");
        Console.WriteLine($"Storage accounts: {options.StoragePercent}%");
        Console.WriteLine($"Slots per storage account: {options.SlotsPerStorageAccount}");
        Console.WriteLine($"Seed: {options.Seed}");
        Console.WriteLine($"Trie node bytes: {options.TrieNodeBytes}");
        Console.WriteLine($"Read ops: {options.ReadOps} total target, {options.ReadOpsPerCategory} per category");
        Console.WriteLine($"Write ops: {options.WriteOps}");
        Console.WriteLine($"Write batch accounts: {options.BatchAccounts}");
        Console.WriteLine($"Write pattern: {options.WritePatternDescription}");
        Console.WriteLine($"Write mode: {options.WriteModeDescription}");
        Console.WriteLine($"Paprika commit mode: {options.PaprikaCommitModeDescription}");
        Console.WriteLine($"Paprika history depth: {options.PaprikaHistoryDepth}");
        if (options.VerifyAll)
        {
            Console.WriteLine($"Verify all: enabled{(options.VerifyAccounts.HasValue ? $", accounts={options.VerifyAccounts.Value.ToString(CultureInfo.InvariantCulture)}" : string.Empty)}, parallelism={options.VerifyParallelism.ToString(CultureInfo.InvariantCulture)}");
        }
        if (options.FileCrcOnly)
        {
            Console.WriteLine("File CRC only: enabled");
        }

        Console.WriteLine();

        string[] schemas = options.Schemas.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < schemas.Length; i++)
        {
            string schema = NormalizeSchema(schemas[i].ToLowerInvariant());
            if (schema is not TrieControlSchema and not FlatSchema and not PaprikaSchema)
            {
                Console.Error.WriteLine($"Unknown schema '{schemas[i]}'. Use trie-control, flat, paprika.");
                return 1;
            }

            RunSchema(schema, options);
        }

        return 0;
    }

    private static string NormalizeSchema(string schema) =>
        string.Equals(schema, FlatInTrieSchema, StringComparison.Ordinal)
            ? TrieControlSchema
            : schema;

    private static bool ContainsTrieControlSchema(string schemas)
        => ContainsSchema(schemas, TrieControlSchema);

    private static bool ContainsSchema(string schemas, string targetSchema)
    {
        string[] split = schemas.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < split.Length; i++)
        {
            string schema = NormalizeSchema(split[i].ToLowerInvariant());
            if (schema == targetSchema)
            {
                return true;
            }
        }

        return false;
    }

    private static void RunSchema(string schema, Options options)
    {
        string schemaPath = Path.Combine(options.BasePath, schema);
        if (options.FileCrcOnly)
        {
            VerifyFileCrc(schema, schemaPath);
            return;
        }

        if (!options.Reuse && Directory.Exists(schemaPath))
        {
            Directory.Delete(schemaPath, recursive: true);
        }

        Directory.CreateDirectory(schemaPath);
        ThrowIfRandomWrittenStoreReused(schemaPath, options);

        using StoreContext store = StoreContext.Open(schema, schemaPath, options);
        Manifest? manifest = ReadManifest(schemaPath);
        ValidateManifest(manifest, schema, options);
        long existingAccounts = manifest?.AccountCount ?? 0;
        long preparedAccounts = existingAccounts;

        if (!options.SkipPrepare)
        {
            preparedAccounts = Prepare(store, schemaPath, options, existingAccounts);
            WriteManifest(schemaPath, schema, preparedAccounts, options);
        }
        else if (manifest is null)
        {
            throw new InvalidOperationException($"Cannot use --skip-prepare because '{Path.Combine(schemaPath, "manifest.txt")}' does not exist.");
        }

        store.Flush();
        long logicalSize = store.GetLogicalSize();
        long footprintSize = store.GetFootprintSize(schemaPath);
        Console.WriteLine($"[{schema}] prepared_accounts={preparedAccounts} logical_size={FormatBytes(logicalSize)} footprint={FormatBytes(footprintSize)}");

        if (options.VerifyAll && options.PrepareOnly)
        {
            VerifyAllData(schema, store.Persistence, options.VerifyAccounts ?? preparedAccounts, options);
        }

        if (options.PrepareOnly)
        {
            return;
        }

        ReadResult read = BenchmarkReads(store.Persistence, preparedAccounts, options);
        if (options.RandomWrites)
        {
            MarkRandomWrittenStore(schemaPath, options);
        }

        WriteResult write = BenchmarkWrites(store.Persistence, preparedAccounts, options);
        store.Flush();
        long finalLogicalSize = store.GetLogicalSize();
        long finalFootprintSize = store.GetFootprintSize(schemaPath);

        Console.WriteLine(
            $"[{schema}] reads: {read.Elapsed.TotalSeconds:F3}s, {read.OperationsPerSecond:F0} ops/s, " +
            $"account={read.Account.OperationsPerSecond:F0}, storage={read.Storage.OperationsPerSecond:F0}, " +
            $"state_trie={read.StateTrie.OperationsPerSecond:F0}, storage_trie={read.StorageTrie.OperationsPerSecond:F0}, checksum={read.Checksum}");
        Console.WriteLine(
            $"[{schema}] read_checksums: account={read.Account.Checksum}, storage={read.Storage.Checksum}, " +
            $"state_trie={read.StateTrie.Checksum}, storage_trie={read.StorageTrie.Checksum}");
        Console.WriteLine(
            $"[{schema}] writes: {write.Elapsed.TotalSeconds:F3}s, {write.OperationsPerSecond:F0} ops/s, " +
            $"mutate={write.MutationElapsed.TotalSeconds:F3}s, commit={write.CommitElapsed.TotalSeconds:F3}s, " +
            $"rows={write.RowsWritten}, logical_size={FormatBytes(finalLogicalSize)}, footprint={FormatBytes(finalFootprintSize)}");

        if (options.VerifyAll)
        {
            VerifyAllData(schema, store.Persistence, options.VerifyAccounts ?? preparedAccounts + options.WriteOps, options);
        }

        Console.WriteLine();
    }

    private static void ThrowIfRandomWrittenStoreReused(string schemaPath, Options options)
    {
        string markerPath = Path.Combine(schemaPath, RandomWritesMarkerFileName);
        if (options.Reuse && File.Exists(markerPath))
        {
            throw new InvalidOperationException($"Cannot reuse '{schemaPath}' because a previous --random-writes run mutated it. Delete the schema directory and regenerate it.");
        }
    }

    private static void MarkRandomWrittenStore(string schemaPath, Options options)
    {
        string markerPath = Path.Combine(schemaPath, RandomWritesMarkerFileName);
        File.WriteAllText(
            markerPath,
            $"random_writes=true{Environment.NewLine}write_ops={options.WriteOps.ToString(CultureInfo.InvariantCulture)}{Environment.NewLine}batch_accounts={options.BatchAccounts.ToString(CultureInfo.InvariantCulture)}{Environment.NewLine}");
    }

    private static long Prepare(StoreContext store, string schemaPath, Options options, long existingAccounts)
    {
        IPersistence persistence = store.Persistence;
        long accountIndex = existingAccounts;
        long targetAccounts = options.TargetBytes.HasValue ? long.MaxValue : options.AccountCount;
        Stopwatch stopwatch = Stopwatch.StartNew();

        while (accountIndex < targetAccounts)
        {
            long remaining = targetAccounts - accountIndex;
            int batchAccountCount = (int)Math.Min(options.BatchAccounts, remaining);
            if (options.TargetBytes.HasValue)
            {
                store.Flush();
                long size = store.GetLogicalSize();
                if (size >= options.TargetBytes.Value)
                {
                    break;
                }

                batchAccountCount = options.BatchAccounts;
            }

            WriteAccounts(persistence, accountIndex, batchAccountCount, options);
            accountIndex += batchAccountCount;

            if (accountIndex % (options.BatchAccounts * 10L) == 0)
            {
                persistence.Flush();
                long size = store.GetLogicalSize();
                Console.WriteLine($"  prepared_accounts={accountIndex} size={FormatBytes(size)} elapsed={stopwatch.Elapsed}");
            }
        }

        stopwatch.Stop();
        Console.WriteLine($"  prepare elapsed={stopwatch.Elapsed}");
        return accountIndex;
    }

    private static ReadResult BenchmarkReads(IPersistence persistence, long preparedAccounts, Options options)
    {
        if (preparedAccounts <= 0)
        {
            return default;
        }

        using (IPersistence.IPersistenceReader reader = persistence.CreateReader())
        {
            ReadCategoryResult account = BenchmarkAccountReads(reader, preparedAccounts, options);
            ReadCategoryResult storage = BenchmarkStorageReads(reader, preparedAccounts, options);
            ReadCategoryResult stateTrie = BenchmarkStateTrieReads(reader, preparedAccounts, options);
            ReadCategoryResult storageTrie = BenchmarkStorageTrieReads(reader, preparedAccounts, options);
            return new ReadResult(account, storage, stateTrie, storageTrie);
        }
    }

    private static ReadCategoryResult BenchmarkAccountReads(IPersistence.IPersistenceReader reader, long preparedAccounts, Options options)
    {
        long checksum = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < options.ReadOpsPerCategory; i++)
        {
            long accountIndex = SelectIndex(i, preparedAccounts);
            Address address = DeriveAddress(accountIndex, options.Seed);
            Account? account = reader.GetAccount(address);
            if (account is not null)
            {
                checksum += (long)account.Nonce;
            }
        }

        stopwatch.Stop();
        return new ReadCategoryResult(stopwatch.Elapsed, options.ReadOpsPerCategory, checksum);
    }

    private static ReadCategoryResult BenchmarkStorageReads(IPersistence.IPersistenceReader reader, long preparedAccounts, Options options)
    {
        long checksum = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < options.ReadOpsPerCategory; i++)
        {
            long storageAccount = SelectStorageIndex(i, preparedAccounts, options.StoragePercent);
            Address storageAddress = DeriveAddress(storageAccount, options.Seed);
            UInt256 slot = DeriveSlot(i % options.SlotsPerStorageAccount, options.Seed);
            SlotValue value = default;
            if (reader.TryGetSlot(storageAddress, in slot, ref value))
            {
                checksum += value.AsSpan[^1];
            }
        }

        stopwatch.Stop();
        return new ReadCategoryResult(stopwatch.Elapsed, options.ReadOpsPerCategory, checksum);
    }

    private static ReadCategoryResult BenchmarkStateTrieReads(IPersistence.IPersistenceReader reader, long preparedAccounts, Options options)
    {
        long checksum = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < options.ReadOpsPerCategory; i++)
        {
            long accountIndex = SelectIndex(i, preparedAccounts);
            Address address = DeriveAddress(accountIndex, options.Seed);
            TreePath statePath = DeriveStatePath(address);
            byte[]? stateNode = reader.TryLoadStateRlp(in statePath, ReadFlags.None);
            checksum += stateNode?.Length ?? 0;
        }

        stopwatch.Stop();
        return new ReadCategoryResult(stopwatch.Elapsed, options.ReadOpsPerCategory, checksum);
    }

    private static ReadCategoryResult BenchmarkStorageTrieReads(IPersistence.IPersistenceReader reader, long preparedAccounts, Options options)
    {
        long checksum = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < options.ReadOpsPerCategory; i++)
        {
            long storageNodeAccount = SelectStorageIndex(i, preparedAccounts, options.StoragePercent);
            Address storageNodeAddress = DeriveAddress(storageNodeAccount, options.Seed);
            Hash256 storageAddressHash = new(storageNodeAddress.ToAccountPath);
            TreePath storagePath = DeriveStoragePath(i % options.SlotsPerStorageAccount, options.Seed);
            byte[]? storageNode = reader.TryLoadStorageRlp(storageAddressHash, in storagePath, ReadFlags.None);
            checksum += storageNode?.Length ?? 0;
        }

        stopwatch.Stop();
        return new ReadCategoryResult(stopwatch.Elapsed, options.ReadOpsPerCategory, checksum);
    }

    private static WriteResult BenchmarkWrites(IPersistence persistence, long preparedAccounts, Options options)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        int remaining = options.WriteOps;
        long accountIndex = preparedAccounts;
        int writeIndex = 0;
        long randomWriteStart = options.RandomWrites ? SelectRandomWriteStart(preparedAccounts, options.Seed) : 0;
        long randomWriteStep = options.RandomWrites ? SelectRandomWriteStep(preparedAccounts, options.Seed) : 0;
        long rows = 0;
        TimeSpan mutationElapsed = TimeSpan.Zero;
        TimeSpan commitElapsed = TimeSpan.Zero;

        while (remaining > 0)
        {
            int batchAccountCount = Math.Min(options.BatchAccounts, remaining);
            WriteBatchResult batch = options.RandomWrites
                ? WriteRandomAccounts(persistence, preparedAccounts, randomWriteStart, randomWriteStep, writeIndex, batchAccountCount, options)
                : WriteAccounts(persistence, accountIndex, batchAccountCount, options);
            rows += batch.RowsWritten;
            mutationElapsed += batch.MutationElapsed;
            commitElapsed += batch.CommitElapsed;
            accountIndex += batchAccountCount;
            writeIndex += batchAccountCount;
            remaining -= batchAccountCount;
        }

        stopwatch.Stop();
        return new WriteResult(stopwatch.Elapsed, mutationElapsed, commitElapsed, rows);
    }

    private static WriteBatchResult WriteRandomAccounts(
        IPersistence persistence,
        long preparedAccounts,
        long randomWriteStart,
        long randomWriteStep,
        int startWriteIndex,
        int accountCount,
        Options options) =>
        WriteAccounts(persistence, 0, accountCount, options, preparedAccounts, randomWriteStart, randomWriteStep, startWriteIndex);

    private static WriteBatchResult WriteAccounts(IPersistence persistence, long startAccountIndex, int accountCount, Options options)
        => WriteAccounts(persistence, startAccountIndex, accountCount, options, 0, 0, 0, 0);

    private static WriteBatchResult WriteAccounts(
        IPersistence persistence,
        long startAccountIndex,
        int accountCount,
        Options options,
        long randomPreparedAccounts,
        long randomWriteStart,
        long randomWriteStep,
        int startWriteIndex)
    {
        long rows = 0;
        IPersistence.IWriteBatch writer = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, options.WriteFlags);
        byte[] stateNodeBuffer = GC.AllocateUninitializedArray<byte>(options.TrieNodeBytes);
        byte[] storageNodeBuffer = GC.AllocateUninitializedArray<byte>(options.TrieNodeBytes);
        Stopwatch mutationStopwatch = Stopwatch.StartNew();
        try
        {
            for (int i = 0; i < accountCount; i++)
            {
                long accountIndex = randomPreparedAccounts == 0
                    ? startAccountIndex + i
                    : SelectRandomWriteIndex(startWriteIndex + i, randomPreparedAccounts, randomWriteStart, randomWriteStep);
                long valueIndex = randomPreparedAccounts == 0
                    ? accountIndex
                    : randomPreparedAccounts + startWriteIndex + i;
                Address address = DeriveAddress(accountIndex, options.Seed);
                ValueHash256 addressPath = address.ToAccountPath;
                bool hasStorage = HasStorage(accountIndex, options.StoragePercent);
                Account account = CreateAccount(valueIndex, address, hasStorage);

                writer.SetAccountRaw(addressPath, account);
                rows++;

                TreePath statePath = DeriveStatePath(addressPath);
                Span<byte> stateNode = stateNodeBuffer[..options.TrieNodeBytes];
                FillNode(stateNode[..options.TrieNodeBytes], valueIndex, 0, options.Seed);
                writer.SetStateTrieNode(in statePath, stateNode[..options.TrieNodeBytes]);
                rows++;

                if (!hasStorage)
                {
                    continue;
                }

                Hash256 storageAddressHash = new(addressPath);
                for (int slotIndex = 0; slotIndex < options.SlotsPerStorageAccount; slotIndex++)
                {
                    UInt256 slot = DeriveSlot(slotIndex, options.Seed);
                    ValueHash256 slotHash = DeriveSlotHash(slot);
                    SlotValue value = DeriveSlotValue(valueIndex, slotIndex, options.Seed);
                    writer.SetStorageRaw(addressPath, slotHash, value);
                    rows++;

                    TreePath storagePath = TreePath.FromPath(slotHash.Bytes);
                    Span<byte> storageNode = storageNodeBuffer[..options.TrieNodeBytes];
                    FillNode(storageNode[..options.TrieNodeBytes], valueIndex, slotIndex + 1, options.Seed);
                    writer.SetStorageTrieNode(storageAddressHash, in storagePath, storageNode[..options.TrieNodeBytes]);
                    rows++;
                }
            }
        }
        catch
        {
            writer.Dispose();
            throw;
        }
        finally
        {
            mutationStopwatch.Stop();
        }

        Stopwatch commitStopwatch = Stopwatch.StartNew();
        try
        {
            writer.Dispose();
        }
        finally
        {
            commitStopwatch.Stop();
        }

        return new WriteBatchResult(rows, mutationStopwatch.Elapsed, commitStopwatch.Elapsed);
    }

    private readonly record struct WriteBatchResult(long RowsWritten, TimeSpan MutationElapsed, TimeSpan CommitElapsed);

    private static VerificationResult VerifyAllData(string schema, IPersistence persistence, long accountCount, Options options)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        int chunkCount = GetVerifyChunkCount(accountCount, options.VerifyProgressInterval);
        VerificationRangeResult[] ranges = new VerificationRangeResult[chunkCount];
        long completedAccounts = 0;
        object progressLock = new();

        if (chunkCount != 0)
        {
            ParallelOptions parallelOptions = new()
            {
                MaxDegreeOfParallelism = Math.Min(options.VerifyParallelism, chunkCount)
            };

            Parallel.For(0, chunkCount, parallelOptions, chunkIndex =>
            {
                long start = chunkIndex * options.VerifyProgressInterval;
                long end = Math.Min(accountCount, start + options.VerifyProgressInterval);
                ranges[chunkIndex] = VerifyDataRange(schema, persistence, start, end, options);

                lock (progressLock)
                {
                    completedAccounts += end - start;
                    Console.WriteLine($"[{schema}] verify_all progress: accounts={completedAccounts:N0}/{accountCount:N0}, elapsed={stopwatch.Elapsed}");
                }
            });
        }

        stopwatch.Stop();
        uint accountCrc = 0;
        uint storageCrc = 0;
        uint stateTrieCrc = 0;
        uint storageTrieCrc = 0;
        long accounts = 0;
        long storageSlots = 0;
        long rows = 0;

        for (int i = 0; i < ranges.Length; i++)
        {
            VerificationRangeResult range = ranges[i];
            accounts += range.Accounts;
            storageSlots += range.StorageSlots;
            rows += range.Rows;
            accountCrc = AppendCrc(accountCrc, range.AccountCrc);
            storageCrc = AppendCrc(storageCrc, range.StorageCrc);
            stateTrieCrc = AppendCrc(stateTrieCrc, range.StateTrieCrc);
            storageTrieCrc = AppendCrc(storageTrieCrc, range.StorageTrieCrc);
        }

        VerificationResult result = new(accounts, storageSlots, rows, stopwatch.Elapsed, accountCrc, storageCrc, stateTrieCrc, storageTrieCrc);
        Console.WriteLine(
            $"[{schema}] verify_all: {result.Elapsed.TotalSeconds:F3}s, {result.RowsPerSecond:F0} rows/s, " +
            $"accounts={result.Accounts}, storage_slots={result.StorageSlots}, rows={result.Rows}, " +
            $"crc=0x{result.CombinedCrc:x8}, account=0x{result.AccountCrc:x8}, storage=0x{result.StorageCrc:x8}, " +
            $"state_trie=0x{result.StateTrieCrc:x8}, storage_trie=0x{result.StorageTrieCrc:x8}");
        return result;
    }

    private static int GetVerifyChunkCount(long accountCount, long chunkSize) =>
        accountCount <= 0 ? 0 : checked((int)((accountCount + chunkSize - 1) / chunkSize));

    private static VerificationRangeResult VerifyDataRange(string schema, IPersistence persistence, long startAccountIndex, long endAccountIndex, Options options)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        uint accountCrc = 0;
        uint storageCrc = 0;
        uint stateTrieCrc = 0;
        uint storageTrieCrc = 0;
        long accounts = 0;
        long storageSlots = 0;
        long rows = 0;
        byte[] expectedNode = GC.AllocateUninitializedArray<byte>(options.TrieNodeBytes);

        for (long accountIndex = startAccountIndex; accountIndex < endAccountIndex; accountIndex++)
        {
            Address address = DeriveAddress(accountIndex, options.Seed);
            ValueHash256 addressPath = address.ToAccountPath;
            bool hasStorage = HasStorage(accountIndex, options.StoragePercent);
            Account expectedAccount = CreateAccount(accountIndex, address, hasStorage);
            Account? account = reader.GetAccount(address);
            if (account != expectedAccount)
            {
                throw new InvalidOperationException($"[{schema}] Account mismatch at index {accountIndex.ToString(CultureInfo.InvariantCulture)}.");
            }

            accountCrc = AppendCrc(accountCrc, address.Bytes);
            accountCrc = AppendCrc(accountCrc, checked((ulong)account.Nonce));
            accountCrc = AppendCrc(accountCrc, checked((ulong)account.Balance));
            accountCrc = AppendCrc(accountCrc, account.StorageRoot.Bytes);
            accountCrc = AppendCrc(accountCrc, account.CodeHash.Bytes);
            rows++;
            accounts++;

            TreePath statePath = DeriveStatePath(addressPath);
            byte[]? stateNode = reader.TryLoadStateRlp(in statePath, ReadFlags.None);
            Span<byte> expectedStateNode = expectedNode.AsSpan(0, options.TrieNodeBytes);
            FillNode(expectedStateNode, accountIndex, 0, options.Seed);
            if (stateNode is null || !stateNode.AsSpan().SequenceEqual(expectedStateNode))
            {
                throw new InvalidOperationException($"[{schema}] State trie node mismatch at account index {accountIndex.ToString(CultureInfo.InvariantCulture)}: expected {DescribeBytes(expectedStateNode)}, actual {DescribeBytes(stateNode)}.");
            }

            stateTrieCrc = AppendCrc(stateTrieCrc, stateNode);
            rows++;

            if (hasStorage)
            {
                Hash256 storageAddressHash = new(addressPath);
                for (int slotIndex = 0; slotIndex < options.SlotsPerStorageAccount; slotIndex++)
                {
                    UInt256 slot = DeriveSlot(slotIndex, options.Seed);
                    SlotValue value = default;
                    if (!reader.TryGetSlot(address, in slot, ref value))
                    {
                        throw new InvalidOperationException($"[{schema}] Missing storage slot {slotIndex.ToString(CultureInfo.InvariantCulture)} at account index {accountIndex.ToString(CultureInfo.InvariantCulture)}.");
                    }

                    SlotValue expectedValue = DeriveSlotValue(accountIndex, slotIndex, options.Seed);
                    if (!value.AsReadOnlySpan.SequenceEqual(expectedValue.AsReadOnlySpan))
                    {
                        throw new InvalidOperationException($"[{schema}] Storage slot {slotIndex.ToString(CultureInfo.InvariantCulture)} mismatch at account index {accountIndex.ToString(CultureInfo.InvariantCulture)}.");
                    }

                    storageCrc = AppendCrc(storageCrc, value.AsReadOnlySpan);
                    rows++;
                    storageSlots++;

                    TreePath storagePath = DeriveStoragePath(slotIndex, options.Seed);
                    byte[]? storageNode = reader.TryLoadStorageRlp(storageAddressHash, in storagePath, ReadFlags.None);
                    Span<byte> expectedStorageNode = expectedNode.AsSpan(0, options.TrieNodeBytes);
                    FillNode(expectedStorageNode, accountIndex, slotIndex + 1, options.Seed);
                    if (storageNode is null || !storageNode.AsSpan().SequenceEqual(expectedStorageNode))
                    {
                        throw new InvalidOperationException($"[{schema}] Storage trie node {slotIndex.ToString(CultureInfo.InvariantCulture)} mismatch at account index {accountIndex.ToString(CultureInfo.InvariantCulture)}: expected {DescribeBytes(expectedStorageNode)}, actual {DescribeBytes(storageNode)}.");
                    }

                    storageTrieCrc = AppendCrc(storageTrieCrc, storageNode);
                    rows++;
                }
            }
        }

        return new VerificationRangeResult(accounts, storageSlots, rows, accountCrc, storageCrc, stateTrieCrc, storageTrieCrc);
    }

    private static FileCrcResult VerifyFileCrc(string schema, string schemaPath)
    {
        if (!Directory.Exists(schemaPath))
        {
            throw new InvalidOperationException($"[{schema}] Cannot run file CRC because '{schemaPath}' does not exist.");
        }

        string[] files = Directory.GetFiles(schemaPath, "*", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        byte[] buffer = GC.AllocateUninitializedArray<byte>(8 * 1024 * 1024);
        Stopwatch stopwatch = Stopwatch.StartNew();
        uint crc = 0;
        long bytes = 0;
        int filesRead = 0;
        long nextProgressBytes = 16L * 1024 * 1024 * 1024;

        for (int i = 0; i < files.Length; i++)
        {
            try
            {
                using FileStream stream = new(files[i], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, buffer.Length, FileOptions.SequentialScan);
                while (true)
                {
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read == 0)
                    {
                        break;
                    }

                    crc = AppendCrc(crc, buffer.AsSpan(0, read));
                    bytes += read;
                    if (bytes >= nextProgressBytes)
                    {
                        Console.WriteLine($"[{schema}] file_crc progress: files={filesRead:N0}/{files.Length:N0}, bytes={FormatBytes(bytes)}, elapsed={stopwatch.Elapsed}");
                        nextProgressBytes += 16L * 1024 * 1024 * 1024;
                    }
                }

                filesRead++;
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
        }

        stopwatch.Stop();
        FileCrcResult result = new(filesRead, bytes, stopwatch.Elapsed, crc);
        Console.WriteLine(
            $"[{schema}] file_crc: {result.Elapsed.TotalSeconds:F3}s, {FormatBytes((long)result.BytesPerSecond)}/s, " +
            $"files={result.Files}, bytes={FormatBytes(result.Bytes)}, crc=0x{result.Crc:x8}");
        return result;
    }

    private static string DescribeBytes(byte[]? bytes) =>
        bytes is null ? "null" : DescribeBytes(bytes.AsSpan());

    private static string DescribeBytes(ReadOnlySpan<byte> bytes)
    {
        int prefixLength = Math.Min(bytes.Length, 16);
        return $"len={bytes.Length.ToString(CultureInfo.InvariantCulture)} first={Convert.ToHexString(bytes[..prefixLength])}";
    }

    private static Account CreateAccount(long accountIndex, Address address, bool hasStorage) =>
        hasStorage
            ? new Account((UInt256)(ulong)accountIndex, (UInt256)(ulong)(accountIndex + 1), Keccak.Compute(address.Bytes), Keccak.OfAnEmptyString)
            : new Account((UInt256)(ulong)accountIndex, (UInt256)(ulong)(accountIndex + 1));

    private static uint AppendCrc(uint crc, uint value) => BitOperations.Crc32C(crc, value);

    private static uint AppendCrc(uint crc, ulong value) => BitOperations.Crc32C(crc, value);

    private static uint AppendCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        while (bytes.Length >= sizeof(ulong))
        {
            crc = BitOperations.Crc32C(crc, BinaryPrimitives.ReadUInt64LittleEndian(bytes));
            bytes = bytes[sizeof(ulong)..];
        }

        if (bytes.Length >= sizeof(uint))
        {
            crc = BitOperations.Crc32C(crc, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
            bytes = bytes[sizeof(uint)..];
        }

        if (bytes.Length >= sizeof(ushort))
        {
            crc = BitOperations.Crc32C(crc, BinaryPrimitives.ReadUInt16LittleEndian(bytes));
            bytes = bytes[sizeof(ushort)..];
        }

        if (!bytes.IsEmpty)
        {
            crc = BitOperations.Crc32C(crc, bytes[0]);
        }

        return crc;
    }

    private static Address DeriveAddress(long index, int seed)
    {
        Span<byte> bytes = stackalloc byte[12];
        BinaryPrimitives.WriteInt32BigEndian(bytes, seed);
        BinaryPrimitives.WriteInt64BigEndian(bytes[4..], index + 1);
        return new Address(Keccak.Compute(bytes).Bytes[..Address.Size]);
    }

    private static UInt256 DeriveSlot(int slotIndex, int seed) =>
        seed == 0
            ? (UInt256)(ulong)(slotIndex + 1)
            : (UInt256)(ulong)(slotIndex + 1) + ((UInt256)(uint)seed << 32);

    private static SlotValue DeriveSlotValue(long accountIndex, int slotIndex, int seed)
    {
        Span<byte> bytes = stackalloc byte[32];
        BinaryPrimitives.WriteInt32BigEndian(bytes, seed);
        BinaryPrimitives.WriteInt64BigEndian(bytes[4..], accountIndex + 1);
        BinaryPrimitives.WriteInt32BigEndian(bytes[12..], slotIndex + 1);
        bytes[31] = (byte)((accountIndex + slotIndex + seed) & 0xff);
        return SlotValue.FromSpanWithoutLeadingZero(bytes);
    }

    private static TreePath DeriveStatePath(Address address) => DeriveStatePath(address.ToAccountPath);

    private static TreePath DeriveStatePath(in ValueHash256 addressPath) => TreePath.FromPath(addressPath.Bytes);

    private static ValueHash256 DeriveSlotHash(in UInt256 slot)
    {
        ValueHash256 slotHash = ValueKeccak.Zero;
        StorageTree.ComputeKeyWithLookup(slot, ref slotHash);
        return slotHash;
    }

    private static TreePath DeriveStoragePath(int slotIndex, int seed) => TreePath.FromPath(DeriveSlotHash(DeriveSlot(slotIndex, seed)).Bytes);

    private static bool HasStorage(long accountIndex, int storagePercent) => accountIndex % 100 < storagePercent;

    private static long SelectIndex(int operationIndex, long count)
    {
        ulong value = unchecked((ulong)operationIndex * 11400714819323198485UL + 0x9e3779b97f4a7c15UL);
        return (long)(value % (ulong)count);
    }

    private static long SelectStorageIndex(int operationIndex, long preparedAccounts, int storagePercent)
    {
        long index = SelectIndex(operationIndex, preparedAccounts);
        for (int i = 0; i < 100; i++)
        {
            if (HasStorage(index, storagePercent))
            {
                return index;
            }

            index++;
            if (index == preparedAccounts)
            {
                index = 0;
            }
        }

        return index;
    }

    private static long SelectRandomWriteIndex(int operationIndex, long preparedAccounts, long start, long step)
    {
        ulong value = unchecked((ulong)start + (ulong)operationIndex * (ulong)step);
        return (long)(value % (ulong)preparedAccounts);
    }

    private static long SelectRandomWriteStart(long preparedAccounts, int seed) =>
        (long)(NextRandom(MixSeed(unchecked((ulong)(uint)seed), 0x5eedu, 0x7719u)) % (ulong)preparedAccounts);

    private static long SelectRandomWriteStep(long preparedAccounts, int seed)
    {
        if (preparedAccounts == 1)
        {
            return 0;
        }

        long step = (long)(NextRandom(MixSeed(unchecked((ulong)(uint)seed), 0xa529u, 0x9e37u)) % (ulong)(preparedAccounts - 1)) + 1;
        while (GreatestCommonDivisor(step, preparedAccounts) != 1)
        {
            step++;
            if (step == preparedAccounts)
            {
                step = 1;
            }
        }

        return step;
    }

    private static long GreatestCommonDivisor(long left, long right)
    {
        while (right != 0)
        {
            long remainder = left % right;
            left = right;
            right = remainder;
        }

        return Math.Abs(left);
    }

    private static void FillNode(Span<byte> buffer, long accountIndex, int salt, int seed)
    {
        if (buffer.IsEmpty)
        {
            return;
        }

        buffer[0] = 0xc0;
        ulong state = MixSeed(unchecked((ulong)accountIndex), unchecked((uint)salt), unchecked((uint)seed));
        int offset = 1;
        while (offset + sizeof(ulong) <= buffer.Length)
        {
            state = NextRandom(state);
            BinaryPrimitives.WriteUInt64LittleEndian(buffer[offset..], state);
            offset += sizeof(ulong);
        }

        if (offset < buffer.Length)
        {
            state = NextRandom(state);
            Span<byte> tail = stackalloc byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64LittleEndian(tail, state);
            tail[..(buffer.Length - offset)].CopyTo(buffer[offset..]);
        }
    }

    private static ulong MixSeed(ulong accountIndex, uint salt, uint seed)
    {
        ulong state = accountIndex + 0x9e3779b97f4a7c15UL;
        state ^= (ulong)salt << 32;
        state ^= seed;
        return NextRandom(state);
    }

    private static ulong NextRandom(ulong state)
    {
        state += 0x9e3779b97f4a7c15UL;
        ulong value = state;
        value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL;
        value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL;
        return value ^ (value >> 31);
    }

    private static Manifest? ReadManifest(string path)
    {
        string manifestPath = Path.Combine(path, "manifest.txt");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        string[] lines = File.ReadAllLines(manifestPath);
        if (lines.Length == 1 && long.TryParse(lines[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long legacyAccounts))
        {
            return new Manifest(0, string.Empty, legacyAccounts, 0, 0, 0, 0);
        }

        Dictionary<string, string> values = new(StringComparer.Ordinal);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            int separator = line.IndexOf('=');
            if (separator <= 0)
            {
                throw new InvalidOperationException($"Invalid benchmark manifest line '{line}'.");
            }

            values[line[..separator]] = line[(separator + 1)..];
        }

        return new Manifest(
            ParseManifestInt(values, "version"),
            GetManifestValue(values, "schema"),
            ParseManifestLong(values, "accounts"),
            ParseManifestInt(values, "storage_percent"),
            ParseManifestInt(values, "slots_per_storage_account"),
            ParseManifestInt(values, "trie_node_bytes"),
            ParseManifestIntOrDefault(values, "seed", 0));
    }

    private static void ValidateManifest(Manifest? manifest, string schema, Options options)
    {
        if (manifest is null)
        {
            return;
        }

        Manifest value = manifest.Value;
        if (value.Version != ManifestVersion ||
            !string.Equals(value.Schema, schema, StringComparison.Ordinal) ||
            value.StoragePercent != options.StoragePercent ||
            value.SlotsPerStorageAccount != options.SlotsPerStorageAccount ||
            value.TrieNodeBytes != options.TrieNodeBytes ||
            value.Seed != options.Seed)
        {
            throw new InvalidOperationException(
                $"Existing benchmark dataset in '{schema}' was generated with incompatible parameters. " +
                "Remove the schema directory or use matching --storage-percent, --slots, --trie-node-bytes and --seed values.");
        }
    }

    private static void WriteManifest(string path, string schema, long accountCount, Options options)
    {
        long storageAccounts = CountStorageAccounts(accountCount, options.StoragePercent);
        long storageSlots = checked(storageAccounts * options.SlotsPerStorageAccount);
        long rows = CountRows(accountCount, options);
        string[] lines =
        [
            $"version={ManifestVersion}",
            $"schema={schema}",
            $"accounts={accountCount.ToString(CultureInfo.InvariantCulture)}",
            $"storage_percent={options.StoragePercent.ToString(CultureInfo.InvariantCulture)}",
            $"storage_accounts={storageAccounts.ToString(CultureInfo.InvariantCulture)}",
            $"slots_per_storage_account={options.SlotsPerStorageAccount.ToString(CultureInfo.InvariantCulture)}",
            $"storage_slots={storageSlots.ToString(CultureInfo.InvariantCulture)}",
            $"rows={rows.ToString(CultureInfo.InvariantCulture)}",
            $"trie_node_bytes={options.TrieNodeBytes.ToString(CultureInfo.InvariantCulture)}",
            $"seed={options.Seed.ToString(CultureInfo.InvariantCulture)}"
        ];
        File.WriteAllLines(Path.Combine(path, "manifest.txt"), lines);
    }

    private static long CountStorageAccounts(long accountCount, int storagePercent)
    {
        long fullCycles = accountCount / 100;
        long remainder = accountCount % 100;
        return checked(fullCycles * storagePercent + Math.Min(remainder, storagePercent));
    }

    private static long CountRows(long accountCount, Options options)
    {
        long storageAccounts = CountStorageAccounts(accountCount, options.StoragePercent);
        return checked(accountCount * 2 + storageAccounts * options.SlotsPerStorageAccount * 2L);
    }

    private static string GetManifestValue(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string? value)
            ? value
            : throw new InvalidOperationException($"Benchmark manifest is missing '{key}'.");

    private static int ParseManifestInt(Dictionary<string, string> values, string key)
    {
        string value = GetManifestValue(values, key);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : throw new InvalidOperationException($"Benchmark manifest has invalid integer value for '{key}'.");
    }

    private static int ParseManifestIntOrDefault(Dictionary<string, string> values, string key, int defaultValue)
    {
        if (!values.TryGetValue(key, out string? value))
        {
            return defaultValue;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : throw new InvalidOperationException($"Benchmark manifest has invalid integer value for '{key}'.");
    }

    private static long ParseManifestLong(Dictionary<string, string> values, string key)
    {
        string value = GetManifestValue(values, key);
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            ? parsed
            : throw new InvalidOperationException($"Benchmark manifest has invalid integer value for '{key}'.");
    }

    private static long GetDirectorySize(string path, string? excludedDirectory = null)
    {
        if (!Directory.Exists(path))
        {
            return 0;
        }

        string? excludedFullPath = excludedDirectory is null ? null : Path.GetFullPath(excludedDirectory);
        long total = 0;
        string[] files;
        try
        {
            files = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
        }
        catch (DirectoryNotFoundException)
        {
            return 0;
        }

        for (int i = 0; i < files.Length; i++)
        {
            if (excludedFullPath is not null && Path.GetFullPath(files[i]).StartsWith(excludedFullPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryGetFileLength(files[i], out long fileLength))
            {
                total += fileLength;
            }
        }

        return total;
    }

    private static bool TryGetFileLength(string path, out long length)
    {
        try
        {
            length = new FileInfo(path).Length;
            return true;
        }
        catch (FileNotFoundException)
        {
            length = 0;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            length = 0;
            return false;
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:F2} {units[unit]}";
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project src/Nethermind/Nethermind.Benchmark.Runner -c release -- --state-db-schema [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --schemas trie-control,flat,paprika");
        Console.WriteLine("  --base-path <path>");
        Console.WriteLine("  --accounts <count>");
        Console.WriteLine("  --target-gb <gb>              Prepare each schema until its directory reaches this size.");
        Console.WriteLine("  --storage-percent <0-100>     Default: 50");
        Console.WriteLine("  --slots <count>               Slots per storage-bearing account. Default: 4");
        Console.WriteLine("  --seed <int>                  Deterministic data seed. Default: 0");
        Console.WriteLine("  --trie-node-bytes <count>     Synthetic trie-node payload bytes. Default: 64");
        Console.WriteLine("  --batch-accounts <count>      Default: 100000");
        Console.WriteLine("  --read-ops <count>            Default: 250000");
        Console.WriteLine("  --write-ops <count>           Default: 100000");
        Console.WriteLine("  --random-writes               Update pseudo-random prepared accounts instead of appending new ones.");
        Console.WriteLine("  --verify-all                  Load every deterministic account/storage/trie row and CRC it.");
        Console.WriteLine("  --verify-accounts <count>     Account count to verify. Default: prepared accounts, or prepared + writes after write benchmark.");
        Console.WriteLine("  --verify-parallelism <count>  Parallel readers for --verify-all. Default: 1");
        Console.WriteLine("  --file-crc-only               Read schema files directly and CRC raw bytes without opening the DB.");
        Console.WriteLine("  --paprika-capacity-gb <gb>    Override Paprika mmap capacity.");
        Console.WriteLine("  --paprika-history-depth <n>   Override Paprika history depth. Default: 128, minimum: 3");
        Console.WriteLine("  --paprika-flush-data-only     Flush Paprika data pages but defer root-page durability.");
        Console.WriteLine("  --unsafe-disable-wal          Use unsafe no-WAL/no-flush writes for prepare and write timing.");
        Console.WriteLine("  --reuse                       Keep existing schema directories and append missing data.");
        Console.WriteLine("  --skip-prepare                Run only benchmark loops against existing stores.");
        Console.WriteLine("  --prepare-only                Prepare data and exit.");
    }

    private sealed class StoreContext : IDisposable
    {
        private readonly ColumnsDb<FlatDbColumns> _columnsDb;
        private readonly HyperClockCacheWrapper _sharedCache;
        private readonly PagedDb? _paprikaDb;

        private StoreContext(ColumnsDb<FlatDbColumns> columnsDb, HyperClockCacheWrapper sharedCache, PagedDb? paprikaDb, IPersistence persistence)
        {
            _columnsDb = columnsDb;
            _sharedCache = sharedCache;
            _paprikaDb = paprikaDb;
            Persistence = persistence;
        }

        public IPersistence Persistence { get; }

        public static StoreContext Open(string schema, string schemaPath, Options options)
        {
            HyperClockCacheWrapper sharedCache = new((ulong)256.MiB);
            DbConfig dbConfig = new();
            PruningConfig pruningConfig = new() { Mode = PruningMode.Memory };
            RocksDbConfigFactory configFactory = new(dbConfig, pruningConfig, new TestHardwareInfo(64.GiB), LimboLogs.Instance);
            ColumnsDb<FlatDbColumns> columnsDb = new(
                schemaPath,
                new DbSettings($"StateDbSchema-{schema}", "flat"),
                dbConfig,
                configFactory,
                LimboLogs.Instance,
                Array.Empty<FlatDbColumns>(),
                sharedCache.Handle);

            if (schema == PaprikaSchema)
            {
                long capacity = Math.Max(options.PaprikaCapacityBytes, 1.GiB);
                PagedDb paprikaDb = PagedDb.MemoryMappedDb(capacity, options.PaprikaHistoryDepth, Path.Combine(schemaPath, "paprika"));
                return new StoreContext(columnsDb, sharedCache, paprikaDb, new PaprikaFlatPersistence(columnsDb, paprikaDb, options.PaprikaCommitMode));
            }

            IPersistence persistence = schema == FlatSchema
                ? new RocksDbPersistence(columnsDb)
                : new FlatInTriePersistence(columnsDb);
            return new StoreContext(columnsDb, sharedCache, null, persistence);
        }

        public void Flush()
        {
            Persistence.Flush();
            _columnsDb.Flush();
            _paprikaDb?.Flush();
        }

        public long GetLogicalSize()
        {
            if (_paprikaDb is null)
            {
                return GetRocksLogicalSize();
            }

            return GetRocksLogicalSize() + GetPaprikaLiveSize();
        }

        public long GetFootprintSize(string schemaPath)
        {
            if (_paprikaDb is null)
            {
                return GetDirectorySize(schemaPath);
            }

            return GetDirectorySize(schemaPath, Path.Combine(schemaPath, "paprika")) + GetPaprikaLiveSize();
        }

        private long GetPaprikaLiveSize() => (long)_paprikaDb!.NextFreePage * Page.PageSize;

        private long GetRocksLogicalSize()
        {
            IDbMeta.DbMetric metric = _columnsDb.GatherMetric();
            long rocksLiveSize = metric.Size + metric.MemtableSize;
            return rocksLiveSize;
        }

        public void Dispose()
        {
            _paprikaDb?.Dispose();
            _columnsDb.Dispose();
            _sharedCache.Dispose();
        }
    }

    private readonly record struct Manifest(
        int Version,
        string Schema,
        long AccountCount,
        int StoragePercent,
        int SlotsPerStorageAccount,
        int TrieNodeBytes,
        int Seed);

    private readonly record struct ReadCategoryResult(TimeSpan Elapsed, long Operations, long Checksum)
    {
        public double OperationsPerSecond => Elapsed.TotalSeconds == 0 ? 0 : Operations / Elapsed.TotalSeconds;
    }

    private readonly record struct ReadResult(
        ReadCategoryResult Account,
        ReadCategoryResult Storage,
        ReadCategoryResult StateTrie,
        ReadCategoryResult StorageTrie)
    {
        public TimeSpan Elapsed => Account.Elapsed + Storage.Elapsed + StateTrie.Elapsed + StorageTrie.Elapsed;
        public long Operations => Account.Operations + Storage.Operations + StateTrie.Operations + StorageTrie.Operations;
        public long Checksum => Account.Checksum + Storage.Checksum + StateTrie.Checksum + StorageTrie.Checksum;
        public double OperationsPerSecond => Elapsed.TotalSeconds == 0 ? 0 : Operations / Elapsed.TotalSeconds;
    }

    private readonly record struct WriteResult(
        TimeSpan Elapsed,
        TimeSpan MutationElapsed,
        TimeSpan CommitElapsed,
        long RowsWritten)
    {
        public double OperationsPerSecond => Elapsed.TotalSeconds == 0 ? 0 : RowsWritten / Elapsed.TotalSeconds;
    }

    private readonly record struct VerificationResult(
        long Accounts,
        long StorageSlots,
        long Rows,
        TimeSpan Elapsed,
        uint AccountCrc,
        uint StorageCrc,
        uint StateTrieCrc,
        uint StorageTrieCrc)
    {
        public double RowsPerSecond => Elapsed.TotalSeconds == 0 ? 0 : Rows / Elapsed.TotalSeconds;
        public uint CombinedCrc => AccountCrc ^ BitOperations.RotateLeft(StorageCrc, 7) ^ BitOperations.RotateLeft(StateTrieCrc, 13) ^ BitOperations.RotateLeft(StorageTrieCrc, 19);
    }

    private readonly record struct VerificationRangeResult(
        long Accounts,
        long StorageSlots,
        long Rows,
        uint AccountCrc,
        uint StorageCrc,
        uint StateTrieCrc,
        uint StorageTrieCrc);

    private readonly record struct FileCrcResult(int Files, long Bytes, TimeSpan Elapsed, uint Crc)
    {
        public double BytesPerSecond => Elapsed.TotalSeconds == 0 ? 0 : Bytes / Elapsed.TotalSeconds;
    }

    public sealed class Options
    {
        public string BasePath { get; private set; } = Path.Combine("artifacts", "state-db-schema-benchmark");
        public string Schemas { get; private set; } = $"{TrieControlSchema},{FlatSchema},{PaprikaSchema}";
        public int AccountCount { get; private set; } = DefaultAccountCount;
        public long? TargetBytes { get; private set; }
        public int StoragePercent { get; private set; } = DefaultStoragePercent;
        public int SlotsPerStorageAccount { get; private set; } = DefaultSlotsPerStorageAccount;
        public int BatchAccounts { get; private set; } = DefaultBatchAccounts;
        public int ReadOps { get; private set; } = DefaultReadOps;
        public int ReadOpsPerCategory => Math.Max(1, ReadOps / 4);
        public int WriteOps { get; private set; } = DefaultWriteOps;
        public int TrieNodeBytes { get; private set; } = DefaultTrieNodeBytes;
        public int Seed { get; private set; }
        public bool VerifyAll { get; private set; }
        public long? VerifyAccounts { get; private set; }
        public long VerifyProgressInterval { get; private set; } = DefaultVerifyProgressInterval;
        public int VerifyParallelism { get; private set; } = 1;
        public bool FileCrcOnly { get; private set; }
        public long? PaprikaCapacityOverrideBytes { get; private set; }
        public byte PaprikaHistoryDepth { get; private set; } = DefaultPaprikaHistoryDepth;
        public bool Reuse { get; private set; }
        public bool SkipPrepare { get; private set; }
        public bool PrepareOnly { get; private set; }
        public bool PaprikaFlushDataOnly { get; private set; }
        public bool UnsafeDisableWal { get; private set; }
        public bool RandomWrites { get; private set; }
        public bool ShowHelp { get; private set; }
        public string WritePatternDescription => RandomWrites
            ? "pseudo-random prepared-account overwrites"
            : "append new accounts";
        public WriteFlags WriteFlags => UnsafeDisableWal ? WriteFlags.DisableWAL : WriteFlags.None;
        public string WriteModeDescription => UnsafeDisableWal
            ? "unsafe-disable-wal"
            : PaprikaFlushDataOnly
                ? "crash-consistent flat; Paprika root flush deferred"
                : "crash-consistent";
        public PaprikaFlatCommitMode PaprikaCommitMode => PaprikaFlushDataOnly
            ? PaprikaFlatCommitMode.FlushDataOnly
            : PaprikaFlatCommitMode.FlushDataAndRoot;
        public string PaprikaCommitModeDescription => PaprikaFlushDataOnly
            ? $"{PaprikaFlatCommitMode.FlushDataOnly} (data pages flushed; root page not durable until a later flush/commit)"
            : PaprikaFlatCommitMode.FlushDataAndRoot.ToString();

        public long PaprikaCapacityBytes
        {
            get
            {
                long target = TargetBytes ?? 1.GiB;
                return PaprikaCapacityOverrideBytes ?? Math.Max(target * 2, 1.GiB);
            }
        }

        public void Normalize() => BasePath = Path.GetFullPath(BasePath);

        public static Options Parse(string[] args)
        {
            Options options = new();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "-h":
                    case "--help":
                        options.ShowHelp = true;
                        break;
                    case "--base-path":
                        options.BasePath = RequireValue(args, ref i, arg);
                        break;
                    case "--schemas":
                        options.Schemas = RequireValue(args, ref i, arg);
                        break;
                    case "--accounts":
                        options.AccountCount = ParsePositiveInt(RequireValue(args, ref i, arg), arg);
                        break;
                    case "--target-gb":
                        options.TargetBytes = (long)(double.Parse(RequireValue(args, ref i, arg), CultureInfo.InvariantCulture) * 1024 * 1024 * 1024);
                        break;
                    case "--storage-percent":
                        options.StoragePercent = ParsePercentage(RequireValue(args, ref i, arg), arg);
                        break;
                    case "--slots":
                        options.SlotsPerStorageAccount = ParsePositiveInt(RequireValue(args, ref i, arg), arg);
                        break;
                    case "--seed":
                        options.Seed = ParseInt(RequireValue(args, ref i, arg), arg);
                        break;
                    case "--batch-accounts":
                        options.BatchAccounts = ParsePositiveInt(RequireValue(args, ref i, arg), arg);
                        break;
                    case "--read-ops":
                        options.ReadOps = ParsePositiveInt(RequireValue(args, ref i, arg), arg);
                        break;
                    case "--write-ops":
                        options.WriteOps = ParsePositiveInt(RequireValue(args, ref i, arg), arg);
                        break;
                    case "--verify-all":
                        options.VerifyAll = true;
                        break;
                    case "--verify-accounts":
                        options.VerifyAccounts = ParseNonNegativeLong(RequireValue(args, ref i, arg), arg);
                        break;
                    case "--verify-progress-interval":
                        options.VerifyProgressInterval = ParsePositiveLong(RequireValue(args, ref i, arg), arg);
                        break;
                    case "--verify-parallelism":
                        options.VerifyParallelism = ParsePositiveInt(RequireValue(args, ref i, arg), arg);
                        break;
                    case "--file-crc-only":
                        options.FileCrcOnly = true;
                        options.Reuse = true;
                        break;
                    case "--paprika-capacity-gb":
                        options.PaprikaCapacityOverrideBytes = ParseGibibytes(RequireValue(args, ref i, arg), arg);
                        break;
                    case "--paprika-history-depth":
                        options.PaprikaHistoryDepth = ParsePaprikaHistoryDepth(RequireValue(args, ref i, arg), arg);
                        break;
                    case "--trie-node-bytes":
                        options.TrieNodeBytes = ParsePositiveInt(RequireValue(args, ref i, arg), arg);
                        break;
                    case "--paprika-flush-data-only":
                        options.PaprikaFlushDataOnly = true;
                        break;
                    case "--unsafe-disable-wal":
                        options.UnsafeDisableWal = true;
                        break;
                    case "--random-writes":
                        options.RandomWrites = true;
                        break;
                    case "--reuse":
                        options.Reuse = true;
                        break;
                    case "--skip-prepare":
                        options.SkipPrepare = true;
                        options.Reuse = true;
                        break;
                    case "--prepare-only":
                        options.PrepareOnly = true;
                        break;
                    default:
                        throw new ArgumentException($"Unknown argument '{arg}'. Use --help.");
                }
            }

            if (options.VerifyAll && options.RandomWrites && !options.PrepareOnly)
            {
                throw new ArgumentException("--verify-all cannot be combined with --random-writes unless --prepare-only is also set.");
            }

            if (options.RandomWrites && options.PaprikaCapacityOverrideBytes is null && ContainsSchema(options.Schemas, PaprikaSchema))
            {
                throw new ArgumentException("--random-writes with the paprika schema requires --paprika-capacity-gb because random overwrites retain history pages.");
            }

            return options;
        }

        private static string RequireValue(string[] args, ref int index, string option)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"{option} requires a value.");
            }

            index++;
            return args[index];
        }

        private static int ParsePositiveInt(string value, string option)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) || result <= 0)
            {
                throw new ArgumentException($"{option} requires a positive integer.");
            }

            return result;
        }

        private static byte ParsePositiveByte(string value, string option)
        {
            if (!byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte result) || result == 0)
            {
                throw new ArgumentException($"{option} requires a positive integer no greater than {byte.MaxValue.ToString(CultureInfo.InvariantCulture)}.");
            }

            return result;
        }

        private static byte ParsePaprikaHistoryDepth(string value, string option)
        {
            byte result = ParsePositiveByte(value, option);
            if (result < 3)
            {
                throw new ArgumentException($"{option} requires an integer from 3 to {byte.MaxValue.ToString(CultureInfo.InvariantCulture)}.");
            }

            return result;
        }

        private static int ParseInt(string value, string option)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
            {
                throw new ArgumentException($"{option} requires an integer.");
            }

            return result;
        }

        private static long ParsePositiveLong(string value, string option)
        {
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result) || result <= 0)
            {
                throw new ArgumentException($"{option} requires a positive integer.");
            }

            return result;
        }

        private static long ParseNonNegativeLong(string value, string option)
        {
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result) || result < 0)
            {
                throw new ArgumentException($"{option} requires a non-negative integer.");
            }

            return result;
        }

        private static long ParseGibibytes(string value, string option)
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) || result <= 0)
            {
                throw new ArgumentException($"{option} requires a positive number.");
            }

            return checked((long)(result * 1024 * 1024 * 1024));
        }

        private static int ParsePercentage(string value, string option)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) || result < 0 || result > 100)
            {
                throw new ArgumentException($"{option} requires an integer between 0 and 100.");
            }

            return result;
        }
    }
}
