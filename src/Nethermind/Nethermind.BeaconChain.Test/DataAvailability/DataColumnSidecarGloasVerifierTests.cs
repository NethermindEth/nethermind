// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.Merge.Plugin.SszRest;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.DataAvailability;

/// <summary>
/// Gloas <c>verify_data_column_sidecar</c> and <c>verify_data_column_sidecar_kzg_proofs</c>: the
/// commitments are the bid's, not the sidecar's, so every check is made against what the caller
/// passes in.
/// </summary>
public class DataColumnSidecarGloasVerifierTests
{
    private const ulong Column = 5;

    [Test]
    public void A_valid_sidecar_passes_every_check()
    {
        DataColumnSidecarGloas sidecar = DataColumnSidecarGloasTestFixture.BuildSidecar(Column);
        SszKzgCommitment[] commitments = DataColumnSidecarGloasTestFixture.Commitments();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(DataColumnSidecarVerifier.VerifyStructure(sidecar, commitments), Is.True);
            Assert.That(DataColumnSidecarVerifier.VerifyKzgProofs(sidecar, commitments), Is.True);
        }
    }

    private static IEnumerable<TestCaseData> StructuralDefects()
    {
        yield return Case("index at NUMBER_OF_COLUMNS", (s, c) => { s.Index = Eip7594DasConstants.NumberOfColumns; return c; });
        yield return Case("zero blobs, even against zero commitments", (s, _) => { s.Column = []; s.KzgProofs = []; return []; });
        yield return Case("no column", (s, c) => { s.Column = null; return c; });
        yield return Case("no proofs", (s, c) => { s.KzgProofs = null; return c; });
        yield return Case("fewer cells than bid commitments", (s, c) => { s.Column = [s.Column![0]]; s.KzgProofs = [s.KzgProofs![0]]; return c; });
        yield return Case("more bid commitments than cells", (_, c) => [.. c, c[0]]);
        yield return Case("fewer proofs than cells", (s, c) => { s.KzgProofs = [s.KzgProofs![0]]; return c; });

        static TestCaseData Case(string name, Func<DataColumnSidecarGloas, SszKzgCommitment[], SszKzgCommitment[]> tamper) =>
            new TestCaseData(tamper).SetArgDisplayNames(name);
    }

    [TestCaseSource(nameof(StructuralDefects))]
    public void A_structural_defect_fails_every_check(Func<DataColumnSidecarGloas, SszKzgCommitment[], SszKzgCommitment[]> tamper)
    {
        DataColumnSidecarGloas sidecar = DataColumnSidecarGloasTestFixture.BuildSidecar(Column);
        SszKzgCommitment[] commitments = tamper(sidecar, DataColumnSidecarGloasTestFixture.Commitments());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(DataColumnSidecarVerifier.VerifyStructure(sidecar, commitments), Is.False);
            Assert.That(DataColumnSidecarVerifier.VerifyKzgProofs(sidecar, commitments), Is.False, "the KZG check must not run on arrays it indexes in lockstep, nor pass an empty batch");
        }
    }

    private static IEnumerable<TestCaseData> CryptographicDefects()
    {
        yield return Case("a flipped cell bit", (s, c) =>
        {
            byte[] cell = s.Column![0].AsSpan().ToArray();
            cell[^1] ^= 0x01;
            s.Column[0] = SszBlobCell.FromSpan(cell);
            return c;
        });
        yield return Case("the bid commits the blobs in the other order", (_, c) => [c[1], c[0]]);
        yield return Case("the cells of one column claimed as another", (s, c) => { s.Index = Column + 1; return c; });

        static TestCaseData Case(string name, Func<DataColumnSidecarGloas, SszKzgCommitment[], SszKzgCommitment[]> tamper) =>
            new TestCaseData(tamper).SetArgDisplayNames(name);
    }

    [TestCaseSource(nameof(CryptographicDefects))]
    public void A_structurally_sound_sidecar_that_does_not_open_the_bid_commitments_fails(Func<DataColumnSidecarGloas, SszKzgCommitment[], SszKzgCommitment[]> tamper)
    {
        DataColumnSidecarGloas sidecar = DataColumnSidecarGloasTestFixture.BuildSidecar(Column);
        SszKzgCommitment[] commitments = tamper(sidecar, DataColumnSidecarGloasTestFixture.Commitments());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(DataColumnSidecarVerifier.VerifyStructure(sidecar, commitments), Is.True, "the defect must be one only KZG can see");
            Assert.That(DataColumnSidecarVerifier.VerifyKzgProofs(sidecar, commitments), Is.False);
        }
    }
}
