// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.AuRa.Validators
{
    public interface IReportingValidator
    {
        void ReportMalicious(Address validator, long blockNumber, byte[] proof, MaliciousCause cause);
        void ReportBenign(Address validator, long blockNumber, BenignCause cause);
        void TryReportSkipped(BlockHeader header, BlockHeader parent);

        public enum BenignCause
        {
            FutureBlock,
            IncorrectProposer,
            SkippedStep
        }

        public enum MaliciousCause
        {
            DuplicateStep,
            SiblingBlocksInSameStep
        }
    }
}
