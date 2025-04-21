// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.WurdumTestPlugin;

[RpcModule("Wurdum")]
public interface IWurdumRpcModule : IRpcModule
{
    [JsonRpcMethod(
        Description = "Test RPC endpoint from Wurdum Plugin.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<WurdumMessageResult>> arbitrum_digestMessage(MessageParams parameters);
}

public record WurdumMessageResult(Hash256 BlockHash, Hash256 SendRoot); // SendRoot is a merkle root of states sent to L1

public enum L1MessageType : byte
{
    L2Message = 3,
    EndOfBlock = 6,
    L2FundedByL1 = 7,
    RollupEvent = 8,
    SubmitRetryable = 9,
    BatchForGasEstimation = 10,
    Initialize = 11,
    EthDeposit = 12,
    BatchPostingReport = 13,
    Invalid = 255
}

public record MessageParams(
    [property: JsonPropertyName("number")] ulong Number,
    [property: JsonPropertyName("message")] MessageWithMetadata Message,
    [property: JsonPropertyName("messageForPrefetch")] MessageWithMetadata? MessageForPrefetch
);

public record MessageWithMetadata(
    [property: JsonPropertyName("message")] L1IncomingMessage Message,
    [property: JsonPropertyName("delayedMessagesRead")] ulong DelayedMessagesRead
);

public record L1IncomingMessage(
    [property: JsonPropertyName("header")] L1IncomingMessageHeader Header,
    [property: JsonPropertyName("l2Msg")] ReadOnlyMemory<byte> L2Msg,
    [property: JsonPropertyName("batchGasCost")] ulong BatchGasCost
);

public record L1IncomingMessageHeader(
    [property: JsonPropertyName("kind")] L1MessageType Kind,
    [property: JsonPropertyName("sender")] Address Sender,
    [property: JsonPropertyName("blockNumber")] ulong BlockNumber,
    [property: JsonPropertyName("timestamp")] ulong Timestamp,
    [property: JsonPropertyName("requestId")] Hash256? RequestId,
    [property: JsonPropertyName("baseFeeL1")] BigInteger BaseFeeL1
);

public class WurdumRpcModule(IManualBlockProductionTrigger trigger, ChainSpec chainSpec, ILogger logger) : IWurdumRpcModule
{
    public async Task<ResultWrapper<WurdumMessageResult>> arbitrum_digestMessage(MessageParams parameters)
    {
        var block = await trigger.BuildBlock();
        return block is null
            ? ResultWrapper<WurdumMessageResult>.Fail("Failed to build block", ErrorCodes.InternalError)
            : ResultWrapper<WurdumMessageResult>.Success(new(block.Hash!, Hash256.Zero));
    }
}
