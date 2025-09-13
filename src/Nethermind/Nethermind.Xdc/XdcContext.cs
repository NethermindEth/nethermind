// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Xdc.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Xdc;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BlockInfo = Nethermind.Xdc.Types.BlockInfo;
using Round = ulong;

using static Nethermind.Xdc.ConfigExtensions;
using Nethermind.Blockchain;
using Nethermind.Int256;
using Nethermind.Core.Extensions;
using System.Threading;
using System.Net.Http.Headers;
using Org.BouncyCastle.Asn1.Mozilla;
using Nethermind.Evm.State;
using Nethermind.Blockchain.Receipts;
using Nethermind.State;
using Nethermind.Core.Collections;
using Snapshot = Nethermind.Xdc.Types.Snapshot;
using Nethermind.Crypto;
using Nethermind.Xdc.Errors;
using Nethermind.Consensus.Rewards;

namespace Nethermind.Xdc;
public class XdcContext
{
    public XdcContext()
    {
        VerifiedHeader = new ConcurrentDictionary<ValueHash256, BlockHeader>();

        CurrentRound = 0;
        HighestSelfMindeRound = 0;
        HighestVotedRound = 0;
        HighestQC = default;
        LockQC = default;
        HighestTC = default;

        LockQC = default;
        HighestTC = default;
        HighestCommitBlock = null;

    }

    public ConcurrentDictionary<ValueHash256, BlockHeader> VerifiedHeader { get; set; }
    public ConcurrentDictionary<Hash256, Address> Signatures { get; set; }
    public Address Leader { get; set; }
    public int TimeoutCounter { get; set; } = 0;
    public Round CurrentRound { get; set; }
    public Round HighestSelfMindeRound { get; set; }
    public Round HighestVotedRound { get; set; }
    public QuorumCert HighestQC { get; set; }
    public QuorumCert LockQC { get; set; }
    public TimeoutCert HighestTC { get; set; }
    public BlockInfo HighestCommitBlock { get; set; }
    public SignFn SignFun { get; set; }

    public bool IsInitialized { get; set; } = false;

    public event Action<IBlockTree, ulong> NewRoundSetEvent;
    public void Initialize(IBlockTree chain, XdcBlockHeader header, IQuorumCertificateManager handler)
    {
        /*if (HighestQC.ProposedBlockInfo.Hash is not null)
        {
            IsInitialized = true;
            return;
        }

        if (header.Number == HotStuffConfig.SwitchBlock)
        {
            HighestQC = new QuorumCert
            {
                Signatures = [],
                ProposedBlockInfo = new BlockInfo(header.Hash, 0, header.Number),
                GapNumber = (ulong)header.Number < HotStuffConfig.Gap
                    ? (ulong)header.Number - HotStuffConfig.Gap
                    : 0ul,
            };

            CurrentRound = 1;
        }
        else
        {
            if (!Utils.TryGetExtraFields(header, out QuorumCert quorumCert, out _, out _))
            {
                throw new ConsensusHeaderDataExtractionException(nameof(ExtraFieldsV2));
            }

            handler.CommitCertificate(chain, quorumCert);
        }

        ulong lastGapNum = Math.Max((ulong)HotStuffConfig.SwitchBlock - HotStuffConfig.Gap, 0ul);

        var lastGapHeader = (XdcBlockHeader)chain.FindHeader((long)lastGapNum);

        if (SnapshotManager.TryGetSnapshot(lastGapHeader.Number, lastGapHeader.Hash, out Snapshot snapshot))
        {
            var checkoutPointHeader = (XdcBlockHeader)chain.FindHeader(HotStuffConfig.SwitchBlock);

            if (!Utils.TryGetExtraFields(checkoutPointHeader, out _, out _, out Address[] masterNodes))
            {
                throw new ConsensusHeaderDataExtractionException(nameof(ExtraFieldsV2));
            }

            if (masterNodes.Length == 0)
            {
                throw new ArgumentException($"masternodes are empty v2 switch number: {HotStuffConfig.SwitchBlock}";
            }

            var newSnapshot = new Snapshot((long)lastGapNum, lastGapHeader.Hash, masterNodes);
            if (!SnapshotManager.TryStoreSnapshot(newSnapshot))
            {
                throw new Exception("failed to store new Snapshot");
            }

            SnapshotManager.TryCacheSnapshot(newSnapshot);
        }

        CountDownTimer.Start();
        IsInitialized = true;*/
    }
    internal bool IsAllowedToSend(IBlockTree tree, XdcBlockHeader header)
    {
        throw new NotImplementedException();
    }


    internal void SetNewRound(IBlockTree chain, ulong round)
    {
        CurrentRound = round;
        TimeoutCounter = 0;

        // timer should be reset outside
        NewRoundSetEvent.Invoke(chain, round);
    }
}
