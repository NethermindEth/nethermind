// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.Merge.Plugin.SszRest;

namespace Nethermind.BeaconChain.Test.DataAvailability;

public class DataColumnSidecarGloasVerifierTests
{
    private const ulong Column = 5;

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
            new TestCaseData(tamper, false, false).SetArgDisplayNames(name, "invalid structure", "invalid KZG");
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
            new TestCaseData(tamper, true, false).SetArgDisplayNames(name, "valid structure", "invalid KZG");
    }

    [TestCase(null, true, true, TestName = "A_valid_sidecar_passes_every_check")]
    [TestCaseSource(nameof(StructuralDefects))]
    [TestCaseSource(nameof(CryptographicDefects))]
    public void Structure_and_kzg_checks_reject_their_respective_defects(
        Func<DataColumnSidecarGloas, SszKzgCommitment[], SszKzgCommitment[]>? tamper, bool expectedStructure, bool expectedKzg)
    {
        DataColumnSidecarGloas sidecar = DataColumnSidecarGloasTestFixture.BuildSidecar(Column);
        SszKzgCommitment[] commitments = DataColumnSidecarGloasTestFixture.Commitments();
        if (tamper is not null) commitments = tamper(sidecar, commitments);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(DataColumnSidecarVerifier.VerifyStructure(sidecar, commitments), Is.EqualTo(expectedStructure),
            expectedStructure && !expectedKzg ? "the defect must be one only KZG can see" : null);
        Assert.That(DataColumnSidecarVerifier.VerifyKzgProofs(sidecar, commitments), Is.EqualTo(expectedKzg),
            expectedStructure ? null : "the KZG check must not run on arrays it indexes in lockstep, nor pass an empty batch");
    }
}
