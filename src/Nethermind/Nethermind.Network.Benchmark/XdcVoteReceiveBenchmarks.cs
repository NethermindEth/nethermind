// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Synchronization.Peers;
using Nethermind.Xdc;
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Types;
using NSubstitute;

namespace Nethermind.Network.Benchmarks;

/// <summary>
/// One round of XDC votes as a follower receives them: every masternode votes once, and every vote arrives once from
/// each peer that relays it, decoded into a new object each time.
/// </summary>
[MemoryDiagnoser]
public class XdcVoteReceiveBenchmarks
{
    /// <summary>The XDC mainnet masternode count.</summary>
    private const int Masternodes = 108;

    private const ulong Round = 1_000;
    private const ulong GapNumber = 450;
    private const long Head = 2_000;

    private readonly BlockRoundInfo _block = new(TestItem.KeccakA, Round, Head);
    private Signature[] _signatures = null!;
    private XdcConsensusContext _context = null!;
    private IBlockTree _blockTree = null!;
    private ISnapshotManager _snapshotManager = null!;
    private ISyncPeerPool _syncPeerPool = null!;
    private IEpochSwitchManager _epochSwitchManager = null!;
    private IQuorumCertificateManager _quorumCertificateManager = null!;
    private ISpecProvider _specProvider = null!;
    private ISigner _signer = null!;
    private IForensicsProcessor _forensicsProcessor = null!;

    /// <summary>Copies of each vote that reach the node: one per relaying peer.</summary>
    [Params(1, 10, 30)]
    public int CopiesPerVote { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        EthereumEcdsa ecdsa = new(0);
        VoteDecoder decoder = new();
        using PrivateKeyGenerator generator = new();
        PrivateKey[] keys = generator.Generate(Masternodes).ToArray();
        Address[] masternodes = new Address[Masternodes];
        _signatures = new Signature[Masternodes];
        for (int i = 0; i < Masternodes; i++)
        {
            KeccakRlpWriter writer = new();
            decoder.Encode(ref writer, new Vote(_block, GapNumber), RlpBehaviors.ForSealing);
            _signatures[i] = ecdsa.Sign(keys[i], writer.GetValueHash());
            masternodes[i] = keys[i].Address;
        }

        _context = new XdcConsensusContext();
        _context.SetNewRound(Round);
        _blockTree = Substitute.For<IBlockTree>();
        _blockTree.Head.Returns(new Block(Build.A.XdcBlockHeader().WithNumber(Head).TestObject));
        _snapshotManager = Substitute.For<ISnapshotManager>();
        _snapshotManager.GetSnapshotByGapNumber(GapNumber).Returns(new Snapshot(0, Hash256.Zero, masternodes));
        _syncPeerPool = Substitute.For<ISyncPeerPool>();
        _epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        _quorumCertificateManager = Substitute.For<IQuorumCertificateManager>();
        _specProvider = Substitute.For<ISpecProvider>();
        _signer = Substitute.For<ISigner>();
        _forensicsProcessor = Substitute.For<IForensicsProcessor>();
    }

    [Benchmark]
    public long ReceiveRound()
    {
        using VotesManager votesManager = new(_context, _syncPeerPool, _blockTree, _epochSwitchManager, _snapshotManager,
            _quorumCertificateManager, _specProvider, _signer, _forensicsProcessor, NullLogManager.Instance);

        for (int copy = 0; copy < CopiesPerVote; copy++)
        {
            foreach (Signature signature in _signatures)
            {
                votesManager.OnReceiveVote(new Vote(_block, GapNumber, signature)).GetAwaiter().GetResult();
            }
        }

        return votesManager.GetVotesCount(new Vote(_block, GapNumber, _signatures[0]));
    }
}
