// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.IO;
using CkzgLib;
using Ethereum.Ssz.Test;
using Ethereum.Test.Base;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using YamlDotNet.RepresentationModel;

namespace Ethereum.ConsensusSpec.Test;

public class GeneralCryptoTests
{
    private static readonly string[] KzgHandlers = ["compute_cells", "compute_cells_and_kzg_proofs", "recover_cells_and_kzg_proofs", "verify_cell_kzg_proof_batch"];

    private static string Root(bool kzg) => TestFixtureDownloader.EnsureDownloaded("ConsensusGeneral", SszConsensusTestLoader.ArchiveUrlTemplate,
        kzg ? "v1.6.1" : ConsensusSpecArchive.Version, "general.tar.gz",
        path => path.StartsWith(kzg ? "tests/general/fulu/kzg/" : "tests/general/altair/bls/", StringComparison.Ordinal),
        kzg ? "fulu-kzg" : "altair-bls", kzg ? "87898ba7a3fa16bc66b08c88b4ccab008833a517a7602cf421c6ce79734e07e9" : "c4f395f71f606c28093500e32067990e430cc73a2277be6f3bf5090adcce3f67");

    private static IEnumerable<TestCaseData> Cases(bool kzg)
    {
        foreach (string path in Directory.EnumerateFiles(Root(kzg), "data.yaml", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string handler = Directory.GetParent(path)!.Parent!.Parent!.Name;
            if (kzg && !KzgHandlers.Contains(handler)) continue;
            yield return new TestCaseData(handler, path).SetName($"{(kzg ? "KZG" : "BLS")}/{handler}/{Directory.GetParent(path)!.Name}");
        }
    }
    private static IEnumerable<TestCaseData> BlsCases() => Cases(false);
    private static IEnumerable<TestCaseData> KzgCases() => Cases(true);

    [Test]
    public void Every_crypto_handler_has_its_pinned_vectors()
    {
        Assert.That(BlsCases().Count(), Is.EqualTo(20));
        Assert.That(KzgCases().GroupBy(test => test.Arguments[0]).ToDictionary(group => (string)group.Key!, group => group.Count()),
            Is.EquivalentTo(new Dictionary<string, int> { ["compute_cells"] = 11, ["compute_cells_and_kzg_proofs"] = 11, ["recover_cells_and_kzg_proofs"] = 18, ["verify_cell_kzg_proof_batch"] = 32 }));
    }

    [TestCaseSource(nameof(BlsCases))]
    public void Bls_vector(string handler, string path)
    {
        YamlMappingNode vector = FuluDriverSupport.LoadMapping(path);
        YamlNode input = vector.Children[new YamlScalarNode("input")];
        YamlNode output = vector.Children[new YamlScalarNode("output")];
        YamlSequenceNode keys = (YamlSequenceNode)(handler == "eth_aggregate_pubkeys" ? input : Field(input, "pubkeys"));
        Bls.P1 aggregate = new(stackalloc long[Bls.P1.Sz]);
        Bls.P1Affine key = new(stackalloc long[Bls.P1Affine.Sz]);
        bool validKeys = true;
        foreach (YamlNode encoded in keys)
        {
            if (!BlsSignatureSet.TryKeyValidate(Hex(encoded), key)) { validKeys = false; break; }
            aggregate.Add(key);
        }
        if (handler == "eth_aggregate_pubkeys")
        {
            byte[]? actual = validKeys && keys.Children.Count > 0 ? aggregate.ToAffine().Compress() : null;
            Assert.That(actual, Is.EqualTo(IsNull(output) ? null : Hex(output)));
            return;
        }
        Assert.That(handler, Is.EqualTo("eth_fast_aggregate_verify"));
        BlsSignature signature = new(Hex(Field(input, "signature")));
        bool expected = bool.Parse(((YamlScalarNode)output).Value!);
        if (keys.Children.Count == 0)
        {
            BeaconStateFulu state = new() { CurrentSyncCommittee = new() { Pubkeys = new BlsPublicKey[Presets.SyncCommitteeSize] } };
            Assert.That(SignatureSets.VerifySyncAggregate(state, new SyncAggregate { SyncCommitteeBits = new BitArray(Presets.SyncCommitteeSize), SyncCommitteeSignature = signature },
                new int[Presets.SyncCommitteeSize], new PubkeyCache()), Is.EqualTo(expected));
            return;
        }
        Hash256 message = new(Hex(Field(input, "message")));
        Assert.That(validKeys && BlockSignatureBatch.Verify(aggregate.ToAffine(), signature, message, null), Is.EqualTo(expected));
        if (validKeys)
        {
            bool batchValid;
            try
            {
                BlockSignatureBatch batch = new();
                if (!BlockSignatureBatch.Verify(aggregate.ToAffine(), signature, message, batch.Defer("invalid vector signature")))
                    throw new BeaconStateException("invalid vector signature");
                batch.Verify();
                batchValid = true;
            }
            catch (BeaconStateException) { batchValid = false; }
            Assert.That(batchValid, Is.EqualTo(expected));
        }
    }

    [TestCaseSource(nameof(KzgCases))]
    public void Kzg_vector(string handler, string path)
    {
        DasKzg.EnsureInitialized();
        YamlMappingNode vector = FuluDriverSupport.LoadMapping(path);
        YamlNode input = Field(vector, "input");
        YamlNode output = Field(vector, "output");
        object? actual;
        try { actual = RunKzg(handler, input); }
        catch (Exception e) when (e is ArgumentException or ApplicationException) { actual = null; }
        object? expected = IsNull(output) ? null : handler == "verify_cell_kzg_proof_batch" ? bool.Parse(((YamlScalarNode)output).Value!)
            : handler == "compute_cells" ? Flatten(output)
            : new[] { Flatten(((YamlSequenceNode)output).Children[0]), Flatten(((YamlSequenceNode)output).Children[1]) };
        Assert.That(actual, Is.EqualTo(expected));
    }

    private static object RunKzg(string handler, YamlNode input)
    {
        if (handler == "verify_cell_kzg_proof_batch")
        {
            ulong[] indices = Indices(input);
            return KzgPolynomialCommitments.VerifyCellProofBatch(Flatten(Field(input, "commitments")), indices,
                Flatten(Field(input, "cells")), Flatten(Field(input, "proofs")), indices.Length);
        }
        byte[] cells = new byte[Ckzg.CellsPerExtBlob * Ckzg.BytesPerCell];
        byte[] proofs = new byte[Ckzg.CellsPerExtBlob * Ckzg.BytesPerProof];
        if (handler == "compute_cells")
        {
            KzgPolynomialCommitments.ComputeCells(Hex(Field(input, "blob")), cells);
            return cells;
        }
        if (handler == "compute_cells_and_kzg_proofs")
        {
            byte[] blob = Hex(Field(input, "blob"));
            KzgPolynomialCommitments.ComputeCells(blob, cells);
            KzgPolynomialCommitments.ComputeCellProofs(blob, proofs);
        }
        else if (handler == "recover_cells_and_kzg_proofs")
        {
            ulong[] indices = Indices(input);
            KzgPolynomialCommitments.RecoverCellsAndProofs(cells, proofs, indices, Flatten(Field(input, "cells")), indices.Length);
        }
        else throw new InvalidOperationException($"Unknown KZG handler {handler}");
        return new[] { cells, proofs };
    }

    private static YamlNode Field(YamlNode node, string name) => ((YamlMappingNode)node).Children[new YamlScalarNode(name)];
    private static bool IsNull(YamlNode node) => node is YamlScalarNode { Value: null or "null" };
    private static byte[] Hex(YamlNode node) => Bytes.FromHexString(((YamlScalarNode)node).Value!);
    private static byte[] Flatten(YamlNode node) => ((YamlSequenceNode)node).Children.SelectMany(Hex).ToArray();
    private static ulong[] Indices(YamlNode node) => ((YamlSequenceNode)Field(node, "cell_indices")).Children.Select(item => ulong.Parse(((YamlScalarNode)item).Value!)).ToArray();
}
