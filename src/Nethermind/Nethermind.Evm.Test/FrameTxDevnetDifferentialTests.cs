// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Cross-client differential check against the ethrex public EIP-8141 devnet. Consumes a corpus
/// of real frame transactions (produced by devnet/passive-diff/fetch_frame_txs.py) and asserts
/// the Nethermind decoder round-trips the exact wire bytes ethrex produced — the cheapest proof
/// that the two clients agree on the frame transaction encoding, with no node required.
///
/// Explicit: never runs in CI. Point NM_FRAME_CORPUS at the fetched frame_tx_corpus.json and run
/// this test manually.
/// </summary>
[TestFixture, Explicit("requires a frame tx corpus fetched from the devnet (NM_FRAME_CORPUS)")]
public class FrameTxDevnetDifferentialTests
{
    private static readonly TxDecoder _txDecoder = TxDecoder.Instance;

    [Test]
    public void RawFrameTxs_FromDevnet_RoundTripThroughOurDecoder()
    {
        string? path = Environment.GetEnvironmentVariable("NM_FRAME_CORPUS");
        Assert.That(path, Is.Not.Null.And.Not.Empty, "set NM_FRAME_CORPUS to the fetched corpus json");
        Assert.That(File.Exists(path), $"corpus not found at {path}");

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path!));
        JsonElement entries = doc.RootElement.GetProperty("entries");
        Assert.That(entries.GetArrayLength(), Is.GreaterThan(0), "corpus is empty");

        int checkedCount = 0;
        foreach (JsonElement entry in entries.EnumerateArray())
        {
            string txHash = entry.GetProperty("txHash").GetString()!;
            string rawHex = entry.GetProperty("rawTx").GetString()!;
            byte[] raw = Bytes.FromHexString(rawHex);

            // Decode ethrex's wire bytes with our decoder.
            Transaction decoded = Rlp.Decode<Transaction>(raw, RlpBehaviors.SkipTypedWrapping);
            Assert.That(decoded.Type, Is.EqualTo(TxType.FrameTx), $"{txHash}: type");
            Assert.That(decoded.Frames, Is.Not.Null.And.Not.Empty, $"{txHash}: frames");

            // Re-encode and assert byte-for-byte parity — proves identical wire encoding.
            RlpStream stream = new(_txDecoder.GetLength(decoded, RlpBehaviors.SkipTypedWrapping));
            _txDecoder.Encode(stream, decoded, RlpBehaviors.SkipTypedWrapping);
            Assert.That(stream.Data.ToArray().ToHexString(), Is.EqualTo(raw.ToHexString()),
                $"{txHash}: re-encoded bytes must match ethrex's wire bytes");

            checkedCount++;
        }

        TestContext.Out.WriteLine($"round-tripped {checkedCount} devnet frame txs");
    }
}
