// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Nethermind.Blockchain;
using Nethermind.Consensus.Decoders;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

/// <summary>
/// Measures how many real transactions fit in one EIP-7805 inclusion list, against a corpus of transactions
/// captured from a live mempool.
/// </summary>
/// <remarks>
/// The corpus is a text file of one canonical EIP-2718 <c>TransactionType || TransactionPayload</c> hex string
/// per line — the form <c>eth_getRawTransactionByHash</c> returns — named by the <c>NETHERMIND_IL_TX_CORPUS</c>
/// environment variable. Each entry is re-encoded through <see cref="InclusionListDecoder.EncodePooled"/> and
/// checked byte-for-byte against the captured bytes, so the reported sizes are the builder's own units rather
/// than a second encoder's. Blob transactions are dropped and their count reported, since the builder's
/// snapshot cannot reach them.
///
/// The list count comes from running <see cref="InclusionListBuilder"/> itself over a pool of one transaction
/// per sender, which is the shape a live pool's ready set overwhelmingly takes. The builder draws senders
/// uniformly, so the count varies run to run and the harness reports its distribution rather than a mean alone.
/// </remarks>
[TestFixture]
[Explicit("measurement harness; needs a captured mempool corpus")]
public class InclusionListSizeMeasurement
{
    private const string CorpusVariable = "NETHERMIND_IL_TX_CORPUS";
    private const int Draws = 2000;
    private const int SszOffsetBytes = 4;

    // Below this fraction of the cap, the average draw is corpus-bound rather than cap-bound and proves nothing.
    private const double MinSaturationFraction = 0.95;

    [Test]
    public void Measure()
    {
        Transaction[] txs = LoadCorpus();
        int[] sizes = new int[txs.Length];
        for (int i = 0; i < txs.Length; i++)
        {
            using ArrayPoolList<byte> encoded = InclusionListDecoder.EncodePooled(txs[i]);
            sizes[i] = encoded.Count;
        }

        Array.Sort(sizes);
        long total = 0;
        foreach (int size in sizes) total += size;

        TestContext.Out.WriteLine($"RESULT corpus transactions={sizes.Length} mean={(double)total / sizes.Length:F1}B");
        foreach (double q in new[] { 0.01, 0.1, 0.25, 0.5, 0.75, 0.9, 0.99 })
        {
            TestContext.Out.WriteLine($"RESULT size p{q * 100:0.##}={sizes[(int)(q * (sizes.Length - 1))]}B");
        }
        TestContext.Out.WriteLine($"RESULT size min={sizes[0]}B max={sizes[^1]}B");

        // Even one list containing every corpus transaction couldn't reach the cap, so no draw ever will.
        if (total < Eip7805Constants.MaxBytesPerInclusionList)
        {
            Assert.Inconclusive($"Corpus totals {total}B, below the {Eip7805Constants.MaxBytesPerInclusionList}B cap — " +
                                 "not even the whole corpus fills one list. Supply a larger corpus.");
        }

        MeasureListCounts(txs);
    }

    /// <summary>Runs the real builder repeatedly over the corpus and reports the entries per list.</summary>
    private static void MeasureListCounts(Transaction[] txs)
    {
        InclusionListBuilder builder = BuildBuilder(PoolOfOneTxPerSender(txs));
        int[] counts = new int[Draws];
        long totalBytes = 0;
        for (int i = 0; i < Draws; i++)
        {
            using InclusionListBytes list = builder.GetInclusionList();
            counts[i] = list.Count;
            foreach (ArrayPoolList<byte> entry in list) totalBytes += entry.Count;
        }

        Array.Sort(counts);
        long sum = 0;
        foreach (int count in counts) sum += count;
        double mean = (double)sum / Draws;

        TestContext.Out.WriteLine($"RESULT list entries mean={mean:F1} min={counts[0]} p10={counts[Draws / 10]} " +
                                  $"p50={counts[Draws / 2]} p90={counts[Draws * 9 / 10]} max={counts[^1]}");
        TestContext.Out.WriteLine($"RESULT list bytes mean={(double)totalBytes / Draws:F0} of " +
                                  $"{Eip7805Constants.MaxBytesPerInclusionList}, " +
                                  $"with SSZ offsets {(double)(totalBytes + (long)SszOffsetBytes * sum) / Draws:F0}");

        // On the mean, not the best draw: the statistics above describe the typical draw, so one lucky draw
        // reaching the cap must not vouch for them.
        long meanBytes = totalBytes / Draws;
        if (meanBytes < (long)(Eip7805Constants.MaxBytesPerInclusionList * MinSaturationFraction))
        {
            Assert.Inconclusive($"Draws average {meanBytes}B of the {Eip7805Constants.MaxBytesPerInclusionList}B cap " +
                                 $"({MinSaturationFraction:P0} threshold) — draws are corpus-bound, not cap-bound. Supply a larger corpus.");
        }
    }

    private static Transaction[] LoadCorpus()
    {
        string? path = Environment.GetEnvironmentVariable(CorpusVariable);
        if (string.IsNullOrEmpty(path))
        {
            Assert.Ignore($"Set {CorpusVariable} to a file of raw transaction hex, one per line.");
        }

        if (!File.Exists(path))
        {
            Assert.Fail($"{CorpusVariable} is set to '{path}', but no such file exists.");
        }

        string[] lines = File.ReadAllLines(path!);
        List<byte[]> raw = new(lines.Length);
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0) raw.Add(Bytes.FromHexString(trimmed));
        }

        if (raw.Count == 0)
        {
            Assert.Fail($"{CorpusVariable} file '{path}' contains no transaction lines.");
        }

        byte[][] entries = raw.ToArray();
        TransactionDecodingResult decoded = TxsDecoder.DecodeTxs(entries, skipErrors: false);
        Assert.That(decoded.Error, Is.Null, "corpus entry is not a valid EIP-2718 transaction");

        Transaction[] txs = decoded.Transactions;
        for (int i = 0; i < txs.Length; i++)
        {
            using ArrayPoolList<byte> reEncoded = InclusionListDecoder.EncodePooled(txs[i]);
            Assert.That(reEncoded.AsSpan().ToArray(), Is.EqualTo(entries[i]),
                $"entry {i} does not re-encode to the bytes it was captured as");
        }

        // The builder draws from the pool's non-blob snapshot alone, so blob txs are outside its input domain.
        // A mined-block capture carries them, so drop them here rather than reject the capture.
        Transaction[] appendable = Array.FindAll(txs, static tx => !tx.SupportsBlobs);
        TestContext.Out.WriteLine($"RESULT corpus blob transactions excluded={txs.Length - appendable.Length}");
        Assert.That(appendable, Is.Not.Empty, $"{CorpusVariable} file '{path}' holds only blob transactions.");

        return appendable;
    }

    /// <summary>A pool whose ready set is one transaction per sender, as the corpus was captured.</summary>
    private static ITxPool PoolOfOneTxPerSender(Transaction[] txs)
    {
        Dictionary<AddressAsKey, Transaction[]> bySender = new(txs.Length);
        for (int i = 0; i < txs.Length; i++)
        {
            // Captured entries carry no recovered sender, and recovering one would only re-derive a key the
            // bucket layout already fixes: one transaction each, so no two share a bucket.
            byte[] address = new byte[Address.Size];
            BinaryPrimitives.WriteInt32BigEndian(address.AsSpan(Address.Size - sizeof(int)), i);
            bySender[new AddressAsKey(new Address(address))] = [txs[i]];
        }

        ITxPool pool = Substitute.For<ITxPool>();
        pool.GetPendingTransactionsBySender(Arg.Any<bool>(), Arg.Any<UInt256>()).Returns(bySender);
        return pool;
    }

    /// <summary>A builder over a zero-base-fee head, so the corpus is priced in rather than filtered out.</summary>
    private static InclusionListBuilder BuildBuilder(ITxPool pool)
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithBaseFeePerGas(UInt256.Zero).TestObject);
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(Frontier.Instance);
        return new InclusionListBuilder(pool, blockTree, specProvider);
    }
}
