// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
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
        BlockRoundInfo blockRoundInfo = new(Keccak.Zero, 1, 1);
        QuorumCertificate qc = new(blockRoundInfo, Array.Empty<Signature>(), 0);
        TimeoutCertificate tc = new(1, Array.Empty<Signature>(), 0);
        SyncInfo syncInfo = new(qc, tc);

        IXdcConsensusContext xdcContext = Substitute.For<IXdcConsensusContext>();
        IQuorumCertificateManager qcManager = Substitute.For<IQuorumCertificateManager>();
        ITimeoutCertificateManager timeoutManager = Substitute.For<ITimeoutCertificateManager>();
        ILogManager logManager = Substitute.For<ILogManager>();

        SyncInfoManager manager = new(xdcContext, qcManager, timeoutManager, logManager);

        manager.ProcessSyncInfo(syncInfo);

        timeoutManager.Received(1).ProcessTimeoutCertificate(tc);
        qcManager.Received(1).CommitCertificate(qc);
    }

    [Test]
    public void VerifySyncInfo_WhenQuorumCertificateRoundIsEqualToPrevious_ReturnsFalseAndError()
    {
        BlockRoundInfo blockRoundInfo = new(Keccak.Zero, 1, 1);
        QuorumCertificate qc = new(blockRoundInfo, Array.Empty<Signature>(), 0);
        TimeoutCertificate tc = new(1, Array.Empty<Signature>(), 0);
        SyncInfo syncInfo = new(qc, tc);

        IXdcConsensusContext xdcContext = Substitute.For<IXdcConsensusContext>();
        QuorumCertificate contextQc = new(
            new BlockRoundInfo(Keccak.Zero, 1, 1),
            Array.Empty<Signature>(),
            0);
        xdcContext.HighestQC.Returns(contextQc);

        IQuorumCertificateManager qcManager = Substitute.For<IQuorumCertificateManager>();
        ITimeoutCertificateManager timeoutManager = Substitute.For<ITimeoutCertificateManager>();
        ILogManager logManager = Substitute.For<ILogManager>();

        SyncInfoManager manager = new(xdcContext, qcManager, timeoutManager, logManager);

        bool result = manager.VerifySyncInfo(syncInfo, out string? error);

        result.Should().BeFalse();
    }

    [Test]
    public void VerifySyncInfo_WhenQuorumCertificateRoundIsLowerThanPrevious_ReturnsFalseAndError()
    {
        BlockRoundInfo blockRoundInfo = new(Keccak.Zero, 1, 1);
        QuorumCertificate qc = new(blockRoundInfo, Array.Empty<Signature>(), 0);
        TimeoutCertificate tc = new(1, Array.Empty<Signature>(), 0);
        SyncInfo syncInfo = new(qc, tc);

        IXdcConsensusContext xdcContext = Substitute.For<IXdcConsensusContext>();
        QuorumCertificate contextQc = new(
            new BlockRoundInfo(Keccak.Zero, 5, 1),
            Array.Empty<Signature>(),
            0);
        xdcContext.HighestQC.Returns(contextQc);

        IQuorumCertificateManager qcManager = Substitute.For<IQuorumCertificateManager>();
        ITimeoutCertificateManager timeoutManager = Substitute.For<ITimeoutCertificateManager>();
        ILogManager logManager = Substitute.For<ILogManager>();

        SyncInfoManager manager = new(xdcContext, qcManager, timeoutManager, logManager);

        bool result = manager.VerifySyncInfo(syncInfo, out string? error);

        result.Should().BeFalse();
    }

    [Test]
    public void VerifySyncInfo_WhenTimeoutCertificateRoundIsEqualToPrevious_ReturnsFalseAndError()
    {
        BlockRoundInfo blockRoundInfo = new(Keccak.Zero, 5, 1);
        QuorumCertificate qc = new(blockRoundInfo, Array.Empty<Signature>(), 0);
        TimeoutCertificate tc = new(2, Array.Empty<Signature>(), 0);
        SyncInfo syncInfo = new(qc, tc);

        IXdcConsensusContext xdcContext = Substitute.For<IXdcConsensusContext>();
        QuorumCertificate contextQc = new(
            new BlockRoundInfo(Keccak.Zero, 1, 1),
            Array.Empty<Signature>(),
            0);
        TimeoutCertificate contextTc = new(2, Array.Empty<Signature>(), 0);
        xdcContext.HighestQC.Returns(contextQc);
        xdcContext.HighestTC.Returns(contextTc);

        IQuorumCertificateManager qcManager = Substitute.For<IQuorumCertificateManager>();
        ITimeoutCertificateManager timeoutManager = Substitute.For<ITimeoutCertificateManager>();
        ILogManager logManager = Substitute.For<ILogManager>();

        SyncInfoManager manager = new(xdcContext, qcManager, timeoutManager, logManager);

        bool result = manager.VerifySyncInfo(syncInfo, out string? error);

        result.Should().BeFalse();
    }

    [Test]
    public void VerifySyncInfo_WhenTimeoutCertificateRoundIsLowerThanPrevious_ReturnsFalseAndError()
    {
        BlockRoundInfo blockRoundInfo = new(Keccak.Zero, 5, 1);
        QuorumCertificate qc = new(blockRoundInfo, Array.Empty<Signature>(), 0);
        TimeoutCertificate tc = new(1, Array.Empty<Signature>(), 0);
        SyncInfo syncInfo = new(qc, tc);

        IXdcConsensusContext xdcContext = Substitute.For<IXdcConsensusContext>();
        QuorumCertificate contextQc = new(
            new BlockRoundInfo(Keccak.Zero, 1, 1),
            Array.Empty<Signature>(),
            0);
        TimeoutCertificate contextTc = new(2, Array.Empty<Signature>(), 0);
        xdcContext.HighestQC.Returns(contextQc);
        xdcContext.HighestTC.Returns(contextTc);

        IQuorumCertificateManager qcManager = Substitute.For<IQuorumCertificateManager>();
        ITimeoutCertificateManager timeoutManager = Substitute.For<ITimeoutCertificateManager>();
        ILogManager logManager = Substitute.For<ILogManager>();

        SyncInfoManager manager = new(xdcContext, qcManager, timeoutManager, logManager);

        bool result = manager.VerifySyncInfo(syncInfo, out string? error);

        result.Should().BeFalse();
    }

    [Test]
    public void VerifySyncInfo_WhenAllVerificationsPass_ReturnsTrue()
    {
        BlockRoundInfo blockRoundInfo = new(Keccak.Zero, 5, 1);
        QuorumCertificate qc = new(blockRoundInfo, Array.Empty<Signature>(), 0);
        TimeoutCertificate tc = new(5, Array.Empty<Signature>(), 0);
        SyncInfo syncInfo = new(qc, tc);

        IXdcConsensusContext xdcContext = Substitute.For<IXdcConsensusContext>();
        QuorumCertificate contextQc = new(
            new BlockRoundInfo(Keccak.Zero, 1, 1),
            Array.Empty<Signature>(),
            0);
        xdcContext.HighestQC.Returns(contextQc);

        IQuorumCertificateManager qcManager = Substitute.For<IQuorumCertificateManager>();
        ITimeoutCertificateManager timeoutManager = Substitute.For<ITimeoutCertificateManager>();
        ILogManager logManager = Substitute.For<ILogManager>();

        qcManager.VerifyCertificate(qc, out Arg.Any<string>()).Returns(true);
        timeoutManager.VerifyTimeoutCertificate(tc, out Arg.Any<string?>()).Returns(true);

        SyncInfoManager manager = new(xdcContext, qcManager, timeoutManager, logManager);

        bool result = manager.VerifySyncInfo(syncInfo, out _);

        result.Should().BeTrue();
    }

    [Test]
    public void VerifySyncInfo_WhenBothCertificatesAreHigher_ReturnsTrue()
    {
        BlockRoundInfo blockRoundInfo = new(Keccak.Zero, 10, 1);
        QuorumCertificate qc = new(blockRoundInfo, Array.Empty<Signature>(), 0);
        TimeoutCertificate tc = new(10, Array.Empty<Signature>(), 0);
        SyncInfo syncInfo = new(qc, tc);

        IXdcConsensusContext xdcContext = Substitute.For<IXdcConsensusContext>();
        QuorumCertificate contextQc = new(
            new BlockRoundInfo(Keccak.Zero, 5, 1),
            Array.Empty<Signature>(),
            0);
        TimeoutCertificate contextTc = new(5, Array.Empty<Signature>(), 0);
        xdcContext.HighestQC.Returns(contextQc);
        xdcContext.HighestTC.Returns(contextTc);

        IQuorumCertificateManager qcManager = Substitute.For<IQuorumCertificateManager>();
        ITimeoutCertificateManager timeoutManager = Substitute.For<ITimeoutCertificateManager>();
        ILogManager logManager = Substitute.For<ILogManager>();

        qcManager.VerifyCertificate(qc, out Arg.Any<string>()).Returns(true);
        timeoutManager.VerifyTimeoutCertificate(tc, out Arg.Any<string?>()).Returns(true);

        SyncInfoManager manager = new(xdcContext, qcManager, timeoutManager, logManager);

        bool result = manager.VerifySyncInfo(syncInfo, out _);

        result.Should().BeTrue();
    }

    [Test]
    public void VerifySyncInfo_WhenOnlyQuorumCertificateIsHigher_ReturnsTrue()
    {
        BlockRoundInfo blockRoundInfo = new(Keccak.Zero, 10, 1);
        QuorumCertificate qc = new(blockRoundInfo, Array.Empty<Signature>(), 0);
        TimeoutCertificate tc = new(5, Array.Empty<Signature>(), 0);
        SyncInfo syncInfo = new(qc, tc);

        IXdcConsensusContext xdcContext = Substitute.For<IXdcConsensusContext>();
        QuorumCertificate contextQc = new(
            new BlockRoundInfo(Keccak.Zero, 5, 1),
            Array.Empty<Signature>(),
            0);
        TimeoutCertificate contextTc = new(5, Array.Empty<Signature>(), 0);
        xdcContext.HighestQC.Returns(contextQc);
        xdcContext.HighestTC.Returns(contextTc);

        IQuorumCertificateManager qcManager = Substitute.For<IQuorumCertificateManager>();
        ITimeoutCertificateManager timeoutManager = Substitute.For<ITimeoutCertificateManager>();
        ILogManager logManager = Substitute.For<ILogManager>();

        qcManager.VerifyCertificate(qc, out Arg.Any<string>()).Returns(true);
        timeoutManager.VerifyTimeoutCertificate(tc, out Arg.Any<string?>()).Returns(true);

        SyncInfoManager manager = new(xdcContext, qcManager, timeoutManager, logManager);

        bool result = manager.VerifySyncInfo(syncInfo, out _);

        result.Should().BeTrue();
    }

    [Test]
    public void VerifySyncInfo_WhenOnlyTimeoutCertificateIsHigher_ReturnsTrue()
    {
        BlockRoundInfo blockRoundInfo = new(Keccak.Zero, 5, 1);
        QuorumCertificate qc = new(blockRoundInfo, Array.Empty<Signature>(), 0);
        TimeoutCertificate tc = new(10, Array.Empty<Signature>(), 0);
        SyncInfo syncInfo = new(qc, tc);

        IXdcConsensusContext xdcContext = Substitute.For<IXdcConsensusContext>();
        QuorumCertificate contextQc = new(
            new BlockRoundInfo(Keccak.Zero, 5, 1),
            Array.Empty<Signature>(),
            0);
        TimeoutCertificate contextTc = new(5, Array.Empty<Signature>(), 0);
        xdcContext.HighestQC.Returns(contextQc);
        xdcContext.HighestTC.Returns(contextTc);

        IQuorumCertificateManager qcManager = Substitute.For<IQuorumCertificateManager>();
        ITimeoutCertificateManager timeoutManager = Substitute.For<ITimeoutCertificateManager>();
        ILogManager logManager = Substitute.For<ILogManager>();

        qcManager.VerifyCertificate(qc, out Arg.Any<string>()).Returns(true);
        timeoutManager.VerifyTimeoutCertificate(tc, out Arg.Any<string?>()).Returns(true);

        SyncInfoManager manager = new(xdcContext, qcManager, timeoutManager, logManager);

        bool result = manager.VerifySyncInfo(syncInfo, out _);

        result.Should().BeTrue();
    }
}
