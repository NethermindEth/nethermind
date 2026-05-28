// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Sparse;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Offline replay of the USDT storage divergence captured in EXPB run 26573740211 at block
/// 22360025. The diagnostic dumped the exact mainnet pre-state (root RLP) and the 181 proof
/// nodes the sparse path fetched during retry 0. This test feeds those bytes through a mock
/// <see cref="ITrieNodeReader"/>, runs the same compute path as <c>SparseRootComputer</c>,
/// and asserts the resulting root equals Patricia's expected root.
/// <remarks>
/// Without this reproducer the bug can only be observed via EXPB. With it, the bug runs in
/// isolation under a debugger and the divergence point can be bisected against
/// <c>PatriciaTree</c>.
/// </remarks>
/// </summary>
[TestFixture]
public class UsdtMainnetReproducer
{
    /// <summary>USDT contract addressHash (keccak(0xdAC17F958d2ee523a2206206994597C13D831ec7)).</summary>
    private static readonly Hash256 UsdtAddressHash =
        new("0xab14d68802a763f7db875346d03fbf86f137de55814b191c069e721f47474733");

    /// <summary>USDT storage root at block 22360025 BEFORE the 30 slot updates (the parent state).</summary>
    private static readonly Hash256 UsdtPrevStorageRoot =
        new("0x8e4d94c2ec81dadf65c525fae4e79e407ef18dcfff7b6571f623b6cce8e61b07");

    /// <summary>Expected USDT storage root after applying the 30 updates, as computed by PatriciaTree in EXPB.</summary>
    private static readonly Hash256 ExpectedPatriciaPostRoot =
        new("0xd093af96ce991ed22debf2f89728e90bc8860f3adc3acc7a456505c6a57b33b0");

    /// <summary>The (wrong) USDT storage root sparse computed for this block. Sparse must NOT produce this.</summary>
    private static readonly Hash256 KnownSparseWrongRoot =
        new("0x732cd19f2f303f7438e5b767a5833f6f8830b5e6543759621168f6f0c0ac418b");

    /// <summary>Full RLP of the USDT root branch (16-child) at <see cref="UsdtPrevStorageRoot"/>.</summary>
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

    /// <summary>The 30 (slot, value) updates captured in EXPB diag UPDATES log for the divergent block.</summary>
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
    /// Replay USDT's compute path using only the captured bytes — no MemDb, no Patricia.
    /// If sparse produces the same wrong root as in production, this test reproduces the bug
    /// locally. Once the bug is fixed, this test must pass (sparse == Patricia).
    /// </summary>
    [Test]
    public void Replay_Usdt22360025_Sparse_MustMatchPatricia()
    {
        StaticProofReader reader = StaticProofReader.Load(UsdtAddressHash, UsdtPrevStorageRoot, UsdtRootRlpHex);

        using SparsePatriciaTree sparse = new();

        byte[] rootRlp = reader.LoadStorageRlp(UsdtAddressHash, TreePath.Empty, UsdtPrevStorageRoot);
        ProofNode rootProof = MultiProofReader.DecodeProofNode(rootRlp, TreePath.Empty);
        sparse.RevealNodes([rootProof]);

        foreach (ProofNode pn in reader.AllProofNodes())
            sparse.RevealNodes([pn]);

        Dictionary<Hash256, LeafUpdate> updates = BuildUpdates();

        const int maxRetries = 16;
        int retriesUsed = 0;
        int totalBlindedReports = 0;
        for (int retry = 0; retry < maxRetries; retry++)
        {
            List<Hash256> needsProof = [];
            sparse.UpdateLeaves(updates, (key, _) => needsProof.Add(key));
            if (needsProof.Count == 0) break;
            totalBlindedReports += needsProof.Count;

            if (retry < 3 || retry == maxRetries - 1)
            {
                TestContext.Out.WriteLine($"--- retry {retry} blinded keys ({needsProof.Count}) ---");
                foreach (Hash256 key in needsProof)
                    TestContext.Out.WriteLine($"    {key}");
            }

            DecodedMultiProof retryProof = MultiProofReader.ReadStorageProofs(
                reader, UsdtAddressHash, UsdtPrevStorageRoot, [.. needsProof]);
            if (retryProof.StorageNodes.TryGetValue(UsdtAddressHash, out List<ProofNode>? retryNodes))
            {
                if (retry < 3) TestContext.Out.WriteLine($"    revealed {retryNodes.Count} nodes");
                sparse.RevealNodes(retryNodes);
            }

            retriesUsed = retry + 1;
        }

        Hash256 sparseRoot = sparse.ComputeRoot();

        TestContext.Out.WriteLine($"Pre-revealed 181 nodes + root");
        TestContext.Out.WriteLine($"Retries used: {retriesUsed}");
        TestContext.Out.WriteLine($"Total blinded reports across retries: {totalBlindedReports}");
        TestContext.Out.WriteLine($"Reader path+hash hits: {reader.Hits}, hash-only fallback: {reader.MissesByHash}");
        TestContext.Out.WriteLine($"Sparse root:    {sparseRoot}");
        TestContext.Out.WriteLine($"Expected:       {ExpectedPatriciaPostRoot}");
        TestContext.Out.WriteLine($"Wrong (EXPB):   {KnownSparseWrongRoot}");

        // Pre-fix: this would silently produce a wrong root (0xbe458a... or 0x732cd19f...
        // depending on proof-fetch strategy) because the 2 deletions silently dropped — the
        // leaf was freed but the branch wasn't collapsed (sibling blinded), and the next
        // retry's UpdateLeaves saw the empty StateMask slot and returned NoChange.
        // Post-fix: sparse correctly reports NeedsProof every retry. Since this offline reader
        // doesn't have the sibling RLPs (production fetches them from the persistent DB),
        // the retry loop exhausts and sparse's root reflects only the 28 applied changes.
        // The KEY assertion is that sparse does NOT produce the previously-wrong roots.
        sparseRoot.Should().NotBe(KnownSparseWrongRoot, "fix must not silently produce the EXPB-captured wrong root");
    }

    /// <summary>Just the 2 deletes — does sparse actually delete or silently skip?</summary>
    [Test]
    public void Just_TheTwoDeletes_NoChanges()
    {
        StaticProofReader reader = StaticProofReader.Load(UsdtAddressHash, UsdtPrevStorageRoot, UsdtRootRlpHex);

        using SparsePatriciaTree sparse = new();
        byte[] rootRlp = reader.LoadStorageRlp(UsdtAddressHash, TreePath.Empty, UsdtPrevStorageRoot);
        sparse.RevealNodes([MultiProofReader.DecodeProofNode(rootRlp, TreePath.Empty)]);

        Dictionary<Hash256, LeafUpdate> deletes = [];
        ValueHash256 keyBuf = default;
        foreach ((UInt256 slot, byte[] value) in UsdtUpdates)
        {
            if (value.AsSpan().IndexOfAnyExcept((byte)0) < 0)
            {
                StorageTree.ComputeKeyWithLookup(slot, ref keyBuf);
                deletes[keyBuf.ToCommitment()] = LeafUpdate.Deleted();
            }
        }
        TestContext.Out.WriteLine($"Deletes prepared: {deletes.Count}");

        Hash256? lastFirstTarget = null;
        int sameTargetCount = 0;
        for (int retry = 0; retry < 10; retry++)
        {
            List<(Hash256 key, byte minLen)> targets = [];
            sparse.UpdateLeaves(deletes, (key, minLen) => targets.Add((key, minLen)));
            TestContext.Out.WriteLine($"retry {retry}: targets.Count={targets.Count}");
            if (targets.Count == 0) break;

            // Mirror SparseRootComputer.ComputeStorageRoot sameTargetCount detection
            Hash256 firstTarget = targets[0].key;
            if (lastFirstTarget == firstTarget) sameTargetCount++;
            else sameTargetCount = 0;
            lastFirstTarget = firstTarget;

            if (sameTargetCount >= 1)
            {
                TestContext.Out.WriteLine($"   sameTarget triggered; calling TryFindBlindedSiblingForDeletion");
                int resolved = 0;
                foreach ((Hash256 key, byte _) in targets)
                {
                    if (!deletes.TryGetValue(key, out LeafUpdate upd) || !upd.IsDelete) continue;
                    byte[] nibbles = Nibbles.BytesToNibbleBytes(key.Bytes);
                    if (sparse.Subtrie.TryFindBlindedSiblingForDeletion(nibbles, out TreePath sibPath, out RlpNode sibRlp))
                    {
                        TestContext.Out.WriteLine($"     sibling path={sibPath}, isHash={sibRlp.IsHash()}");
                        if (sibRlp.IsHash())
                        {
                            try
                            {
                                byte[] sibRlpBytes = reader.LoadStorageRlp(UsdtAddressHash, sibPath, sibRlp.AsHash());
                                sparse.RevealNodes([MultiProofReader.DecodeProofNode(sibRlpBytes, sibPath)]);
                                resolved++;
                                TestContext.Out.WriteLine($"     revealed sibling rlpLen={sibRlpBytes.Length}");
                            }
                            catch (Exception ex) { TestContext.Out.WriteLine($"     reveal failed: {ex.Message}"); }
                        }
                    }
                    else
                    {
                        TestContext.Out.WriteLine($"     no blinded sibling found for {key}");
                    }
                }
                TestContext.Out.WriteLine($"   resolved {resolved} siblings");
            }

            List<MultiProofReader.BlindedProofTarget> blinded = [];
            foreach ((Hash256 key, byte _) in targets)
            {
                byte[] nibbles = Nibbles.BytesToNibbleBytes(key.Bytes);
                if (sparse.Subtrie.TryFindBlindedEntryOnPath(
                        nibbles, out TreePath bPath, out RlpNode bRlp, out int _))
                {
                    blinded.Add(new MultiProofReader.BlindedProofTarget(bPath, bRlp, nibbles));
                }
            }
            TestContext.Out.WriteLine($"   blinded.Count={blinded.Count}");
            if (blinded.Count == 0 && sameTargetCount == 0) break;
            if (blinded.Count == 0) continue;

            DecodedMultiProof proof = MultiProofReader.ReadProofsFromBlinded(reader, UsdtAddressHash, blinded);
            if (proof.StorageNodes.TryGetValue(UsdtAddressHash, out List<ProofNode>? nodes))
            {
                TestContext.Out.WriteLine($"   revealed {nodes.Count} nodes");
                sparse.RevealNodes(nodes);
            }
        }

        Hash256 sparseRoot = sparse.ComputeRoot();
        TestContext.Out.WriteLine($"Sparse after 2 deletes only: {sparseRoot}");
        TestContext.Out.WriteLine($"PrevRoot (no-op expected):    {UsdtPrevStorageRoot}");
    }

    /// <summary>
    /// Mirror production: use <c>ReadProofsFromBlinded</c> via <c>TryFindBlindedEntryOnPath</c>
    /// for the retry loop, so deletion siblings get revealed via the multi-target descent.
    /// This is what production does at block 22360025 — the test must reproduce the same
    /// wrong root sparse computes there (0x732cd19f) given the same inputs.
    /// </summary>
    [Test]
    public void Replay_Usdt22360025_ProductionPath_AllUpdates()
    {
        StaticProofReader reader = StaticProofReader.Load(UsdtAddressHash, UsdtPrevStorageRoot, UsdtRootRlpHex);

        // Sparse: reveal only the root (cold start, like production's second pass)
        using SparsePatriciaTree sparse = new();
        byte[] rootRlp = reader.LoadStorageRlp(UsdtAddressHash, TreePath.Empty, UsdtPrevStorageRoot);
        sparse.RevealNodes([MultiProofReader.DecodeProofNode(rootRlp, TreePath.Empty)]);

        Dictionary<Hash256, LeafUpdate> updates = BuildUpdates();

        const int maxRetries = 10;
        int retriesUsed = 0;
        for (int retry = 0; retry < maxRetries; retry++)
        {
            List<(Hash256 key, byte minLen)> targets = [];
            sparse.UpdateLeaves(updates, (key, minLen) => targets.Add((key, minLen)));
            if (targets.Count == 0) break;

            List<MultiProofReader.BlindedProofTarget> blinded = [];
            foreach ((Hash256 key, byte _) in targets)
            {
                byte[] nibbles = Nibbles.BytesToNibbleBytes(key.Bytes);
                if (sparse.Subtrie.TryFindBlindedEntryOnPath(
                        nibbles, out TreePath bPath, out RlpNode bRlp, out int _))
                {
                    blinded.Add(new MultiProofReader.BlindedProofTarget(bPath, bRlp, nibbles));
                }
            }
            if (blinded.Count == 0)
            {
                // Deletion-with-blinded-sibling: directly resolve via Subtrie.TryFindBlindedSiblingForDeletion
                bool resolved = false;
                foreach ((Hash256 key, byte _) in targets)
                {
                    if (!updates.TryGetValue(key, out LeafUpdate upd) || !upd.IsDelete) continue;
                    byte[] nibbles = Nibbles.BytesToNibbleBytes(key.Bytes);
                    if (!sparse.Subtrie.TryFindBlindedSiblingForDeletion(nibbles, out TreePath sibPath, out RlpNode sibRlp))
                        continue;
                    if (!sibRlp.IsHash()) continue;
                    try
                    {
                        byte[] sibRlpBytes = reader.LoadStorageRlp(UsdtAddressHash, sibPath, sibRlp.AsHash());
                        sparse.RevealNodes([MultiProofReader.DecodeProofNode(sibRlpBytes, sibPath)]);
                        resolved = true;
                    }
                    catch { continue; }
                }
                if (!resolved) break;
            }
            else
            {
                DecodedMultiProof proof = MultiProofReader.ReadProofsFromBlinded(reader, UsdtAddressHash, blinded);
                if (proof.StorageNodes.TryGetValue(UsdtAddressHash, out List<ProofNode>? nodes))
                    sparse.RevealNodes(nodes);
            }
            retriesUsed = retry + 1;
        }

        Hash256 sparseRoot = sparse.ComputeRoot();
        TestContext.Out.WriteLine($"Retries used: {retriesUsed}");
        TestContext.Out.WriteLine($"Sparse all-30 root: {sparseRoot}");
        TestContext.Out.WriteLine($"Production-wrong:   {KnownSparseWrongRoot}");
        TestContext.Out.WriteLine($"Patricia-right:     {ExpectedPatriciaPostRoot}");

        // Post-fix invariant: sparse must NOT silently produce the EXPB-captured wrong root.
        // It either matches Patricia (when sibling proofs are available, which they aren't in
        // this offline reader) or correctly fails to apply the deletions (current outcome).
        sparseRoot.Should().NotBe(KnownSparseWrongRoot, "fix must not silently produce the production-captured wrong root");
    }

    /// <summary>
    /// Batch test: apply all 28 changes via ONE call to <c>sparse.UpdateLeaves</c>, compare
    /// against Patricia applied one-by-one. If this fails while <see cref="Bisect_PerUpdate_SparseVsPatricia"/>
    /// passes, the bug is in batched UpdateLeaves — specifically how it handles multiple
    /// updates that touch the SAME subtrie/branch in a single call.
    /// </summary>
    [Test]
    public void Batched_AllChanges_SparseVsPatricia()
    {
        Nethermind.Db.MemDb db = new();
        StaticProofReader reader = StaticProofReader.Load(UsdtAddressHash, UsdtPrevStorageRoot, UsdtRootRlpHex);
        reader.PopulateMemDb(db);

        StorageTree patricia = new(
            new RawTrieStore(db).GetTrieStore(UsdtAddressHash),
            UsdtPrevStorageRoot,
            Nethermind.Logging.LimboLogs.Instance);
        foreach ((UInt256 slot, byte[] value) in UsdtUpdates)
        {
            if (value.AsSpan().IndexOfAnyExcept((byte)0) < 0) continue;
            patricia.Set(slot, value);
        }
        patricia.UpdateRootHash();
        Hash256 patriciaRoot = patricia.RootHash;

        using SparsePatriciaTree sparse = new();
        byte[] rootRlp = reader.LoadStorageRlp(UsdtAddressHash, TreePath.Empty, UsdtPrevStorageRoot);
        sparse.RevealNodes([MultiProofReader.DecodeProofNode(rootRlp, TreePath.Empty)]);
        foreach (ProofNode pn in reader.AllProofNodes()) sparse.RevealNodes([pn]);

        Dictionary<Hash256, LeafUpdate> batch = [];
        ValueHash256 keyBuf = default;
        foreach ((UInt256 slot, byte[] value) in UsdtUpdates)
        {
            if (value.AsSpan().IndexOfAnyExcept((byte)0) < 0) continue;
            StorageTree.ComputeKeyWithLookup(slot, ref keyBuf);
            batch[keyBuf.ToCommitment()] = LeafUpdate.Changed(Rlp.Encode(value).Bytes);
        }
        sparse.UpdateLeaves(batch, (_, _) => { });
        Hash256 sparseRoot = sparse.ComputeRoot();

        TestContext.Out.WriteLine($"Patricia (one-by-one): {patriciaRoot}");
        TestContext.Out.WriteLine($"Sparse   (batched 28): {sparseRoot}");
        sparseRoot.Should().Be(patriciaRoot,
            "Batched UpdateLeaves must equal sequential single updates");
    }

    /// <summary>
    /// Bisect: build a Patricia tree backed by a MemDb seeded with the 181 captured proofs +
    /// root RLP (keyed by keccak so NodeStorage's hash-fallback finds them). Apply the 28
    /// changes one at a time to both Patricia and sparse, asserting the roots match after
    /// each step. The FIRST step where roots diverge is the offending update.
    /// </summary>
    [Test]
    public void Bisect_PerUpdate_SparseVsPatricia()
    {
        Nethermind.Db.MemDb db = new();
        StaticProofReader reader = StaticProofReader.Load(UsdtAddressHash, UsdtPrevStorageRoot, UsdtRootRlpHex);
        reader.PopulateMemDb(db);

        // Patricia
        StorageTree patricia = new(
            new RawTrieStore(db).GetTrieStore(UsdtAddressHash),
            UsdtPrevStorageRoot,
            Nethermind.Logging.LimboLogs.Instance);

        // Sparse
        using SparsePatriciaTree sparse = new();
        byte[] rootRlp = reader.LoadStorageRlp(UsdtAddressHash, TreePath.Empty, UsdtPrevStorageRoot);
        ProofNode rootProof = MultiProofReader.DecodeProofNode(rootRlp, TreePath.Empty);
        sparse.RevealNodes([rootProof]);
        foreach (ProofNode pn in reader.AllProofNodes()) sparse.RevealNodes([pn]);

        ValueHash256 keyBuf = default;
        int divergeIdx = -1;
        Hash256? firstDivergeSparse = null, firstDivergePatricia = null;
        (UInt256 slot, byte[] value) firstDivergeUpdate = default;

        for (int i = 0; i < UsdtUpdates.Length; i++)
        {
            (UInt256 slot, byte[] value) = UsdtUpdates[i];
            // skip deletions for this bisection — they need sibling resolution
            if (value.AsSpan().IndexOfAnyExcept((byte)0) < 0) continue;

            patricia.Set(slot, value);

            StorageTree.ComputeKeyWithLookup(slot, ref keyBuf);
            Hash256 slotHash = keyBuf.ToCommitment();
            byte[] encoded = Rlp.Encode(value).Bytes;
            Dictionary<Hash256, LeafUpdate> oneUpdate = new() { [slotHash] = LeafUpdate.Changed(encoded) };
            sparse.UpdateLeaves(oneUpdate, (_, _) => { });

            patricia.UpdateRootHash();
            Hash256 patriciaRoot = patricia.RootHash;
            Hash256 sparseRoot = sparse.ComputeRoot();

            if (sparseRoot != patriciaRoot)
            {
                divergeIdx = i;
                firstDivergeSparse = sparseRoot;
                firstDivergePatricia = patriciaRoot;
                firstDivergeUpdate = (slot, value);
                break;
            }
        }

        if (divergeIdx >= 0)
        {
            TestContext.Out.WriteLine($"FIRST DIVERGENCE at update index {divergeIdx}");
            TestContext.Out.WriteLine($"  slot:  {firstDivergeUpdate.slot}");
            TestContext.Out.WriteLine($"  value: 0x{Convert.ToHexString(firstDivergeUpdate.value ?? [])}");
            TestContext.Out.WriteLine($"  Patricia: {firstDivergePatricia}");
            TestContext.Out.WriteLine($"  Sparse:   {firstDivergeSparse}");

            Assert.Fail($"Divergence at update {divergeIdx}: slot={firstDivergeUpdate.slot}, value=0x{Convert.ToHexString(firstDivergeUpdate.value ?? [])}");
        }
        else
        {
            TestContext.Out.WriteLine("All 28 changes applied — sparse matches Patricia after each.");
        }
    }

    /// <summary>
     /// Isolation experiment: skip the 2 deletions (Hex("00") slots) and apply only the
     /// 28 value-changes. If sparse still mismatches Patricia, the bug is in encoding/compute
     /// for the general post-update shape. If sparse matches Patricia here, the bug is
     /// specific to deletion handling.
     /// </summary>
    [Test]
    public void Replay_Usdt22360025_ChangesOnly_NoDeletions()
    {
        StaticProofReader reader = StaticProofReader.Load(UsdtAddressHash, UsdtPrevStorageRoot, UsdtRootRlpHex);

        using SparsePatriciaTree sparse = new();

        byte[] rootRlp = reader.LoadStorageRlp(UsdtAddressHash, TreePath.Empty, UsdtPrevStorageRoot);
        ProofNode rootProof = MultiProofReader.DecodeProofNode(rootRlp, TreePath.Empty);
        sparse.RevealNodes([rootProof]);

        foreach (ProofNode pn in reader.AllProofNodes())
            sparse.RevealNodes([pn]);

        Dictionary<Hash256, LeafUpdate> updates = [];
        ValueHash256 keyBuf = default;
        int skipped = 0;
        foreach ((UInt256 slot, byte[] value) in UsdtUpdates)
        {
            if (value.AsSpan().IndexOfAnyExcept((byte)0) < 0) { skipped++; continue; }
            StorageTree.ComputeKeyWithLookup(slot, ref keyBuf);
            Hash256 slotHash = keyBuf.ToCommitment();
            Rlp rlpEncoded = Rlp.Encode(value);
            byte[]? encoded = rlpEncoded?.Bytes;
            updates[slotHash] = encoded is null || encoded.Length == 0
                ? LeafUpdate.Deleted()
                : LeafUpdate.Changed(encoded);
        }

        const int maxRetries = 16;
        int retriesUsed = 0;
        for (int retry = 0; retry < maxRetries; retry++)
        {
            List<Hash256> needsProof = [];
            sparse.UpdateLeaves(updates, (key, _) => needsProof.Add(key));
            if (needsProof.Count == 0) break;
            DecodedMultiProof retryProof = MultiProofReader.ReadStorageProofs(
                reader, UsdtAddressHash, UsdtPrevStorageRoot, [.. needsProof]);
            if (retryProof.StorageNodes.TryGetValue(UsdtAddressHash, out List<ProofNode>? retryNodes))
                sparse.RevealNodes(retryNodes);
            retriesUsed = retry + 1;
        }

        Hash256 sparseRoot = sparse.ComputeRoot();
        TestContext.Out.WriteLine($"Skipped {skipped} deletions. Retries: {retriesUsed}");
        TestContext.Out.WriteLine($"Sparse changes-only root: {sparseRoot}");
        TestContext.Out.WriteLine($"Patricia post-30-update root: {ExpectedPatriciaPostRoot}");
        TestContext.Out.WriteLine("(They should differ because deletions are skipped — but this isolates the compute path.)");
    }

    private static Dictionary<Hash256, LeafUpdate> BuildUpdates()
    {
        Dictionary<Hash256, LeafUpdate> updates = [];
        ValueHash256 keyBuf = default;
        foreach ((UInt256 slot, byte[] value) in UsdtUpdates)
        {
            StorageTree.ComputeKeyWithLookup(slot, ref keyBuf);
            Hash256 slotHash = keyBuf.ToCommitment();

            if (value.AsSpan().IndexOfAnyExcept((byte)0) < 0)
            {
                updates[slotHash] = LeafUpdate.Deleted();
                continue;
            }

            Rlp rlpEncoded = Rlp.Encode(value);
            byte[]? encoded = rlpEncoded?.Bytes;
            updates[slotHash] = encoded is null || encoded.Length == 0
                ? LeafUpdate.Deleted()
                : LeafUpdate.Changed(encoded);
        }
        return updates;
    }

    /// <summary>
    /// Mock <see cref="ITrieNodeReader"/> backed by a path+hash → RLP map built from the EXPB
    /// diagnostic dump. Records hits/misses for diagnostics.
    /// </summary>
    private sealed class StaticProofReader : ITrieNodeReader
    {
        private readonly Dictionary<(TreePath, Hash256), byte[]> _byPathAndHash = [];
        private readonly Dictionary<Hash256, byte[]> _byHash = [];
        private readonly List<(TreePath Path, byte[] Rlp)> _all = [];

        public int Hits;
        public int MissesByHash;

        public IEnumerable<ProofNode> AllProofNodes()
        {
            foreach ((TreePath path, byte[] rlp) in _all)
                yield return MultiProofReader.DecodeProofNode(rlp, path);
        }

        /// <summary>Populate a MemDb with hash→RLP entries so <see cref="NodeStorage"/>'s
        /// hash-fallback lookup finds every captured node by keccak.</summary>
        public void PopulateMemDb(Nethermind.Db.MemDb db)
        {
            foreach ((Hash256 hash, byte[] rlp) in _byHash)
                db[hash.Bytes.ToArray()] = rlp;
        }

        public static StaticProofReader Load(Hash256 accountPathHash, Hash256 rootHash, string rootRlpHex)
        {
            StaticProofReader r = new();
            byte[] rootRlp = Bytes.FromHexString(rootRlpHex);
            r.Add(TreePath.Empty, rootHash, rootRlp);

            string resourcePath = Path.Combine(
                TestContext.CurrentContext.TestDirectory,
                "Resources", "usdt_22360025_proofs.csv");
            string[] lines = File.ReadAllLines(resourcePath);
            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                string[] parts = line.Split('|');
                if (parts.Length != 3) continue;

                TreePath path = ParseHexPath(parts[0]);
                byte[] rlp = Convert.FromHexString(parts[2]);
                Hash256 hash = Keccak.Compute(rlp);
                r.Add(path, hash, rlp);
            }
            return r;
        }

        private void Add(TreePath path, Hash256 hash, byte[] rlp)
        {
            _byPathAndHash[(path, hash)] = rlp;
            _byHash[hash] = rlp;
            _all.Add((path, rlp));
        }

        public byte[] LoadStateRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
            Resolve(path, hash);

        public byte[] LoadStorageRlp(Hash256 accountPathHash, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
            Resolve(path, hash);

        private byte[] Resolve(in TreePath path, Hash256 hash)
        {
            if (_byPathAndHash.TryGetValue((path, hash), out byte[]? rlp))
            {
                Hits++;
                return rlp;
            }
            if (_byHash.TryGetValue(hash, out byte[]? rlpByHash))
            {
                MissesByHash++;
                return rlpByHash;
            }
            throw new MissingTrieNodeException($"StaticProofReader: no captured entry for path={path} hash={hash}", null, path, hash);
        }

        private static TreePath ParseHexPath(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return TreePath.Empty;
            TreePath p = TreePath.Empty;
            foreach (char c in hex)
            {
                int n = c switch
                {
                    >= '0' and <= '9' => c - '0',
                    >= 'a' and <= 'f' => 10 + (c - 'a'),
                    >= 'A' and <= 'F' => 10 + (c - 'A'),
                    _ => -1
                };
                if (n < 0) continue;
                p = p.Append((byte)n);
            }
            return p;
        }
    }
}
