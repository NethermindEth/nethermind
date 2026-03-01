// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.AuRa.Validators
{
    public class PendingValidators(long blockNumber, Hash256 blockHash, Address[] addresses)
    {
        public Address[] Addresses { get; } = addresses;
        public long BlockNumber { get; } = blockNumber;
        public Hash256 BlockHash { get; } = blockHash;
        public bool AreFinalized { get; set; }
    }
}
