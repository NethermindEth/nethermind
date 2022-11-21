// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only 

using System.Collections.Generic;
using System.Text;
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

        public IList<Withdrawal>? Withdrawals { get; set; }

        /// <summary>
        /// GasLimit
        /// </summary>
        /// <remarks>
        /// Only used for MEV-Boost
        /// </remarks>
        public long? GasLimit { get; set; }

        public override string ToString() => ToString(string.Empty);

        public string ToString(string indentation) => new StringBuilder()
            .AppendLine($"{indentation}{nameof(Timestamp)}:             {Timestamp}")
            .AppendLine($"{indentation}{nameof(PrevRandao)}:            {PrevRandao}")
            .AppendLine($"{indentation}{nameof(SuggestedFeeRecipient)}: {SuggestedFeeRecipient}")
            .AppendLine($"{indentation}{nameof(Withdrawals)}:           {Withdrawals}")
            .ToString();
    }
}
