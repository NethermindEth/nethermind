// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.AuRa.Validators
{
    public class NullReportingValidator : IReportingValidator
    {
        public static NullReportingValidator Instance { get; } = new NullReportingValidator();
        public void ReportMalicious(Address validator, long blockNumber, byte[] proof, IReportingValidator.MaliciousCause cause) { }
        public void ReportBenign(Address validator, long blockNumber, IReportingValidator.BenignCause cause) { }
        public void TryReportSkipped(BlockHeader header, BlockHeader parent) { }
    }
}
