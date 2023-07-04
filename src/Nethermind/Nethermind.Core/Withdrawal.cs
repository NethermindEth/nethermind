// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Newtonsoft.Json;

namespace Nethermind.Core;

/// <summary>
/// Represents a withdrawal that has been validated at the consensus layer.
/// </summary>
public class Withdrawal
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
    /// Gets or sets the withdrawal amount in GWei.
    /// </summary>
    [JsonProperty(PropertyName = "amount")]
    public ulong AmountInGwei { get; set; }

    [JsonIgnore]
    public UInt256 AmountInWei => AmountInGwei * 1.GWei();

    public override string ToString() => ToString(string.Empty);

    public string ToString(string indentation) => new StringBuilder($"{indentation}{nameof(Withdrawal)} {{")
        .Append($"{nameof(Index)}: {Index}, ")
        .Append($"{nameof(ValidatorIndex)}: {ValidatorIndex}, ")
        .Append($"{nameof(Address)}: {Address}, ")
        .Append($"{nameof(AmountInGwei)}: {AmountInGwei}}}")
        .ToString();
}
