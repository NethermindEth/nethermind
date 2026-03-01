// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.AuRa.Validators
{
    public class ValidatorInfo(long finalizingBlockNumber, long previousFinalizingBlockNumber, Address[] validators)
    {
        public long FinalizingBlockNumber { get; } = finalizingBlockNumber;
        public long PreviousFinalizingBlockNumber { get; } = previousFinalizingBlockNumber;
        public Address[] Validators { get; } = validators;
    }
}
