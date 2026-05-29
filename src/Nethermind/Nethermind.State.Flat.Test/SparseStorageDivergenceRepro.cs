// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Nethermind.Trie.Sparse;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Replays the 30 USDT slot updates that EXPB diagnostic captured as the first
/// SPARSE STORAGE DIVERGENCE at block 22360025. We can't reproduce USDT's actual
/// pre-block storage trie (it's millions of slots, requires mainnet snapshot), so
/// this test runs the SAME 30 updates over a smaller synthesized starting state.
/// If sparse and Patricia diverge here, the bug is in per-update logic and
/// independent of cross-block state. If they match, the bug is cross-block.
/// </summary>
[TestFixture]
public class SparseStorageDivergenceRepro
{
    // The 30 (slot, RLP-encoded value) pairs from EXPB run 26570166376.
    // Values are the raw EVM bytes (before RLP encoding) as captured by the diagnostic.
    private static readonly (UInt256 Slot, byte[] Value)[] UsdtUpdates =
    [
        (UInt256.Parse("24880548784460413565206321680149210674606117156704960678793268570165181055087"), Hex("0329CF7BE9")),
        (UInt256.Parse("66432190378958698982748037416721501974597557166684390034768808040963035027597"), Hex("0C8BC4EE")),
        (UInt256.Parse("54154433725855040597599936947229818939870848354138472930450730341202592042273"), Hex("053D")),
        (UInt256.Parse("4666533536884160153875929683164264545496940896313765415127204256547137608608"),  Hex("1823CF40")),
        (UInt256.Parse("54594398242127256168020648300364834754716832664957855491444373544627111999200"), Hex("01969848779E40")),
        (UInt256.Parse("1662939945458848929411175874323755463499963766534011902216711725837592650886"),  Hex("076ADF84")),
        (UInt256.Parse("8499339460709621932889829196415158328360717834089808628411183687464756841615"),  Hex("00")),
        (UInt256.Parse("100774577740991836489658534584707137339969436236139302717070667277057599548896"), Hex("0360ABDA19")),
        (UInt256.Parse("19735292588665622305008754747059467024430479447062626962922891164172414854753"), Hex("3B31E5822A")),
        (UInt256.Parse("93814260588281390960302870827067378211479301260448654608214190735670554499530"), Hex("05F5E100")),
        (UInt256.Parse("55409083425362913209108532393300178189024666377709523372236103604766776847546"), Hex("8645A321")),
        (UInt256.Parse("57846480528666089346974600914302275919821987969527313588706040524224246015222"), Hex("7BC8EC86")),
        (UInt256.Parse("105337562284535177966825304364387370391387040418577676007829002128637852364997"), Hex("0BB3B78A")),
        (UInt256.Parse("42962703007025507023510264055053811505298033430706698263988402499465482156817"), Hex("013DB16A")),
        (UInt256.Parse("87579920088409577074406663302538657763608640898557545177575394890769848170162"), Hex("00")),
        (UInt256.Parse("56451175213482809652738306103676735064013360338888027008133385773800954823816"), Hex("148625A29F")),
        (UInt256.Parse("94277138448614721016297393966151823589806760432022851576542795816519921569149"), Hex("0171E884744B")),
        (UInt256.Parse("100303017462220927203868629009148936612233804024508206812851136365061248367786"), Hex("3F528860A8")),
        (UInt256.Parse("58503166935794899529373963234700026353561458556759469400570547766664673378107"), Hex("0D30D8F486CF")),
        (UInt256.Parse("36140762719286893666201324923555612530141957914271939314731304696457654811039"), Hex("94FFE5C7")),
        (UInt256.Parse("66843475070601742227602128142808079298401838240208997658875598553070686940591"), Hex("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF")),
        (UInt256.Parse("39181872723613962231511786708157826484188080027633173665573010479360799163725"), Hex("07646520")),
        (UInt256.Parse("104256474729522689243594716596841574846249156829713318065379945043478392608047"), Hex("8770A414")),
        (UInt256.Parse("107476074695659601222706739085925993614568767376902281265617278208461174207115"), Hex("02160EC0")),
        (UInt256.Parse("51348809623707519075011937304197130709056269973978573069147643724076641194635"), Hex("022F8DD454")),
        (UInt256.Parse("26228100754620744046455276488116329195166822774550435633500677963289136681753"), Hex("47868C00")),
        (UInt256.Parse("77999172037640322263057202549010655824995975032059979209149790050149853960815"), Hex("21954220E798")),
        (UInt256.Parse("48690225930399837206938694953279783125702183599771526886819732034475729020329"), Hex("2934A8")),
        (UInt256.Parse("10872891300812054119806826129947410490614213788581151901982359718877908605826"), Hex("025EBDCE678D")),
        (UInt256.Parse("3477570045879978665933245088824668321381518639899723696854491909589541332791"),  Hex("1164AF1C")),
    ];

    private static byte[] Hex(string h) => Convert.FromHexString(h);

    /// <summary>
    /// Build a small Patricia storage trie, apply the 30 USDT updates via both
    /// Patricia and sparse (with proof-driven reveal), assert the resulting roots match.
    /// This isolates "per-update logic" vs "cross-block state accumulation".
    /// </summary>
    [Test]
    public void Replay_Usdt30Updates_AgainstSyntheticStartingState_RootsMustMatch()
    {
        MemDb db = new();
        Hash256 accountPathHash = TestItem.AddressA.ToAccountPath.ToHash256();
        StorageTree storage = new(new RawTrieStore(db).GetTrieStore(accountPathHash), LimboLogs.Instance);

        // Seed with a few slots so the trie isn't trivial.
        Random rng = new(7);
        for (int i = 0; i < 30; i++)
        {
            UInt256 seedSlot = new(BitConverter.ToUInt64(SeededBytes(rng, 8)), 0, 0, 0);
            byte[] seedValue = SeededBytes(rng, 4);
            storage.Set(seedSlot, seedValue);
        }
        storage.UpdateRootHash();
        storage.Commit();
        Hash256 seedRoot = storage.RootHash;

        // Apply the 30 USDT updates via Patricia
        foreach ((UInt256 slot, byte[] value) in UsdtUpdates)
            storage.Set(slot, value);
        storage.UpdateRootHash();
        storage.Commit();
        Hash256 patriciaPostRoot = storage.RootHash;

        // Now apply same via sparse
        HalfPathTrieNodeReader reader = new(new NodeStorage(db));
        using SparsePatriciaTree sparse = new();

        // Reveal initial proof for the first update target so the sparse trie has a starting
        // shape; subsequent reveal-update-retry iterations fill in the rest.
        ValueHash256 firstKeyBuf = default;
        StorageTree.ComputeKeyWithLookup(UsdtUpdates[0].Slot, ref firstKeyBuf);
        Hash256 firstTargetHash = firstKeyBuf.ToCommitment();
        DecodedMultiProof seedProof = MultiProofReader.ReadStorageProofs(
            reader, accountPathHash, seedRoot, [firstTargetHash]);
        if (seedProof.StorageNodes.TryGetValue(accountPathHash, out List<ProofNode>? seedNodes))
            sparse.RevealNodes(seedNodes);

        Dictionary<ValueHash256, LeafUpdate> updates = [];
        ValueHash256 keyBuf = default;
        foreach ((UInt256 slot, byte[] value) in UsdtUpdates)
        {
            StorageTree.ComputeKeyWithLookup(slot, ref keyBuf);
            Hash256 slotHash = keyBuf.ToCommitment();

            if (value.AsSpan().IndexOfAnyExcept((byte)0) < 0)
            {
                updates[slotHash] = LeafUpdate.Deleted();
            }
            else
            {
                Rlp rlpEncoded = Rlp.Encode(value);
                byte[]? encoded = rlpEncoded?.Bytes;
                updates[slotHash] = encoded is null || encoded.Length == 0
                    ? LeafUpdate.Deleted()
                    : LeafUpdate.Changed(encoded);
            }
        }

        // Reveal + update + retry loop (mirrors SparseRootComputer.ComputeStorageRoot)
        const int maxRetries = 10;
        for (int retry = 0; retry < maxRetries; retry++)
        {
            List<Hash256> targets = [];
            sparse.UpdateLeaves(updates, (key, _) => targets.Add(key.ToCommitment()));
            if (targets.Count == 0) break;

            DecodedMultiProof proof = MultiProofReader.ReadStorageProofs(
                reader, accountPathHash, seedRoot, targets.ToArray());
            if (proof.StorageNodes.TryGetValue(accountPathHash, out List<ProofNode>? nodes))
                sparse.RevealNodes(nodes);
        }

        Hash256 sparseRoot = sparse.ComputeRoot();
        sparseRoot.Should().Be(patriciaPostRoot,
            $"Sparse storage root must match Patricia for the 30 USDT updates. " +
            $"Patricia={patriciaPostRoot}, Sparse={sparseRoot}");
    }

    private static byte[] SeededBytes(Random rng, int len)
    {
        byte[] b = new byte[len];
        rng.NextBytes(b);
        return b;
    }

    /// <summary>
    /// USDT's actual storage root RLP at the failing block, captured from EXPB DIAG dump
    /// (prevRoot=0x8e4d94c2ec81dadf65c525fae4e79e407ef18dcfff7b6571f623b6cce8e61b07).
    /// This is a 16-child Branch where every slot is a 32-byte hash reference — the exact
    /// shape mainnet USDT has at depth 0 due to its millions-of-slots size.
    /// </summary>
    private const string UsdtRootRlpHex =
        "F90211" +
        "A0243985007268B9047754206ED0476C58E81A0D5B90E8A5D55E0B97AB6FB9DCCA" +
        "A0C792E96BE51C36508C571C2E9A4933EDD7057058299A3641B1AB074740773B09" +
        "A0C2DD8B24F8C2D6F71BF09016F41E6C0E9439035423996D89BFC873AE459ABE06" +
        "A02AAD4696C2ECDC8F8885A2F0EA90E9A50639ED628CD7BD51B6DA196EC5F8DF54" +
        "A046F8751BD15955EA4E51068EAA063565EF853C91FF296DA99E77A4B10C80EE36" +
        "A06C9FBD9856EFE489121F8730D0C4DBC5175083F649729CCAB89471B49A20F43F" +
        "A022AFEF3DEEF97F02335791D2C6E3F31BD88DABE8B166ACD62DEEC691E68CFA9E" +
        "A0B6925795639D87D32CEA94BF0C1CCA56CD62D60ACBF7CD22F6181FF6F749CCCC" +
        "A01296C14EB051E4D969D9DF3D83CE0CF13626A4EE4FB8E56796BC3ECB645BAEED" +
        "A0F6BB396C97CAE29DA5FFF8A1F83134AA541F2469F566733B2F0B5B7B295ACB15" +
        "A097D03EF19DBA1D1634EADD62A72EC16F928C56077DD13CA896B7CAD6EA36B535" +
        "A03AD88211B408CE27F84B7B3AF9314E28EA626DA5BBA6C35B96AEBA599F26BE92" +
        "A002F77F34BF77360C4D39E6A54AEE179EF9FBE883402B3B015E9866C75564FC7B" +
        "A0D46A361E38133090F9E163EF3B118E1BF6F909BA26421DD2D76B24E631F1B4D7" +
        "A0B206F8E128367C7C2F73E5979F74C5D758DB0591507968CCB537F0E21DF2A9FE" +
        "A06979C4A41A501B8F9D365D36C3773CA2E4329008DCD952A240FBC2BAAF3CC778" +
        "80";

    /// <summary>
    /// Direct decode + reveal of USDT's actual mainnet root RLP. Then check that the sparse
    /// trie's ComputeRoot() returns the same hash. If sparse mis-decodes/mis-encodes a 16-
    /// child branch with all hashed children, this catches it.
    /// </summary>
    [Test]
    public void Reveal_UsdtMainnetRoot_RoundTripsToSameHash()
    {
        byte[] rootRlp = Convert.FromHexString(UsdtRootRlpHex);
        Hash256 expectedHash = new("0x8e4d94c2ec81dadf65c525fae4e79e407ef18dcfff7b6571f623b6cce8e61b07");

        ProofNode rootProof = MultiProofReader.DecodeProofNode(rootRlp, TreePath.Empty);
        using SparsePatriciaTree sparse = new();
        sparse.RevealNodes([rootProof]);

        Hash256 computed = sparse.ComputeRoot();
        computed.Should().Be(expectedHash,
            "Decoding + re-encoding USDT's mainnet root RLP must produce the same hash. " +
            $"Expected={expectedHash}, Computed={computed}");
    }

    /// <summary>
    /// Hypothesis test: is sparse storage UpdateLeaves order-dependent? Apply the same 30
    /// updates in 5 different shuffled orders and assert the resulting root is identical.
    /// If sparse is canonical (as it should be), all 5 roots match. If a specific ordering
    /// produces a different root, we have an order-dependency bug.
    /// </summary>
    [Test]
    public void Sparse_UpdateOrder_MustBeCanonical()
    {
        MemDb db = new();
        Hash256 accountPathHash = TestItem.AddressA.ToAccountPath.ToHash256();
        StorageTree storage = new(new RawTrieStore(db).GetTrieStore(accountPathHash), LimboLogs.Instance);

        // Seed with 1000 random slots to give complex structure
        Random rng = new(42);
        for (int i = 0; i < 1000; i++)
        {
            byte[] keyBytes = new byte[32];
            rng.NextBytes(keyBytes);
            UInt256 seedSlot = new(keyBytes);
            byte[] seedValue = SeededBytes(rng, 4);
            storage.Set(seedSlot, seedValue);
        }
        storage.UpdateRootHash();
        storage.Commit();
        Hash256 seedRoot = storage.RootHash;

        HalfPathTrieNodeReader reader = new(new NodeStorage(db));

        Hash256? firstRoot = null;

        // Try 5 different orderings of the 30 USDT updates
        for (int shuffleSeed = 100; shuffleSeed < 105; shuffleSeed++)
        {
            (UInt256, byte[])[] shuffled = (((UInt256 Slot, byte[] Value)[])UsdtUpdates.Clone())
                .Select(t => (t.Slot, t.Value))
                .ToArray();
            Random shuffleRng = new(shuffleSeed);
            for (int i = shuffled.Length - 1; i > 0; i--)
            {
                int j = shuffleRng.Next(i + 1);
                (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
            }

            using SparsePatriciaTree sparse = new();

            ValueHash256 firstKeyBuf = default;
            StorageTree.ComputeKeyWithLookup(shuffled[0].Item1, ref firstKeyBuf);
            DecodedMultiProof seedProof = MultiProofReader.ReadStorageProofs(
                reader, accountPathHash, seedRoot, [firstKeyBuf.ToCommitment()]);
            if (seedProof.StorageNodes.TryGetValue(accountPathHash, out List<ProofNode>? seedNodes))
                sparse.RevealNodes(seedNodes);

            Dictionary<ValueHash256, LeafUpdate> updates = [];
            ValueHash256 keyBuf = default;
            foreach ((UInt256 slot, byte[] value) in shuffled)
            {
                StorageTree.ComputeKeyWithLookup(slot, ref keyBuf);
                Hash256 slotHash = keyBuf.ToCommitment();
                if (value.AsSpan().IndexOfAnyExcept((byte)0) < 0)
                {
                    updates[slotHash] = LeafUpdate.Deleted();
                }
                else
                {
                    Rlp rlpEncoded = Rlp.Encode(value);
                    byte[]? encoded = rlpEncoded?.Bytes;
                    updates[slotHash] = encoded is null || encoded.Length == 0
                        ? LeafUpdate.Deleted()
                        : LeafUpdate.Changed(encoded);
                }
            }

            for (int retry = 0; retry < 10; retry++)
            {
                List<Hash256> targets = [];
                sparse.UpdateLeaves(updates, (key, _) => targets.Add(key.ToCommitment()));
                if (targets.Count == 0) break;
                DecodedMultiProof proof = MultiProofReader.ReadStorageProofs(
                    reader, accountPathHash, seedRoot, targets.ToArray());
                if (proof.StorageNodes.TryGetValue(accountPathHash, out List<ProofNode>? nodes))
                    sparse.RevealNodes(nodes);
            }

            Hash256 sparseRoot = sparse.ComputeRoot();
            if (firstRoot is null) firstRoot = sparseRoot;
            else sparseRoot.Should().Be(firstRoot,
                $"shuffleSeed={shuffleSeed}: sparse root must be order-independent");
        }
    }

    /// <summary>
    /// More aggressive reproducer: 10,000 random slots make a much deeper storage trie
    /// (multiple extension levels, large branches, mix of inline and hashed children).
    /// Apply the 30 USDT updates and validate sparse == Patricia. This shape is closer to
    /// USDT's actual mainnet trie depth.
    /// </summary>
    [Test]
    [TestCase(0, 10_000)]
    [TestCase(0, 100_000)]
    public void Replay_Usdt30Updates_AgainstDeepStartingState_RootsMustMatch(int seed, int seedCount)
    {
        MemDb db = new();
        Hash256 accountPathHash = TestItem.AddressA.ToAccountPath.ToHash256();
        StorageTree storage = new(new RawTrieStore(db).GetTrieStore(accountPathHash), LimboLogs.Instance);

        // Deep seed: many random slots → many trie levels, varied node shapes.
        Random rng = new(seed);
        for (int i = 0; i < seedCount; i++)
        {
            byte[] keyBytes = new byte[32];
            rng.NextBytes(keyBytes);
            UInt256 seedSlot = new(keyBytes);
            int valueLen = 1 + rng.Next(31); // mix of value sizes
            byte[] seedValue = SeededBytes(rng, valueLen);
            storage.Set(seedSlot, seedValue);
        }
        storage.UpdateRootHash();
        storage.Commit();
        Hash256 seedRoot = storage.RootHash;

        foreach ((UInt256 slot, byte[] value) in UsdtUpdates)
            storage.Set(slot, value);
        storage.UpdateRootHash();
        storage.Commit();
        Hash256 patriciaPostRoot = storage.RootHash;

        HalfPathTrieNodeReader reader = new(new NodeStorage(db));
        using SparsePatriciaTree sparse = new();

        // Reveal seed proof
        ValueHash256 firstKeyBuf = default;
        StorageTree.ComputeKeyWithLookup(UsdtUpdates[0].Slot, ref firstKeyBuf);
        Hash256 firstTargetHash = firstKeyBuf.ToCommitment();
        DecodedMultiProof seedProof = MultiProofReader.ReadStorageProofs(
            reader, accountPathHash, seedRoot, [firstTargetHash]);
        if (seedProof.StorageNodes.TryGetValue(accountPathHash, out List<ProofNode>? seedNodes))
            sparse.RevealNodes(seedNodes);

        Dictionary<ValueHash256, LeafUpdate> updates = [];
        ValueHash256 keyBuf = default;
        foreach ((UInt256 slot, byte[] value) in UsdtUpdates)
        {
            StorageTree.ComputeKeyWithLookup(slot, ref keyBuf);
            Hash256 slotHash = keyBuf.ToCommitment();

            if (value.AsSpan().IndexOfAnyExcept((byte)0) < 0)
            {
                updates[slotHash] = LeafUpdate.Deleted();
            }
            else
            {
                Rlp rlpEncoded = Rlp.Encode(value);
                byte[]? encoded = rlpEncoded?.Bytes;
                updates[slotHash] = encoded is null || encoded.Length == 0
                    ? LeafUpdate.Deleted()
                    : LeafUpdate.Changed(encoded);
            }
        }

        const int maxRetries = 10;
        for (int retry = 0; retry < maxRetries; retry++)
        {
            List<Hash256> targets = [];
            sparse.UpdateLeaves(updates, (key, _) => targets.Add(key.ToCommitment()));
            if (targets.Count == 0) break;

            DecodedMultiProof proof = MultiProofReader.ReadStorageProofs(
                reader, accountPathHash, seedRoot, targets.ToArray());
            if (proof.StorageNodes.TryGetValue(accountPathHash, out List<ProofNode>? nodes))
                sparse.RevealNodes(nodes);
        }

        Hash256 sparseRoot = sparse.ComputeRoot();
        sparseRoot.Should().Be(patriciaPostRoot,
            $"seed={seed}: Sparse storage root must match Patricia for 30 USDT updates over a 10k-slot deep trie. " +
            $"Patricia={patriciaPostRoot}, Sparse={sparseRoot}");
    }
}
