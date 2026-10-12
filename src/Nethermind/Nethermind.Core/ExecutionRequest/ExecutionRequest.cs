// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using Nethermind.Core.Extensions;

namespace Nethermind.Core.ExecutionRequest;

public enum ExecutionRequestType : byte
{
    Deposit = 0,
    WithdrawalRequest = 1,
    ConsolidationRequest = 2,
    BuilderDepositRequest = 3, // EIP-8282
    BuilderExitRequest = 4 // EIP-8282
}

public class ExecutionRequest
{
    public byte RequestType { get; set; }
    public byte[]? RequestData { get; set; }

    public override string ToString() => ToString(string.Empty);

    public string ToString(string indentation) => @$"{indentation}{nameof(ExecutionRequest)}
            {{{nameof(RequestType)}: {RequestType},
            {nameof(RequestData)}: {RequestData!.ToHexString()}}}";
}
