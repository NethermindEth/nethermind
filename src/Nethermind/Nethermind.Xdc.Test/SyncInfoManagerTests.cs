// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Xdc.Errors;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

[TestFixture, Parallelizable(ParallelScope.All)]
public class SyncInfoManagerTests
{
    private const ulong KnownRound = 5;

    // A stale, invalid or missing certificate is skipped on its own - a null round stands for the
    // certificate a peer leaves out, which both RLP decoders return as null for an empty list.
    [TestCase(10ul, true, true)]
    [TestCase(KnownRound, true, false)]
    [TestCase(1ul, true, false)]
    [TestCase(10ul, false, false)]
    [TestCase(null, true, false)]
    public void ProcessQuorumCertificate_CommitsOnlyAFresherValidCertificate(ulong? round, bool isValid, bool expectCommitted)
    {
        QuorumCertificate qc = round is null ? null! : Qc(round.Value);
        IQuorumCertificateManager qcManager = Substitute.For<IQuorumCertificateManager>();
        qcManager.VerifyCertificate(qc, out Arg.Any<string>()).Returns(isValid);

        string? error = CreateManager(qcManager, Substitute.For<ITimeoutCertificateManager>()).ProcessQuorumCertificate(qc);

        qcManager.Received(expectCommitted ? 1 : 0).CommitCertificate(qc);
        Assert.That(error is null, Is.EqualTo(expectCommitted), $"unexpected error: {error}");
    }

    /// <inheritdoc cref="ProcessQuorumCertificate_CommitsOnlyAFresherValidCertificate"/>
    [TestCase(10ul, true, true)]
    [TestCase(KnownRound, true, false)]
    [TestCase(1ul, true, false)]
    [TestCase(10ul, false, false)]
    [TestCase(null, true, false)]
    public void ProcessTimeoutCertificate_AppliesOnlyAFresherValidCertificate(ulong? round, bool isValid, bool expectApplied)
    {
        TimeoutCertificate tc = round is null ? null! : Tc(round.Value);
        ITimeoutCertificateManager timeoutManager = Substitute.For<ITimeoutCertificateManager>();
        timeoutManager.VerifyTimeoutCertificate(tc, out Arg.Any<string?>()).Returns(isValid);

        string? error = CreateManager(Substitute.For<IQuorumCertificateManager>(), timeoutManager).ProcessTimeoutCertificate(tc);

        timeoutManager.Received(expectApplied ? 1 : 0).ProcessTimeoutCertificate(tc);
        Assert.That(error is null, Is.EqualTo(expectApplied), $"unexpected error: {error}");
    }

    [Test]
    public void ProcessQuorumCertificate_WhenTargetBlockIsUnknown_IsReportedInsteadOfThrowing()
    {
        QuorumCertificate qc = Qc(10);
        IQuorumCertificateManager qcManager = Substitute.For<IQuorumCertificateManager>();
        qcManager.VerifyCertificate(qc, out Arg.Any<string>()).Returns(true);
        qcManager.When(m => m.CommitCertificate(qc)).Throw(new IncomingMessageBlockNotFoundException(Keccak.Zero, 10));

        SyncInfoManager manager = CreateManager(qcManager, Substitute.For<ITimeoutCertificateManager>());

        Assert.That(manager.ProcessQuorumCertificate(qc), Is.Not.Null);
    }

    private static SyncInfoManager CreateManager(IQuorumCertificateManager qcManager, ITimeoutCertificateManager timeoutManager)
    {
        IXdcConsensusContext xdcContext = Substitute.For<IXdcConsensusContext>();
        xdcContext.HighestQC.Returns(Qc(KnownRound));
        xdcContext.HighestTC.Returns(Tc(KnownRound));
        return new SyncInfoManager(xdcContext, qcManager, timeoutManager);
    }

    private static QuorumCertificate Qc(ulong round) => new(new BlockRoundInfo(Keccak.Zero, round, 1), [], 0);

    private static TimeoutCertificate Tc(ulong round) => new(round, [], 0);
}
