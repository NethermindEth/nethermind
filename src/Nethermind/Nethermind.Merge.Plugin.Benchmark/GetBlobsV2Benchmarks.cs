// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using CkzgLib;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Benchmark;

/// <summary>
/// The work of <c>engine_getBlobsV2</c> on a blob pool cache miss: the stored transaction is read and decoded, and
/// its blobs and cell proofs are written to the response.
/// </summary>
[MemoryDiagnoser]
public class GetBlobsV2Benchmarks
{
    private const int BlobCount = 6;

    private BlobTxStorage _storage = null!;
    private TxLookupKey[] _keys = null!;
    private readonly Transaction?[] _read = new Transaction?[1];
    private readonly byte[]?[] _blobs = new byte[BlobCount][];
    private readonly ReadOnlyMemory<byte[]>[] _proofs = new ReadOnlyMemory<byte[]>[BlobCount];
    private readonly PipeWriter _output = PipeWriter.Create(Stream.Null);

    [GlobalSetup]
    public void Setup()
    {
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(BlobCount, spec: Osaka.Instance)
            .SignedAndResolved(new EthereumEcdsa(BlockchainIds.Mainnet), TestItem.PrivateKeyA)
            .TestObject;
        _storage = new BlobTxStorage();
        _storage.Add(tx);
        _keys = [new TxLookupKey(tx.Hash!.ValueHash256, tx.SenderAddress!, tx.Timestamp)];
    }

    [Benchmark]
    public async Task ReadAndWriteResponse()
    {
        _storage.TryGetMany(_keys, 1, _read);
        ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)_read[0]!.NetworkWrapper!;
        for (int i = 0; i < BlobCount; i++)
        {
            _blobs[i] = wrapper.Blobs[i];
            _proofs[i] = new ReadOnlyMemory<byte[]>(wrapper.Proofs, i * Ckzg.CellsPerExtBlob, Ckzg.CellsPerExtBlob);
        }

        using BlobsV2DirectResponse response = new(_blobs, _proofs, BlobCount);
        await response.WriteToAsync(_output, default);
    }
}
