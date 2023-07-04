// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.AuRa.Validators
{
    public class ValidatorInfo
    {
        static ValidatorInfo()
        {
            Rlp.Decoders[typeof(ValidatorInfo)] = new ValidatorInfoDecoder();
        }

        public ValidatorInfo(long finalizingBlockNumber, long previousFinalizingBlockNumber, Address[] validators)
        {
            FinalizingBlockNumber = finalizingBlockNumber;
            PreviousFinalizingBlockNumber = previousFinalizingBlockNumber;
            Validators = validators;
        }

        public long FinalizingBlockNumber { get; }
        public long PreviousFinalizingBlockNumber { get; }
        public Address[] Validators { get; }
    }
}
