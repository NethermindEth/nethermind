// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm;

public class AccountOverride
{
    public ulong? Nonce { get; set; }
    public UInt256? Balance { get; set; }

    /// <remarks>
    /// Read from JSON, the array may be shared with other requests that sent the same text, so it must not be modified.
    /// </remarks>
    [JsonConverter(typeof(OverrideCodeJsonConverter))]
    public byte[]? Code { get; set; }
    public Address? MovePrecompileToAddress { get; set; }

    /// <summary>
    ///     Storage for AccountOverrideState
    /// </summary>
    public Dictionary<UInt256, Hash256>? State { get; set; }

    /// <summary>
    ///     Storage difference for AccountOverrideStateDiff
    /// </summary>
    public Dictionary<UInt256, Hash256>? StateDiff { get; set; }

    /// <summary>
    /// Returns <see langword="true"/> if any account-state field is set (balance, nonce, code, state, or stateDiff).
    /// </summary>
    public bool HasStateChanges => Balance is not null || Nonce is not null || Code is not null || State is not null || StateDiff is not null;
}
