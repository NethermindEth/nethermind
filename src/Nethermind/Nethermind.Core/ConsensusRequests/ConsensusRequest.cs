// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


namespace Nethermind.Core.ConsensusRequests;

public enum RequestsType
{
    Deposit = 0,
    WithdrawalRequest = 1
}

public class ConsensusRequest
{
    public RequestsType Type { get; protected set; }
    public ulong AmountField { get; protected set; }
    public Address? SourceAddressField { get; protected set; }
    public byte[]? ValidatorPubkeyField { get; protected set; }
    public byte[]? PubKeyField { get; set; }
    public byte[]? WithdrawalCredentialsField { get; protected set; }
    public byte[]? SignatureField { get; protected set; }
    public ulong? IndexField { get; protected set; }
}
