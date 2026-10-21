// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test;

[Parallelizable(ParallelScope.All)]
public class ForensicsProcessorTests
{
    [Test]
    public void FindQCsInSameRound_MatchesExpectedQcs()
    {
        ForensicsProcessor processor = BuildForensicsProcessor(Substitute.For<IBlockTree>());

        QuorumCertificate qc1 = BuildQc("qc1", round: 10, number: 910);
        QuorumCertificate qc2 = BuildQc("qc2", round: 12, number: 910);
        QuorumCertificate qc3 = BuildQc("qc3", round: 13, number: 910);
        QuorumCertificate qc4 = BuildQc("qc4", round: 12, number: 910);
        QuorumCertificate qc5 = BuildQc("qc5", round: 13, number: 910);
        QuorumCertificate qc6 = BuildQc("qc6", round: 15, number: 910);

        bool found = processor.TryFindQCsInSameRound(
            [qc1, qc2, qc3],
            [qc4, qc5, qc6],
            out QuorumCertificate? first,
            out QuorumCertificate? second);

        Assert.That(found, Is.True);
        Assert.That(first, Is.EqualTo(qc2));
        Assert.That(second, Is.EqualTo(qc4));
    }

    [Test]
    public void FindAncestorBlockHash_ReturnsExpectedAncestorAndPaths()
    {
        Hash256 ancestorHash = Keccak.Compute("ancestor");
        Hash256 firstChildHash = Keccak.Compute("first-child");
        Hash256 secondChildHash = Keccak.Compute("second-child");
        Hash256 thirdChildHash = Keccak.Compute("third-child");

        XdcBlockHeader ancestor = Build.A.XdcBlockHeader()
            .WithHash(ancestorHash)
            .WithNumber(100)
            .TestObject;
        XdcBlockHeader firstChild = Build.A.XdcBlockHeader()
            .WithHash(firstChildHash)
            .WithParentHash(ancestorHash)
            .WithNumber(101)
            .TestObject;
        XdcBlockHeader secondChild = Build.A.XdcBlockHeader()
            .WithHash(secondChildHash)
            .WithParentHash(firstChildHash)
            .WithNumber(102)
            .TestObject;
        XdcBlockHeader thirdChild = Build.A.XdcBlockHeader()
            .WithHash(thirdChildHash)
            .WithParentHash(ancestorHash)
            .WithNumber(101)
            .TestObject;

        Dictionary<Hash256, XdcBlockHeader> headers = new()
        {
            [ancestorHash] = ancestor,
            [firstChildHash] = firstChild,
            [secondChildHash] = secondChild,
            [thirdChildHash] = thirdChild
        };

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindHeader(Arg.Any<Hash256>()).Returns(call =>
        {
            Hash256 hash = call.Arg<Hash256>();
            return headers.TryGetValue(hash, out XdcBlockHeader? header) ? header : null;
        });

        ForensicsProcessor processor = BuildForensicsProcessor(blockTree);
        (Hash256 foundAncestor, IList<string> firstPath, IList<string> secondPath) = processor.FindAncestorBlockHash(
            new BlockRoundInfo(secondChildHash, round: 20, number: 102),
            new BlockRoundInfo(thirdChildHash, round: 21, number: 101));

        Assert.That(foundAncestor, Is.EqualTo(ancestorHash));
        Assert.That(firstPath, Is.EqualTo(new[] { ancestorHash.ToString(), firstChildHash.ToString(), secondChildHash.ToString() }));
        Assert.That(secondPath, Is.EqualTo(new[] { ancestorHash.ToString(), thirdChildHash.ToString() }));
    }

    [Test]
    public async Task SetCommittedQCs_ValidInput_StoresThreeQcsInOrder()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        ForensicsProcessor processor = BuildForensicsProcessor(blockTree);

        Hash256 ancestorHash = Keccak.Compute("ancestor-for-set");
        QuorumCertificate qc1 = new(new BlockRoundInfo(ancestorHash, 11, 100), [], 450);

        XdcBlockHeader header1 = Build.A.XdcBlockHeader()
            .WithParentHash(ancestorHash)
            .WithNumber(101)
            .TestObject;
        header1.ExtraConsensusData = new ExtraFieldsV2(12, qc1);

        QuorumCertificate qc2 = new(new BlockRoundInfo(header1.Hash!, 12, 101), [], 450);

        XdcBlockHeader header2 = Build.A.XdcBlockHeader()
            .WithParentHash(header1.Hash!)
            .WithNumber(102)
            .TestObject;
        header2.ExtraConsensusData = new ExtraFieldsV2(13, qc2);

        QuorumCertificate incomingQc = new(new BlockRoundInfo(header2.Hash!, 13, 102), [], 450);

        Assert.That(header2.ExtraConsensusData!.QuorumCert.ProposedBlockInfo.Hash, Is.EqualTo(header1.Hash));
        Assert.That(incomingQc.ProposedBlockInfo.Hash, Is.EqualTo(header2.Hash));

        await processor.SetCommittedQCs([header1, header2], incomingQc);

        QuorumCertificate[] highestCommittedQcs = processor.GetHighestCommittedQcsSnapshot();
        Assert.That(highestCommittedQcs.Length, Is.EqualTo(3));
        Assert.That(highestCommittedQcs[0], Is.EqualTo(qc1));
        Assert.That(highestCommittedQcs[1], Is.EqualTo(qc2));
        Assert.That(highestCommittedQcs[2], Is.EqualTo(incomingQc));
    }

    private static ForensicsProcessor BuildForensicsProcessor(IBlockTree blockTree) => new(
            blockTree,
            Substitute.For<IEpochSwitchManager>(),
            LimboLogs.Instance);

    private static QuorumCertificate BuildQc(string seed, ulong round, ulong number)
    {
        Hash256 hash = Keccak.Compute(seed);
        return new QuorumCertificate(new BlockRoundInfo(hash, round, number), [], 450);
    }
}
