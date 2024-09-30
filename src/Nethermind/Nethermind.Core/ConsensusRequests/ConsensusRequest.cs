// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using System;
using System.Text.Json.Serialization;


namespace Nethermind.Core.ConsensusRequests;

public enum ConsensusRequestsType : byte
{
    Deposit = 0,
    WithdrawalRequest = 1,
    ConsolidationRequest = 2
}

public abstract class ConsensusRequest
{
    [JsonIgnore]
    public ConsensusRequestsType Type { get; protected set; }

    /// <summary>
    /// Encodes the request into a byte array
    /// reference: https://eips.ethereum.org/EIPS/eip-7685
    /// </summary>
    /// <returns> request = request_type ++ request_data </returns>
    public abstract byte[] Encode();

    /// <summary>
    /// Decodes the request from a byte array
    /// reference: https://eips.ethereum.org/EIPS/eip-7685
    /// </summary>
    /// <param name="data"> request = request_type ++ request_data </param>
    /// <returns> request </returns>
    public abstract ConsensusRequest Decode(byte[] data);
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
        Deposit[] deposits = new Deposit[depositCount];
        WithdrawalRequest[] withdrawalRequests = new WithdrawalRequest[withdrawalRequestCount];
        ConsolidationRequest[] consolidationRequests = new ConsolidationRequest[consolidationRequestCount];
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

    public static byte[][] Encode(this ConsensusRequest[]? requests)
    {
        if (requests is null) return Array.Empty<byte[]>();
        byte[][] requestsEncoded = new byte[requests.Length][];
        for (int i = 0; i < requests.Length; i++)
        {
            requestsEncoded[i] = requests[i].Encode();
        }
        return requestsEncoded;
    }

    public static ConsensusRequest Decode(byte[] data)
    {
        if (data.Length < 2)
        {
            throw new ArgumentException("Invalid data length");
        }

        ConsensusRequestsType type = (ConsensusRequestsType)data[0];
        return type switch
        {
            ConsensusRequestsType.Deposit => new Deposit().Decode(data),
            ConsensusRequestsType.WithdrawalRequest => new WithdrawalRequest().Decode(data),
            ConsensusRequestsType.ConsolidationRequest => new ConsolidationRequest().Decode(data),
            _ => throw new ArgumentException("Invalid request type")
        };
    }

}
