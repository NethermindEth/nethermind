// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Nethermind.Int256;

namespace Nethermind.Core;

/// <summary>
/// Represents a withdrawal that has been validated at the consensus layer.
/// </summary>
public interface IWithdrawal
{
    /// <summary>
    /// Gets the withdrawal unique id.
    /// </summary>
    public ulong Index { get; }

    /// <summary>
    /// Gets the validator index on the consensus layer the withdrawal corresponds to.
    /// </summary>
    public ulong ValidatorIndex { get; }

    /// <summary>
    /// Gets the withdrawal address.
    /// </summary>
    public Address Address { get; }

    /// <summary>
    /// Gets the withdrawal amount in Wei.
    /// </summary>
    public UInt256 Amount { get; }

    string ToString(string indentation);
}
