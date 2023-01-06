// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Producers;

public interface IPayloadAttributes
{
    /// <summary>
    /// V1
    /// </summary>
    public ulong Timestamp { get; }

    /// <summary>
    /// V1
    /// </summary>
    public Keccak PrevRandao { get; }

    /// <summary>
    /// V1
    /// </summary>
    public Address SuggestedFeeRecipient { get; }

    /// <summary>
    /// V2
    /// </summary>
    public IReadOnlyList<IWithdrawal>? Withdrawals => null;

    /// <summary>
    /// GasLimit
    /// </summary>
    /// <remarks>
    /// Only used for MEV-Boost
    /// </remarks>
    public long? GasLimit { get; }
}
