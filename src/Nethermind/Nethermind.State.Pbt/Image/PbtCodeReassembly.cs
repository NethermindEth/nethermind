// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Flat.Collections;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt.Image;

/// <summary>Reassembles the bytecode of the staged contract accounts from the code chunks of an ascending leaf stream into the code column.</summary>
/// <remarks>
/// Code chunks are keyed by a hash of their code hash, so the code section scatters each code's chunks. Three sorted spools
/// regroup them without reading staged state back: the code hashes and sizes of the account section; the chunk stems those
/// codes derive, which ascend as the code section does, so each stem streaming past names its code and first chunk; and the
/// chunks keyed by code hash and chunk id, which yield each code's chunks in sequence. The reassembly runs in the background
/// once the code section ends. Requiring every chunk to fall within a spooled code is what stops a stream carrying code no
/// account names.
/// </remarks>
public sealed class PbtCodeReassembly : IDisposable
{
    private const string CodePhase = "PBT import code";
    private const int StemWidth = 256;
    private const int ChunkKeyLength = ValueHash256.MemorySize + sizeof(uint);

    private readonly PbtRocksDbPersistence _target;
    private readonly int _concurrency;
    private readonly ILogManager _logManager;
    private readonly CancellationTokenSource _cancellation;
    private readonly SortedSpool _codes;
    private readonly SortedSpool _stems;
    private readonly SortedSpool _chunks;
    private readonly SortedSpool.Writer _codeWriter;
    private SortedSpool.Writer? _chunkWriter;
    private SortedSpool.Cursor? _stemCursor;
    private ValueHash256 _stemCode;
    private uint _stemFirstChunk;
    private Task? _rebuild;

    /// <param name="concurrency">Maximum spool pre-merges and reconstruction workers.</param>
    public PbtCodeReassembly(PbtRocksDbPersistence target, string directory, int sortBufferBytes, int concurrency, ILogManager logManager,
        CancellationToken cancellationToken)
    {
        _target = target;
        _concurrency = concurrency;
        _logManager = logManager;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _codes = new(directory, sortBufferBytes, 1, logManager, _cancellation.Token) { MaxConcurrentPreMerges = concurrency };
        _stems = new(directory, sortBufferBytes, 1, logManager, _cancellation.Token) { MaxConcurrentPreMerges = concurrency };
        _chunks = new(directory, sortBufferBytes, 1, logManager, _cancellation.Token) { MaxConcurrentPreMerges = concurrency };
        _codeWriter = _codes.CreateWriter();
    }

    /// <summary>Names a contract code of the account section; repeated hashes collapse.</summary>
    public void AddCode(in ValueHash256 codeHash, uint size)
    {
        Span<byte> value = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(value, size);
        _codeWriter.Add(codeHash.Bytes, value);
    }

    /// <summary>Takes the next code chunk of the ascending code section, refusing one no added code accounts for.</summary>
    public void AddChunk(in PbtPath key, in ValueHash256 leaf)
    {
        if (leaf == default) throw new InvalidDataException("Noncanonical code chunk or PUSHDATA count.");
        _chunkWriter ??= MapStems();
        ReadOnlySpan<byte> stem = key.Bytes[1..^1];
        if (!stem.SequenceEqual(_stemCursor!.Key)) MatchStem(stem);
        Span<byte> chunkKey = stackalloc byte[ChunkKeyLength];
        _stemCode.Bytes.CopyTo(chunkKey);
        BinaryPrimitives.WriteUInt32BigEndian(chunkKey[ValueHash256.MemorySize..], _stemFirstChunk + key.Bytes[^1]);
        _chunkWriter.Add(chunkKey, leaf.Bytes);
    }

    /// <summary>Marks the end of the code section, starting the reassembly in the background on the first call.</summary>
    public void EndChunks()
    {
        if (_rebuild is not null) return;
        _codeWriter.Dispose();
        _chunkWriter?.Dispose();
        _stemCursor?.Dispose();
        _stemCursor = null;
        _rebuild = Task.Run(Reassemble, CancellationToken.None);
    }

    /// <summary>Waits for the reassembled codes to be written and flushed.</summary>
    public void Complete()
    {
        EndChunks();
        _rebuild!.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        try { _rebuild?.Wait(); }
        catch (AggregateException) { }
        _codeWriter.Dispose();
        _chunkWriter?.Dispose();
        _stemCursor?.Dispose();
        _codes.Dispose();
        _stems.Dispose();
        _chunks.Dispose();
        _cancellation.Dispose();
    }

    /// <summary>Spools the stem of every chunk group of the added codes, with the code hash and first chunk id the stem holds.</summary>
    /// <returns>The writer of the chunks spool.</returns>
    private SortedSpool.Writer MapStems()
    {
        _codeWriter.Dispose();
        using (SortedSpool.Writer stems = _stems.CreateWriter())
        using (SortedSpool.Cursor codes = _codes.Read())
        {
            Span<byte> value = stackalloc byte[ChunkKeyLength];
            while (codes.MoveNext())
            {
                codes.Key.CopyTo(value);
                long chunkCount = PbtKeyDerivation.CodeChunkCount(BinaryPrimitives.ReadUInt32LittleEndian(codes.Value));
                for (int firstChunk = 0; firstChunk < chunkCount; firstChunk += StemWidth)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(value[ValueHash256.MemorySize..], (uint)firstChunk);
                    PbtPath stemKey = Eip8297KeyDerivation.OverflowCodeKey(codes.Key, firstChunk);
                    stems.Add(stemKey.Bytes[1..^1], value);
                }
            }
        }
        _stemCursor = _stems.Read();
        return _chunks.CreateWriter();
    }

    /// <remarks>Spooled stems the code section lacks are groups of all-zero chunks, which the tree omits.</remarks>
    private void MatchStem(ReadOnlySpan<byte> stem)
    {
        int order;
        do
        {
            if (!_stemCursor!.MoveNext()) throw UnaccountedChunks();
            order = _stemCursor.Key.SequenceCompareTo(stem);
        } while (order < 0);
        if (order != 0) throw UnaccountedChunks();
        _stemCode = new ValueHash256(_stemCursor.Value[..ValueHash256.MemorySize]);
        _stemFirstChunk = BinaryPrimitives.ReadUInt32BigEndian(_stemCursor.Value[ValueHash256.MemorySize..]);
    }

    private static InvalidDataException UnaccountedChunks() => new("Snapshot holds code chunks no account's code accounts for.");

    private void Reassemble()
    {
        CancellationToken cancellationToken = _cancellation.Token;
        float walked = 0;
        ulong rebuilt = 0;
        object writeLock = new();
        IPbtPersistence.IWriteBatch? batch = null;
        int writes = 0;
        ParallelOptions options = new() { MaxDegreeOfParallelism = _concurrency, CancellationToken = cancellationToken };
        using ProgressReporter progress = PbtImageProgress.Start(CodePhase, "code", 0, _logManager);
        progress.Logger.SetFormat(p => PbtImageProgress.Format(CodePhase, walked, PbtImageProgress.Counted("code", p)));
        try
        {
            Parallel.ForEach(Partitioner.Create(Assembled(cancellationToken), EnumerablePartitionerOptions.NoBuffering), options,
                (entry, loop) =>
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        CodeInfo code = Rebuild(entry.Hash, entry.Size, entry.Leaves);
                        lock (writeLock)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            batch ??= _target.CreateStagingWriteBatch();
                            batch.SetCode(entry.Hash, code);
                            walked = Math.Max(walked, PbtImageProgress.KeyspaceFraction(entry.Hash));
                            progress.Update(++rebuilt);
                            if (++writes == PbtLeafIngestion.BatchSize) Commit();
                        }
                    }
                    catch
                    {
                        loop.Stop();
                        throw;
                    }
                });
            cancellationToken.ThrowIfCancellationRequested();
            Commit();
        }
        catch (AggregateException failures)
        {
            Exception failure = failures.Flatten().InnerExceptions[0];
            foreach (Exception cause in failures.Flatten().InnerExceptions)
            {
                if (cause is OperationCanceledException) continue;
                failure = cause;
                break;
            }
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally
        {
            batch?.Dispose();
        }
        _target.Flush();

        void Commit()
        {
            if (batch is null) return;
            batch.Commit();
            IPbtPersistence.IWriteBatch committed = batch;
            batch = null;
            committed.Dispose();
            writes = 0;
        }
    }

    /// <summary>Each added code with its chunks, absent chunks left zero, walking the codes and chunks spools side by side.</summary>
    private IEnumerable<(ValueHash256 Hash, int Size, ValueHash256[] Leaves)> Assembled(CancellationToken cancellationToken)
    {
        using SortedSpool.Cursor codes = _codes.Read();
        using SortedSpool.Cursor chunks = _chunks.Read();
        bool hasChunk = chunks.MoveNext();
        while (codes.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValueHash256 codeHash = new(codes.Key);
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(codes.Value);
            ValueHash256[] leaves = new ValueHash256[PbtKeyDerivation.CodeChunkCount(size)];
            for (; hasChunk && chunks.Key[..ValueHash256.MemorySize].SequenceEqual(codeHash.Bytes); hasChunk = chunks.MoveNext())
            {
                uint chunkId = BinaryPrimitives.ReadUInt32BigEndian(chunks.Key[ValueHash256.MemorySize..]);
                if (chunkId >= leaves.Length) throw UnaccountedChunks();
                leaves[chunkId] = new ValueHash256(chunks.Value);
            }
            yield return (codeHash, size, leaves);
        }
    }

    /// <summary>Reassembles <paramref name="size"/> bytes of code from its chunks, absent chunks reading as zeros,
    /// then requires the chunks to be exactly the code's canonical ones.</summary>
    private static CodeInfo Rebuild(in ValueHash256 codeHash, int size, ValueHash256[] leaves)
    {
        byte[] code = new byte[size];
        for (int chunk = 0; chunk < leaves.Length; chunk++) PbtKeyDerivation.CopyChunkCode(leaves[chunk].Bytes, chunk, code);

        if (ValueKeccak.Compute(code) != codeHash || Eip7702Constants.IsDelegatedCode(code))
            throw new InvalidDataException("Code bytes do not match the account code hash or delegation representation.");
        using NativeMemoryList<byte> chunkMemory = PbtKeyDerivation.ChunkifyCode(code);
        ReadOnlySpan<byte> encodedChunks = chunkMemory.AsSpan();
        for (int chunk = 0; chunk < leaves.Length; chunk++)
        {
            if (leaves[chunk] != new ValueHash256(encodedChunks.Slice(chunk * PbtKeyDerivation.CodeChunkSize, PbtKeyDerivation.CodeChunkSize)))
                throw new InvalidDataException("Noncanonical code chunk or PUSHDATA count.");
        }
        return new CodeInfo(code);
    }
}
