// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Consensus.Producers
{
    public class PayloadAttributes
    {
        public ulong Timestamp { get; set; }

        public Keccak PrevRandao { get; set; }

        public Address SuggestedFeeRecipient { get; set; }

        /// <summary>
        /// GasLimit
        /// </summary>
        /// <remarks>
        /// Only used for MEV-Boost
        /// </remarks>
        public long? GasLimit { get; set; }

        public override string ToString()
        {
            return $"PayloadAttributes: ({nameof(Timestamp)}: {Timestamp}, {nameof(PrevRandao)}: {PrevRandao}, {nameof(SuggestedFeeRecipient)}: {SuggestedFeeRecipient})";
        }
    }
}
