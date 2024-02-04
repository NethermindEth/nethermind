// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json.Serialization;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Core;

/// <summary>
/// Represents a Deposit that has been validated at the consensus layer.
/// </summary>
public class Deposit
{
    public byte[]? PublicKey { get; set; }
    public byte[]? WithdrawalCredential { get; set; }
    public ulong Amount { get; set; }
    public byte[]? Signature { get; set; }
    public ulong Index { get; set; }
    public override string ToString() => ToString(string.Empty);

    public string ToString(string indentation) => new StringBuilder($"{indentation}{nameof(Deposit)} {{")
        .Append($"{nameof(Index)}: {Index}, ")
        .Append($"{nameof(WithdrawalCredential)}: {WithdrawalCredential?.ToHexString()}, ")
        .Append($"{nameof(Amount)}: {Amount}, ")
        .Append($"{nameof(PublicKey)}: {PublicKey?.ToHexString()}}}")
        .ToString();
}
