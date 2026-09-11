// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.AuRa.Validators
{
    public class ValidatorInfo(ulong finalizingBlockNumber, ulong previousFinalizingBlockNumber, Address[] validators)
    {
        static ValidatorInfo() => Rlp.RegisterDecoder(typeof(ValidatorInfo), new ValidatorInfoDecoder());

        public ulong FinalizingBlockNumber { get; } = finalizingBlockNumber;
        public ulong PreviousFinalizingBlockNumber { get; } = previousFinalizingBlockNumber;
        public Address[] Validators { get; } = validators;
    }
}
