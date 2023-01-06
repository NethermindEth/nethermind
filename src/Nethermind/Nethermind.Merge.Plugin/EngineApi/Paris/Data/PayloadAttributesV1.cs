// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Merge.Plugin.EngineApi.Paris.Data;

public class PayloadAttributesV1 : IPayloadAttributes
{
    public ulong Timestamp { get; set; }

    public Keccak PrevRandao { get; set; } = Keccak.Zero;

    public Address SuggestedFeeRecipient { get; set; } = Address.Zero;

    /// <summary>
    /// GasLimit
    /// </summary>
    /// <remarks>
    /// Only used for MEV-Boost
    /// </remarks>
    public long? GasLimit { get; set; }

    public override string ToString() => ToString(string.Empty);

    public string ToString(string indentation) => BuildString(new StringBuilder(), indentation).ToString();

    protected virtual StringBuilder BuildString(StringBuilder builder, string indentation) =>
        builder.Append($"{indentation}{GetType().Name} {{")
            .Append($"{nameof(Timestamp)}: {Timestamp}, ")
            .Append($"{nameof(PrevRandao)}: {PrevRandao}, ")
            .Append($"{nameof(SuggestedFeeRecipient)}: {SuggestedFeeRecipient}")
            .Append('}');
}
