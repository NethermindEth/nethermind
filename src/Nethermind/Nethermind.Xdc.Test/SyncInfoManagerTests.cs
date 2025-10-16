// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Core;
using FluentAssertions;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Xdc.Errors;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test;
internal class SyncInfoManagerTests
{
    private SyncInfoManager _syncInfoManager;
    private XdcContext _context;
    private IQuorumCertificateManager _quorumCertificateManager;
    private ITimeoutCertificateManager _timeoutCertificateManager;

    private ILogger _logger;


    [SetUp]
    public void Setup()
    {
        _context = new XdcContext();
        _logger = TestLogManager.Instance.GetLogger(nameof(SyncInfoManagerTests));
        _quorumCertificateManager = NSubstitute.Substitute.For<IQuorumCertificateManager>();
        _timeoutCertificateManager = NSubstitute.Substitute.For<ITimeoutCertificateManager>();
        _syncInfoManager = new SyncInfoManager(_context, _quorumCertificateManager, _timeoutCertificateManager, _logger);
    }

    [Test]
    public void GetSyncInfo_ReturnsCurrentHighestCertificates()
    {
        // Arrange
        var highestQC = new QuorumCertificate(new BlockRoundInfo(TestItem.KeccakA, 1, 1), [TestItem.RandomSignatureA], 1);
        var highestTC = new TimeoutCertificate(2, [TestItem.RandomSignatureA], 1);
        _context.HighestQC = highestQC;
        _context.HighestTC = highestTC;
        // Act
        var syncInfo = _syncInfoManager.GetSyncInfo();
        // Assert

        highestQC.Should().BeEquivalentTo(syncInfo.HighestQuorumCert);
        highestTC.Should().BeEquivalentTo(syncInfo.HighestTimeoutCert);
    }

    [Test]
    public void ProcessSyncInfo_CommitsCertificates()
    {
        // Arrange
        var qc = new QuorumCertificate(new BlockRoundInfo(TestItem.KeccakA, 1, 1), [TestItem.RandomSignatureA], 1);
        var tc = new TimeoutCertificate(2, [TestItem.RandomSignatureA], 1);
        var syncInfo = new SyncInfo(qc, tc);

        _quorumCertificateManager.When(x => x.CommitCertificate(Arg.Any<QuorumCertificate>()))
                 .Do(x => {
                     QuorumCertificate arg = x.Arg<QuorumCertificate>();
                    _context.HighestQC = arg;
                 });


        _timeoutCertificateManager.When(x => x.ProcessTimeoutCertificate(Arg.Any<TimeoutCertificate>()))
                 .Do(x => {
                     TimeoutCertificate arg = x.Arg<TimeoutCertificate>();
                     _context.HighestTC = arg;
                 });

        // Act
        _syncInfoManager.ProcessSyncInfo(syncInfo);
        // Assert
        _quorumCertificateManager.Received(1).CommitCertificate(qc);
        _timeoutCertificateManager.Received(1).ProcessTimeoutCertificate(tc);

        _context.HighestQC.Should().BeEquivalentTo(qc);
        _context.HighestTC.Should().BeEquivalentTo(tc);
    }

    [Test]
    public void ProcessSyncInfo_ThrowsWhenQcManagerFails()
    {
        Exception expectedException = new CertificateValidationException(CertificateType.QuorumCertificate, CertificateValidationFailure.InvalidRound);

        // Arrange
        var qc = new QuorumCertificate(new BlockRoundInfo(TestItem.KeccakA, 1, 1), [TestItem.RandomSignatureA], 1);
        var tc = new TimeoutCertificate(2, [TestItem.RandomSignatureA], 1);
        var syncInfo = new SyncInfo(qc, tc);

        _quorumCertificateManager.When(x => x.CommitCertificate(Arg.Any<QuorumCertificate>()))
                 .Do(x => throw expectedException);
        // Act
        Action act = () => _syncInfoManager.ProcessSyncInfo(syncInfo);
        // Assert
        act.Should().Throw<Exception>().WithMessage(expectedException.Message);
        _quorumCertificateManager.Received(1).CommitCertificate(qc);
        _timeoutCertificateManager.DidNotReceive().ProcessTimeoutCertificate(tc);
    }

    [Test]
    public void ProcessSyncInfo_ThrowsWhenTcManagerFails()
    {
        Exception expectedException = new CertificateValidationException(CertificateType.TimeoutCertificate, CertificateValidationFailure.InvalidRound);

        // Arrange
        var qc = new QuorumCertificate(new BlockRoundInfo(TestItem.KeccakA, 1, 1), [TestItem.RandomSignatureA], 1);
        var tc = new TimeoutCertificate(2, [TestItem.RandomSignatureA], 1);
        var syncInfo = new SyncInfo(qc, tc);
        _timeoutCertificateManager.When(x => x.ProcessTimeoutCertificate(Arg.Any<TimeoutCertificate>()))
                 .Do(x => throw expectedException);
        // Act
        Action act = () => _syncInfoManager.ProcessSyncInfo(syncInfo);
        // Assert
        act.Should().Throw<Exception>().WithMessage(expectedException.Message);
        _timeoutCertificateManager.Received(1).ProcessTimeoutCertificate(tc);
        _quorumCertificateManager.Received(1).CommitCertificate(qc);
    }

    [Test]
    public void VerifySyncInfo_ReturnsFalse_WhenQCertificateIsNotHigher()
    {
        // Arrange
        var currentQC = new QuorumCertificate(new BlockRoundInfo(TestItem.KeccakA, 2, 1), [TestItem.RandomSignatureA], 1);
        var currentTC = new TimeoutCertificate(2, [TestItem.RandomSignatureA], 1);
        _context.HighestQC = currentQC;
        _context.HighestTC = currentTC;
        var qc = new QuorumCertificate(new BlockRoundInfo(TestItem.KeccakA, 1, 1), [TestItem.RandomSignatureA], 1);
        var tc = new TimeoutCertificate(3, [TestItem.RandomSignatureA], 1);
        var syncInfo = new SyncInfo(qc, tc);
        // Act
        var result = _syncInfoManager.VerifySyncInfo(syncInfo);
        // Assert
        result.Should().BeFalse();
    }

    [Test]
    public void VerifySyncInfo_ReturnsFalse_WhenTCertificateIsNotHigher()
    {
        // Arrange
        var currentQC = new QuorumCertificate(new BlockRoundInfo(TestItem.KeccakA, 2, 1), [TestItem.RandomSignatureA], 1);
        var currentTC = new TimeoutCertificate(3, [TestItem.RandomSignatureA], 1);
        _context.HighestQC = currentQC;
        _context.HighestTC = currentTC;
        var qc = new QuorumCertificate(new BlockRoundInfo(TestItem.KeccakA, 3, 1), [TestItem.RandomSignatureA], 1);
        var tc = new TimeoutCertificate(2, [TestItem.RandomSignatureA], 1);
        var syncInfo = new SyncInfo(qc, tc);
        // Act
        var result = _syncInfoManager.VerifySyncInfo(syncInfo);
        // Assert
        result.Should().BeFalse();
    }

    [Test]
    public void VerifySyncInfo_ReturnsTrue_WhenBothCertificatesAreHigher_AndCertificatesAreValid()
    {
        // Arrange
        var currentQC = new QuorumCertificate(new BlockRoundInfo(TestItem.KeccakA, 2, 1), [TestItem.RandomSignatureA], 1);
        var currentTC = new TimeoutCertificate(2, [TestItem.RandomSignatureA], 1);
        _context.HighestQC = currentQC;
        _context.HighestTC = currentTC;

        var qc = new QuorumCertificate(new BlockRoundInfo(TestItem.KeccakA, 3, 1), [TestItem.RandomSignatureA], 1);
        var tc = new TimeoutCertificate(3, [TestItem.RandomSignatureA], 1);
        var syncInfo = new SyncInfo(qc, tc);
        _quorumCertificateManager.VerifyCertificate(Arg.Any<QuorumCertificate>(), Arg.Any<XdcBlockHeader>(), out Arg.Any<string>()).Returns(true);
        _timeoutCertificateManager.VerifyTimeoutCertificate(Arg.Any<TimeoutCertificate>(), out Arg.Any<string>()).Returns(true);
        // Act
        var result = _syncInfoManager.VerifySyncInfo(syncInfo);
        // Assert
        result.Should().BeTrue();
    }

}
