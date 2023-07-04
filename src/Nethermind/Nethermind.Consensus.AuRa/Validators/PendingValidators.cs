// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.AuRa.Validators
{
    public class PendingValidators
    {
        static PendingValidators()
        {
            Rlp.Decoders[typeof(PendingValidators)] = new PendingValidatorsDecoder();
        }

        public PendingValidators(long blockNumber, Keccak blockHash, Address[] addresses)
        {
            BlockNumber = blockNumber;
            BlockHash = blockHash;
            Addresses = addresses;
        }

        public Address[] Addresses { get; }
        public long BlockNumber { get; }
        public Keccak BlockHash { get; }
        public bool AreFinalized { get; set; }
    }
}
