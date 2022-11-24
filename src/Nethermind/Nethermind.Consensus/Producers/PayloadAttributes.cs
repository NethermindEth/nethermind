// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only 

using System.Collections.Generic;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Producers;

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

    public string ToString(string indentation)
    {
        var sb = new StringBuilder($"{indentation}{nameof(PayloadAttributes)} {{")
            .Append($"{nameof(Timestamp)}: {Timestamp}, ")
            .Append($"{nameof(PrevRandao)}: {PrevRandao}, ")
            .Append($"{nameof(SuggestedFeeRecipient)}: {SuggestedFeeRecipient}");

        if (Withdrawals is not null)
        {
            sb.AppendLine($", {nameof(Withdrawals)}: ");

            // Consider limiting the output
            foreach (var withdrawal in Withdrawals)
                sb.AppendLine(withdrawal.ToString($"{indentation}    "));
        }

        sb.Append('}');

        return sb.ToString();
    }
}
