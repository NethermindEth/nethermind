// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.EngineApi.Shanghai.Data;

/// <summary>
/// Represents a withdrawal that has been validated at the consensus layer.
/// <see href="https://github.com/ethereum/execution-apis/blob/main/src/engine/shanghai.md#withdrawalv1"/>
/// </summary>
public class WithdrawalV1 : IWithdrawal
{
    /// <summary>
    /// Gets or sets the withdrawal unique id.
    /// </summary>
    public ulong Index { get; set; }

    /// <summary>
    /// Gets or sets the validator index on the consensus layer the withdrawal corresponds to.
    /// </summary>
    public ulong ValidatorIndex { get; set; }

    /// <summary>
    /// Gets or sets the withdrawal address.
    /// </summary>
    public Address Address { get; set; } = Address.Zero;

    /// <summary>
    /// Gets or sets the withdrawal amount in Wei.
    /// </summary>
    public UInt256 Amount { get; set; }

    public override string ToString() => ToString(string.Empty);

    public string ToString(string indentation) => new StringBuilder($"{indentation}{nameof(WithdrawalV1)} {{")
        .Append($"{nameof(Index)}: {Index}, ")
        .Append($"{nameof(ValidatorIndex)}: {ValidatorIndex}, ")
        .Append($"{nameof(Address)}: {Address}, ")
        .Append($"{nameof(Amount)}: {Amount}}}")
        .ToString();
}
