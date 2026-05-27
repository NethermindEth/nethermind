// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text;
using System.Text.Json.Serialization;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Core;

/// <summary>
/// Represents a withdrawal that has been validated at the consensus layer.
/// </summary>
public class Withdrawal : IEquatable<Withdrawal>
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
    [JsonPropertyName("amount")]
    public ulong AmountInGwei { get; set; }

    [JsonIgnore]
    public UInt256 AmountInWei => AmountInGwei * 1.GWei;

    public bool Equals(Withdrawal? other) =>
        ReferenceEquals(this, other) ||
        other is not null &&
        Index == other.Index &&
        ValidatorIndex == other.ValidatorIndex &&
        Address == other.Address &&
        AmountInGwei == other.AmountInGwei;

    public override bool Equals(object? obj) => obj is Withdrawal other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Index, ValidatorIndex, Address, AmountInGwei);

    public override string ToString() => ToString(string.Empty);

    public string ToString(string indentation) => new StringBuilder($"{indentation}{nameof(Withdrawal)} {{")
        .Append($"{nameof(Index)}: {Index}, ")
        .Append($"{nameof(ValidatorIndex)}: {ValidatorIndex}, ")
        .Append($"{nameof(Address)}: {Address}, ")
        .Append($"{nameof(AmountInGwei)}: {AmountInGwei}}}")
        .ToString();
}
