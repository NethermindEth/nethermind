// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Xdc.Errors;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;
using System;

namespace Nethermind.Xdc.Test;

[TestFixture, Parallelizable(ParallelScope.All)]
public class SyncInfoManagerTests
{
    [Test]
    public void ProcessSyncInfo_WithValidSyncInfo_CallsBothManagersMethods()
    {
        var blockRoundInfo = new BlockRoundInfo(Keccak.Zero, 1, 1);
        var qc = new QuorumCertificate(blockRoundInfo, Array.Empty<Signature>(), 0);
        var tc = new TimeoutCertificate(1, Array.Empty<Signature>(), 0);
        var syncInfo = new SyncInfo(qc, tc);

        var xdcContext = Substitute.For<IXdcConsensusContext>();
        var qcManager = Substitute.For<IQuorumCertificateManager>();
        var timeoutManager = Substitute.For<ITimeoutCertificateManager>();
        var logManager = Substitute.For<ILogManager>();

        var manager = new SyncInfoManager(xdcContext, qcManager, timeoutManager, logManager);

        manager.ProcessSyncInfo(syncInfo);

        timeoutManager.Received(1).ProcessTimeoutCertificate(tc);
        qcManager.Received(1).CommitCertificate(qc);
    }

    [Test]
    public void VerifySyncInfo_WhenQuorumCertificateRoundIsEqualToPrevious_ReturnsFalseAndError()
    {
        var blockRoundInfo = new BlockRoundInfo(Keccak.Zero, 1, 1);
        var qc = new QuorumCertificate(blockRoundInfo, Array.Empty<Signature>(), 0);
        var tc = new TimeoutCertificate(1, Array.Empty<Signature>(), 0);
        var syncInfo = new SyncInfo(qc, tc);

        var xdcContext = Substitute.For<IXdcConsensusContext>();
        var contextQc = new QuorumCertificate(
            new BlockRoundInfo(Keccak.Zero, 1, 1),
            Array.Empty<Signature>(),
            0);
        xdcContext.HighestQC.Returns(contextQc);

        var qcManager = Substitute.For<IQuorumCertificateManager>();
        var timeoutManager = Substitute.For<ITimeoutCertificateManager>();
        var logManager = Substitute.For<ILogManager>();

        var manager = new SyncInfoManager(xdcContext, qcManager, timeoutManager, logManager);

        var result = manager.VerifySyncInfo(syncInfo, out var error);

        result.Should().BeFalse();
    }

    [Test]
    public void VerifySyncInfo_WhenQuorumCertificateRoundIsLowerThanPrevious_ReturnsFalseAndError()
    {
        var blockRoundInfo = new BlockRoundInfo(Keccak.Zero, 1, 1);
        var qc = new QuorumCertificate(blockRoundInfo, Array.Empty<Signature>(), 0);
        var tc = new TimeoutCertificate(1, Array.Empty<Signature>(), 0);
        var syncInfo = new SyncInfo(qc, tc);

        var xdcContext = Substitute.For<IXdcConsensusContext>();
        var contextQc = new QuorumCertificate(
            new BlockRoundInfo(Keccak.Zero, 5, 1),
            Array.Empty<Signature>(),
            0);
        xdcContext.HighestQC.Returns(contextQc);

        var qcManager = Substitute.For<IQuorumCertificateManager>();
        var timeoutManager = Substitute.For<ITimeoutCertificateManager>();
        var logManager = Substitute.For<ILogManager>();

        var manager = new SyncInfoManager(xdcContext, qcManager, timeoutManager, logManager);

        var result = manager.VerifySyncInfo(syncInfo, out var error);

        result.Should().BeFalse();
    }

    [Test]
    public void VerifySyncInfo_WhenTimeoutCertificateRoundIsEqualToPrevious_ReturnsFalseAndError()
    {
        var blockRoundInfo = new BlockRoundInfo(Keccak.Zero, 5, 1);
        var qc = new QuorumCertificate(blockRoundInfo, Array.Empty<Signature>(), 0);
        var tc = new TimeoutCertificate(2, Array.Empty<Signature>(), 0);
        var syncInfo = new SyncInfo(qc, tc);

        var xdcContext = Substitute.For<IXdcConsensusContext>();
        var contextQc = new QuorumCertificate(
            new BlockRoundInfo(Keccak.Zero, 1, 1),
            Array.Empty<Signature>(),
            0);
        var contextTc = new TimeoutCertificate(2, Array.Empty<Signature>(), 0);
        xdcContext.HighestQC.Returns(contextQc);
        xdcContext.HighestTC.Returns(contextTc);

        var qcManager = Substitute.For<IQuorumCertificateManager>();
        var timeoutManager = Substitute.For<ITimeoutCertificateManager>();
        var logManager = Substitute.For<ILogManager>();

        var manager = new SyncInfoManager(xdcContext, qcManager, timeoutManager, logManager);

        var result = manager.VerifySyncInfo(syncInfo, out var error);

        result.Should().BeFalse();
    }

    [Test]
    public void VerifySyncInfo_WhenTimeoutCertificateRoundIsLowerThanPrevious_ReturnsFalseAndError()
    {
        var blockRoundInfo = new BlockRoundInfo(Keccak.Zero, 5, 1);
        var qc = new QuorumCertificate(blockRoundInfo, Array.Empty<Signature>(), 0);
        var tc = new TimeoutCertificate(1, Array.Empty<Signature>(), 0);
        var syncInfo = new SyncInfo(qc, tc);

        var xdcContext = Substitute.For<IXdcConsensusContext>();
        var contextQc = new QuorumCertificate(
            new BlockRoundInfo(Keccak.Zero, 1, 1),
            Array.Empty<Signature>(),
            0);
        var contextTc = new TimeoutCertificate(2, Array.Empty<Signature>(), 0);
        xdcContext.HighestQC.Returns(contextQc);
        xdcContext.HighestTC.Returns(contextTc);

        var qcManager = Substitute.For<IQuorumCertificateManager>();
        var timeoutManager = Substitute.For<ITimeoutCertificateManager>();
        var logManager = Substitute.For<ILogManager>();

        var manager = new SyncInfoManager(xdcContext, qcManager, timeoutManager, logManager);

        var result = manager.VerifySyncInfo(syncInfo, out var error);

        result.Should().BeFalse();
    }

    [Test]
    public void VerifySyncInfo_WhenAllVerificationsPass_ReturnsTrue()
    {
        var blockRoundInfo = new BlockRoundInfo(Keccak.Zero, 5, 1);
        var qc = new QuorumCertificate(blockRoundInfo, Array.Empty<Signature>(), 0);
        var tc = new TimeoutCertificate(5, Array.Empty<Signature>(), 0);
        var syncInfo = new SyncInfo(qc, tc);

        var xdcContext = Substitute.For<IXdcConsensusContext>();
        var contextQc = new QuorumCertificate(
            new BlockRoundInfo(Keccak.Zero, 1, 1),
            Array.Empty<Signature>(),
            0);
        xdcContext.HighestQC.Returns(contextQc);

        var qcManager = Substitute.For<IQuorumCertificateManager>();
        var timeoutManager = Substitute.For<ITimeoutCertificateManager>();
        var logManager = Substitute.For<ILogManager>();

        qcManager.VerifyCertificate(qc, out Arg.Any<string>()).Returns(true);
        timeoutManager.VerifyTimeoutCertificate(tc, out Arg.Any<string>()).Returns(true);

        var manager = new SyncInfoManager(xdcContext, qcManager, timeoutManager, logManager);

        var result = manager.VerifySyncInfo(syncInfo, out _);

        result.Should().BeTrue();
    }

    [Test]
    public void VerifySyncInfo_WhenBothCertificatesAreHigher_ReturnsTrue()
    {
        var blockRoundInfo = new BlockRoundInfo(Keccak.Zero, 10, 1);
        var qc = new QuorumCertificate(blockRoundInfo, Array.Empty<Signature>(), 0);
        var tc = new TimeoutCertificate(10, Array.Empty<Signature>(), 0);
        var syncInfo = new SyncInfo(qc, tc);

        var xdcContext = Substitute.For<IXdcConsensusContext>();
        var contextQc = new QuorumCertificate(
            new BlockRoundInfo(Keccak.Zero, 5, 1),
            Array.Empty<Signature>(),
            0);
        var contextTc = new TimeoutCertificate(5, Array.Empty<Signature>(), 0);
        xdcContext.HighestQC.Returns(contextQc);
        xdcContext.HighestTC.Returns(contextTc);

        var qcManager = Substitute.For<IQuorumCertificateManager>();
        var timeoutManager = Substitute.For<ITimeoutCertificateManager>();
        var logManager = Substitute.For<ILogManager>();

        qcManager.VerifyCertificate(qc, out Arg.Any<string>()).Returns(true);
        timeoutManager.VerifyTimeoutCertificate(tc, out Arg.Any<string>()).Returns(true);

        var manager = new SyncInfoManager(xdcContext, qcManager, timeoutManager, logManager);

        var result = manager.VerifySyncInfo(syncInfo, out _);

        result.Should().BeTrue();
    }

    [Test]
    public void VerifySyncInfo_WhenOnlyQuorumCertificateIsHigher_ReturnsTrue()
    {
        var blockRoundInfo = new BlockRoundInfo(Keccak.Zero, 10, 1);
        var qc = new QuorumCertificate(blockRoundInfo, Array.Empty<Signature>(), 0);
        var tc = new TimeoutCertificate(5, Array.Empty<Signature>(), 0);
        var syncInfo = new SyncInfo(qc, tc);

        var xdcContext = Substitute.For<IXdcConsensusContext>();
        var contextQc = new QuorumCertificate(
            new BlockRoundInfo(Keccak.Zero, 5, 1),
            Array.Empty<Signature>(),
            0);
        var contextTc = new TimeoutCertificate(5, Array.Empty<Signature>(), 0);
        xdcContext.HighestQC.Returns(contextQc);
        xdcContext.HighestTC.Returns(contextTc);

        var qcManager = Substitute.For<IQuorumCertificateManager>();
        var timeoutManager = Substitute.For<ITimeoutCertificateManager>();
        var logManager = Substitute.For<ILogManager>();

        qcManager.VerifyCertificate(qc, out Arg.Any<string>()).Returns(true);
        timeoutManager.VerifyTimeoutCertificate(tc, out Arg.Any<string>()).Returns(true);

        var manager = new SyncInfoManager(xdcContext, qcManager, timeoutManager, logManager);

        var result = manager.VerifySyncInfo(syncInfo, out _);

        result.Should().BeTrue();
    }

    [Test]
    public void VerifySyncInfo_WhenOnlyTimeoutCertificateIsHigher_ReturnsTrue()
    {
        var blockRoundInfo = new BlockRoundInfo(Keccak.Zero, 5, 1);
        var qc = new QuorumCertificate(blockRoundInfo, Array.Empty<Signature>(), 0);
        var tc = new TimeoutCertificate(10, Array.Empty<Signature>(), 0);
        var syncInfo = new SyncInfo(qc, tc);

        var xdcContext = Substitute.For<IXdcConsensusContext>();
        var contextQc = new QuorumCertificate(
            new BlockRoundInfo(Keccak.Zero, 5, 1),
            Array.Empty<Signature>(),
            0);
        var contextTc = new TimeoutCertificate(5, Array.Empty<Signature>(), 0);
        xdcContext.HighestQC.Returns(contextQc);
        xdcContext.HighestTC.Returns(contextTc);

        var qcManager = Substitute.For<IQuorumCertificateManager>();
        var timeoutManager = Substitute.For<ITimeoutCertificateManager>();
        var logManager = Substitute.For<ILogManager>();

        qcManager.VerifyCertificate(qc, out Arg.Any<string>()).Returns(true);
        timeoutManager.VerifyTimeoutCertificate(tc, out Arg.Any<string>()).Returns(true);

        var manager = new SyncInfoManager(xdcContext, qcManager, timeoutManager, logManager);

        var result = manager.VerifySyncInfo(syncInfo, out _);

        result.Should().BeTrue();
    }
}
