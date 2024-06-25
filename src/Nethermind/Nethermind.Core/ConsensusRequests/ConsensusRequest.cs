// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using System.Text.Json.Serialization;

namespace Nethermind.Core.ConsensusRequests;

public enum ConsensusRequestsType : byte
{
    Deposit = 0,
    WithdrawalRequest = 1,
    ConsolidationRequest = 2
}

public class ConsensusRequest
{
    [JsonIgnore]
    public ConsensusRequestsType Type { get; protected set; }

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

public static class ConsensusRequestExtensions
{
    public static (int depositCount, int withdrawalRequestCount, int consolidationRequestCount) GetTypeCounts(this ConsensusRequest[]? requests)
    {
        int depositCount = 0;
        int withdrawalRequestCount = 0;
        int consolidationRequestCount = 0;
        int length = requests?.Length ?? 0;
        for (int i = 0; i < length; i++)
        {
            if (requests![i].Type == ConsensusRequestsType.Deposit)
            {
                depositCount++;
            }
            else if (requests[i].Type == ConsensusRequestsType.WithdrawalRequest)
            {
                withdrawalRequestCount++;
            }
            else
            {
                consolidationRequestCount++;
            }
        }

        return (depositCount, withdrawalRequestCount, consolidationRequestCount);
    }

    public static (Deposit[]? deposits, WithdrawalRequest[]? withdrawalRequests, ConsolidationRequest[]? consolidationRequests) SplitRequests(this ConsensusRequest[]? requests)
    {
        if (requests is null) return (null, null, null);
        (int depositCount, int withdrawalRequestCount, int consolidationRequestCount) = requests.GetTypeCounts();
        Deposit[]? deposits = new Deposit[depositCount];
        WithdrawalRequest[]? withdrawalRequests = new WithdrawalRequest[withdrawalRequestCount];
        ConsolidationRequest[]? consolidationRequests = new ConsolidationRequest[consolidationRequestCount];
        int depositIndex = 0;
        int withdrawalRequestIndex = 0;
        int consolidationRequestIndex = 0;
        for (int i = 0; i < requests.Length; i++)
        {
            if (requests[i].Type == ConsensusRequestsType.Deposit)
            {
                deposits[depositIndex++] = (Deposit)requests[i];
            }
            else if (requests[i].Type == ConsensusRequestsType.WithdrawalRequest)
            {
                withdrawalRequests[withdrawalRequestIndex++] = (WithdrawalRequest)requests[i];
            }
            else
            {
                consolidationRequests[consolidationRequestIndex++] = (ConsolidationRequest)requests[i];
            }
        }

        return (deposits, withdrawalRequests, consolidationRequests);
    }
}
