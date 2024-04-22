// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using System.Text.Json.Serialization;

namespace Nethermind.Core.ConsensusRequests;

public enum RequestsType
{
    Deposit = 0,
    WithdrawalRequest = 1
}

public class ConsensusRequest
{
    [JsonIgnore]
    public RequestsType Type { get; protected set; }

    [JsonIgnore]
    public ulong AmountField { get; protected set; }

    [JsonIgnore]
    public Address? SourceAddressField { get; protected set; }

    [JsonIgnore]
    public byte[]? PubKeyField { get; set; }

    [JsonIgnore]
    public byte[]? WithdrawalCredentialsField { get; protected set; }

    [JsonIgnore]
    public byte[]? SignatureField { get; protected set; }

    [JsonIgnore]
    public ulong? IndexField { get; protected set; }
}
