// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Xdc.Types;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

internal static class XdcTestAssertions
{
    public static void AssertExtraFields(ExtraFieldsV2? actual, ExtraFieldsV2 expected)
    {
        Assert.That(actual, Is.Not.Null);
        if (actual is null)
        {
            return;
        }

        Assert.Multiple(() =>
        {
            Assert.That(actual.BlockRound, Is.EqualTo(expected.BlockRound));
            AssertQuorumCertificate(actual.QuorumCert, expected.QuorumCert);
        });
    }

    public static void AssertSyncInfo(SyncInfo? actual, SyncInfo expected)
    {
        Assert.That(actual, Is.Not.Null);
        if (actual is null)
        {
            return;
        }

        Assert.Multiple(() =>
        {
            AssertQuorumCertificate(actual.HighestQuorumCert, expected.HighestQuorumCert);
            AssertTimeoutCertificate(actual.HighestTimeoutCert, expected.HighestTimeoutCert);
        });
    }

    public static void AssertTimeoutCertificate(TimeoutCertificate? actual, TimeoutCertificate expected)
    {
        Assert.That(actual, Is.Not.Null);
        if (actual is null)
        {
            return;
        }

        Assert.Multiple(() =>
        {
            Assert.That(actual.Round, Is.EqualTo(expected.Round));
            Assert.That(actual.GapNumber, Is.EqualTo(expected.GapNumber));
            AssertSignatures(actual.Signatures, expected.Signatures);
        });
    }

    public static void AssertQuorumCertificate(QuorumCertificate? actual, QuorumCertificate? expected)
    {
        if (expected is null)
        {
            Assert.That(actual, Is.Null);
            return;
        }

        Assert.That(actual, Is.Not.Null);
        if (actual is null)
        {
            return;
        }

        Assert.Multiple(() =>
        {
            AssertBlockRoundInfo(actual.ProposedBlockInfo, expected.ProposedBlockInfo);
            Assert.That(actual.GapNumber, Is.EqualTo(expected.GapNumber));
            AssertSignatures(actual.Signatures, expected.Signatures);
        });
    }

    public static void AssertVote(Vote? actual, Vote expected, bool compareSigner = false)
    {
        Assert.That(actual, Is.Not.Null);
        if (actual is null)
        {
            return;
        }

        Assert.Multiple(() =>
        {
            AssertBlockRoundInfo(actual.ProposedBlockInfo, expected.ProposedBlockInfo);
            Assert.That(actual.GapNumber, Is.EqualTo(expected.GapNumber));
            Assert.That(actual.Signature, Is.EqualTo(expected.Signature));
            Assert.That(actual.IsMyVote, Is.EqualTo(expected.IsMyVote));
            if (compareSigner)
            {
                Assert.That(actual.Signer, Is.EqualTo(expected.Signer));
            }
        });
    }

    public static void AssertBlockRoundInfo(BlockRoundInfo? actual, BlockRoundInfo expected)
    {
        Assert.That(actual, Is.Not.Null);
        if (actual is null)
        {
            return;
        }

        Assert.Multiple(() =>
        {
            Assert.That(actual.Hash, Is.EqualTo(expected.Hash));
            Assert.That(actual.Round, Is.EqualTo(expected.Round));
            Assert.That(actual.BlockNumber, Is.EqualTo(expected.BlockNumber));
        });
    }

    public static void AssertEpochSwitchInfo(EpochSwitchInfo? actual, EpochSwitchInfo expected)
    {
        Assert.That(actual, Is.Not.Null);
        if (actual is null)
        {
            return;
        }

        Assert.Multiple(() =>
        {
            Assert.That(actual.Masternodes, Is.EquivalentTo(expected.Masternodes));
            Assert.That(actual.StandbyNodes, Is.EquivalentTo(expected.StandbyNodes));
            Assert.That(actual.Penalties, Is.EquivalentTo(expected.Penalties));
            AssertBlockRoundInfo(actual.EpochSwitchBlockInfo, expected.EpochSwitchBlockInfo);
            if (expected.EpochSwitchParentBlockInfo is null)
            {
                Assert.That(actual.EpochSwitchParentBlockInfo, Is.Null);
            }
            else
            {
                AssertBlockRoundInfo(actual.EpochSwitchParentBlockInfo, expected.EpochSwitchParentBlockInfo);
            }
        });
    }

    public static void AssertSnapshot(Snapshot? actual, Snapshot expected)
    {
        Assert.That(actual, Is.Not.Null);
        if (actual is null)
        {
            return;
        }

        Assert.Multiple(() =>
        {
            Assert.That(actual.BlockNumber, Is.EqualTo(expected.BlockNumber));
            Assert.That(actual.HeaderHash, Is.EqualTo(expected.HeaderHash));
            Assert.That(actual.NextEpochCandidates, Is.EquivalentTo(expected.NextEpochCandidates));
        });
    }

    public static void AssertSubnetSnapshot(SubnetSnapshot? actual, SubnetSnapshot expected)
    {
        AssertSnapshot(actual, expected);
        if (actual is null)
        {
            return;
        }

        Assert.That(actual.NextEpochPenalties, Is.EquivalentTo(expected.NextEpochPenalties));
    }

    public static void AssertXdcHeader(XdcBlockHeader? actual, XdcBlockHeader expected, bool compareHash = true)
    {
        Assert.That(actual, Is.Not.Null);
        if (actual is null)
        {
            return;
        }

        Assert.Multiple(() =>
        {
            if (compareHash)
            {
                Assert.That(actual.Hash, Is.EqualTo(expected.Hash));
            }

            Assert.That(actual.ParentHash, Is.EqualTo(expected.ParentHash));
            Assert.That(actual.UnclesHash, Is.EqualTo(expected.UnclesHash));
            Assert.That(actual.Beneficiary, Is.EqualTo(expected.Beneficiary));
            Assert.That(actual.Difficulty, Is.EqualTo(expected.Difficulty));
            Assert.That(actual.Number, Is.EqualTo(expected.Number));
            Assert.That(actual.GasLimit, Is.EqualTo(expected.GasLimit));
            Assert.That(actual.GasUsed, Is.EqualTo(expected.GasUsed));
            Assert.That(actual.Timestamp, Is.EqualTo(expected.Timestamp));
            Assert.That(actual.ExtraData, Is.EqualTo(expected.ExtraData));
            AssertBytesEquivalent(actual.Validators, expected.Validators);
            Assert.That(actual.Validator, Is.EqualTo(expected.Validator));
            AssertBytesEquivalent(actual.Penalties, expected.Penalties);
            Assert.That(actual.TxRoot, Is.EqualTo(expected.TxRoot));
            Assert.That(actual.StateRoot, Is.EqualTo(expected.StateRoot));
            Assert.That(actual.ReceiptsRoot, Is.EqualTo(expected.ReceiptsRoot));
            Assert.That(actual.Bloom, Is.EqualTo(expected.Bloom));
            Assert.That(actual.MixHash, Is.EqualTo(expected.MixHash));
            Assert.That(actual.Nonce, Is.EqualTo(expected.Nonce));
        });
    }

    public static void AssertBlock(Block? actual, Block expected)
    {
        Assert.That(actual, Is.Not.Null);
        if (actual is null)
        {
            return;
        }

        Assert.Multiple(() =>
        {
            AssertXdcHeader((XdcBlockHeader)actual.Header, (XdcBlockHeader)expected.Header);
            actual.Transactions.EqualToTransactions(expected.Transactions);
            Assert.That(actual.Uncles, Has.Length.EqualTo(expected.Uncles.Length));
        });

        for (int i = 0; i < expected.Uncles.Length; i++)
        {
            AssertXdcHeader((XdcBlockHeader)actual.Uncles[i], (XdcBlockHeader)expected.Uncles[i]);
        }
    }

    private static void AssertSignatures(Signature[]? actual, Signature[]? expected)
    {
        if (expected is null)
        {
            Assert.That(actual, Is.Null);
        }
        else
        {
            Assert.That(actual, Is.EqualTo(expected));
        }
    }

    private static void AssertBytesEquivalent(byte[]? actual, byte[]? expected)
    {
        if (expected is null)
        {
            Assert.That(actual, Is.Null);
            return;
        }

        Assert.That(actual, Is.EqualTo(expected));
    }
}
