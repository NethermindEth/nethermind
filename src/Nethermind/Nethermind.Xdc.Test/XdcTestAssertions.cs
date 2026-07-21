// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Xdc.Types;
using NUnit.Framework.Constraints;

namespace Nethermind.Xdc.Test;

internal static class XdcTestAssertions
{
    public static EqualConstraint UsingXdcComparer(this EqualConstraint constraint, bool compareHash = true, bool compareSigner = true) =>
        constraint
            .Using<Block>(new XdcBlockComparer(compareHash))
            .Using<XdcBlockHeader>(new XdcBlockHeaderComparer(compareHash))
            .Using<BlockRoundInfo>(BlockRoundInfoComparer.Instance)
            .Using<EpochSwitchInfo>(EpochSwitchInfoComparer.Instance)
            .Using<ExtraFieldsV2>(ExtraFieldsV2Comparer.Instance)
            .Using<QuorumCertificate>(QuorumCertificateComparer.Instance)
            .Using<Snapshot>(SnapshotComparer.Instance)
            .Using<SubnetSnapshot>(SubnetSnapshotComparer.Instance)
            .Using<SyncInfo>(SyncInfoComparer.Instance)
            .Using<TimeoutCertificate>(TimeoutCertificateComparer.Instance)
            .Using<Vote>(compareSigner ? VoteComparer.Instance : VoteComparer.WithoutSigner);

    private sealed class XdcBlockComparer(bool compareHash) : IEqualityComparer<Block>
    {
        private readonly IEqualityComparer<Block> _baseComparer = TestEqualityComparers.Block(compareHash);
        private readonly XdcBlockHeaderComparer _headerComparer = new(compareHash);

        public bool Equals(Block? actual, Block? expected)
        {
            if (actual is null || expected is null)
            {
                return actual is null && expected is null;
            }

            if (!_baseComparer.Equals(actual, expected) ||
                actual.Header is not XdcBlockHeader actualHeader ||
                expected.Header is not XdcBlockHeader expectedHeader ||
                !_headerComparer.Equals(actualHeader, expectedHeader) ||
                actual.Uncles.Length != expected.Uncles.Length)
            {
                return false;
            }

            for (int i = 0; i < expected.Uncles.Length; i++)
            {
                if (actual.Uncles[i] is not XdcBlockHeader actualUncle ||
                    expected.Uncles[i] is not XdcBlockHeader expectedUncle ||
                    !_headerComparer.Equals(actualUncle, expectedUncle))
                {
                    return false;
                }
            }

            return true;
        }

        public int GetHashCode(Block obj) => 0;
    }

    private sealed class XdcBlockHeaderComparer(bool compareHash) : IEqualityComparer<XdcBlockHeader>
    {
        private readonly IEqualityComparer<BlockHeader> _baseComparer = TestEqualityComparers.BlockHeader(compareHash);

        public bool Equals(XdcBlockHeader? actual, XdcBlockHeader? expected)
        {
            if (actual is null || expected is null)
            {
                return actual is null && expected is null;
            }

            if (actual.GetType() != expected.GetType())
            {
                return false;
            }

            if (!_baseComparer.Equals(actual, expected) ||
                !TestEqualityComparers.BytesEqual(actual.Validators, expected.Validators) ||
                !TestEqualityComparers.BytesEqual(actual.Validator, expected.Validator) ||
                !TestEqualityComparers.BytesEqual(actual.Penalties, expected.Penalties))
            {
                return false;
            }

            return actual is not XdcSubnetBlockHeader actualSubnet ||
                expected is XdcSubnetBlockHeader expectedSubnet &&
                TestEqualityComparers.BytesEqual(actualSubnet.NextValidators, expectedSubnet.NextValidators);
        }

        public int GetHashCode(XdcBlockHeader obj) => 0;
    }

    private sealed class BlockRoundInfoComparer : IEqualityComparer<BlockRoundInfo>
    {
        public static BlockRoundInfoComparer Instance { get; } = new();

        public bool Equals(BlockRoundInfo? actual, BlockRoundInfo? expected)
        {
            if (actual is null || expected is null)
            {
                return actual is null && expected is null;
            }

            return actual.Hash == expected.Hash &&
                actual.Round == expected.Round &&
                actual.BlockNumber == expected.BlockNumber;
        }

        public int GetHashCode(BlockRoundInfo obj) => 0;
    }

    private sealed class EpochSwitchInfoComparer : IEqualityComparer<EpochSwitchInfo>
    {
        public static EpochSwitchInfoComparer Instance { get; } = new();

        public bool Equals(EpochSwitchInfo? actual, EpochSwitchInfo? expected)
        {
            if (actual is null || expected is null)
            {
                return actual is null && expected is null;
            }

            return TestEqualityComparers.ArraysEqual(actual.Masternodes, expected.Masternodes) &&
                TestEqualityComparers.ArraysEqual(actual.StandbyNodes, expected.StandbyNodes) &&
                TestEqualityComparers.ArraysEqual(actual.Penalties, expected.Penalties) &&
                BlockRoundInfoComparer.Instance.Equals(actual.EpochSwitchBlockInfo, expected.EpochSwitchBlockInfo) &&
                BlockRoundInfoComparer.Instance.Equals(actual.EpochSwitchParentBlockInfo, expected.EpochSwitchParentBlockInfo);
        }

        public int GetHashCode(EpochSwitchInfo obj) => 0;
    }

    private sealed class ExtraFieldsV2Comparer : IEqualityComparer<ExtraFieldsV2>
    {
        public static ExtraFieldsV2Comparer Instance { get; } = new();

        public bool Equals(ExtraFieldsV2? actual, ExtraFieldsV2? expected)
        {
            if (actual is null || expected is null)
            {
                return actual is null && expected is null;
            }

            return actual.BlockRound == expected.BlockRound &&
                QuorumCertificateComparer.Instance.Equals(actual.QuorumCert, expected.QuorumCert);
        }

        public int GetHashCode(ExtraFieldsV2 obj) => 0;
    }

    private sealed class QuorumCertificateComparer : IEqualityComparer<QuorumCertificate>
    {
        public static QuorumCertificateComparer Instance { get; } = new();

        public bool Equals(QuorumCertificate? actual, QuorumCertificate? expected)
        {
            if (actual is null || expected is null)
            {
                return actual is null && expected is null;
            }

            return BlockRoundInfoComparer.Instance.Equals(actual.ProposedBlockInfo, expected.ProposedBlockInfo) &&
                TestEqualityComparers.ArraysEqual(actual.Signatures, expected.Signatures) &&
                actual.GapNumber == expected.GapNumber;
        }

        public int GetHashCode(QuorumCertificate obj) => 0;
    }

    private sealed class SnapshotComparer : IEqualityComparer<Snapshot>
    {
        public static SnapshotComparer Instance { get; } = new();

        public bool Equals(Snapshot? actual, Snapshot? expected)
        {
            if (actual is null || expected is null)
            {
                return actual is null && expected is null;
            }

            return actual.BlockNumber == expected.BlockNumber &&
                actual.HeaderHash == expected.HeaderHash &&
                TestEqualityComparers.ArraysEqual(actual.NextEpochCandidates, expected.NextEpochCandidates);
        }

        public int GetHashCode(Snapshot obj) => 0;
    }

    private sealed class SubnetSnapshotComparer : IEqualityComparer<SubnetSnapshot>
    {
        public static SubnetSnapshotComparer Instance { get; } = new();

        public bool Equals(SubnetSnapshot? actual, SubnetSnapshot? expected)
        {
            if (actual is null || expected is null)
            {
                return actual is null && expected is null;
            }

            return SnapshotComparer.Instance.Equals(actual, expected) &&
                TestEqualityComparers.ArraysEqual(actual.NextEpochPenalties, expected.NextEpochPenalties);
        }

        public int GetHashCode(SubnetSnapshot obj) => 0;
    }

    private sealed class SyncInfoComparer : IEqualityComparer<SyncInfo>
    {
        public static SyncInfoComparer Instance { get; } = new();

        public bool Equals(SyncInfo? actual, SyncInfo? expected)
        {
            if (actual is null || expected is null)
            {
                return actual is null && expected is null;
            }

            return QuorumCertificateComparer.Instance.Equals(actual.HighestQuorumCert, expected.HighestQuorumCert) &&
                TimeoutCertificateComparer.Instance.Equals(actual.HighestTimeoutCert, expected.HighestTimeoutCert);
        }

        public int GetHashCode(SyncInfo obj) => 0;
    }

    private sealed class TimeoutCertificateComparer : IEqualityComparer<TimeoutCertificate>
    {
        public static TimeoutCertificateComparer Instance { get; } = new();

        public bool Equals(TimeoutCertificate? actual, TimeoutCertificate? expected)
        {
            if (actual is null || expected is null)
            {
                return actual is null && expected is null;
            }

            return actual.Round == expected.Round &&
                TestEqualityComparers.ArraysEqual(actual.Signatures, expected.Signatures) &&
                actual.GapNumber == expected.GapNumber;
        }

        public int GetHashCode(TimeoutCertificate obj) => 0;
    }

    private sealed class VoteComparer(bool compareSigner) : IEqualityComparer<Vote>
    {
        public static VoteComparer Instance { get; } = new(compareSigner: true);
        public static VoteComparer WithoutSigner { get; } = new(compareSigner: false);

        public bool Equals(Vote? actual, Vote? expected)
        {
            if (actual is null || expected is null)
            {
                return actual is null && expected is null;
            }

            return BlockRoundInfoComparer.Instance.Equals(actual.ProposedBlockInfo, expected.ProposedBlockInfo) &&
                actual.GapNumber == expected.GapNumber &&
                object.Equals(actual.Signature, expected.Signature) &&
                (!compareSigner || actual.Signer == expected.Signer) &&
                actual.IsMyVote == expected.IsMyVote;
        }

        public int GetHashCode(Vote obj) => 0;
    }
}
