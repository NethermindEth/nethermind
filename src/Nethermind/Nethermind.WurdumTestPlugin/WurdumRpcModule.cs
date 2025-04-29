// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
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
        IsSharable = false,
        IsImplemented = true)]
    Task<ResultWrapper<WurdumMessageResult>> arbitrum_digestMessage(MessageParams parameters);
}

public class WurdumRpcModuleFactory(IManualBlockProductionTrigger trigger, ChainSpec chainSpec, ILogger logger) : IRpcModuleFactory<IWurdumRpcModule>
{
    public IWurdumRpcModule Create() => new WurdumRpcModule(trigger, chainSpec, logger);
}

public record WurdumMessageResult(Hash256 BlockHash, Hash256 SendRoot); // SendRoot is a merkle root of states sent to L1

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
    [property: JsonPropertyName("l2Msg")] string L2Msg,
    [property: JsonPropertyName("batchGasCost")] ulong BatchGasCost
);

public record L1IncomingMessageHeader(
    [property: JsonPropertyName("kind")] ArbitrumL1MessageKind Kind,
    [property: JsonPropertyName("sender")] Address Sender,
    [property: JsonPropertyName("blockNumber")] ulong BlockNumber,
    [property: JsonPropertyName("timestamp")] ulong Timestamp,
    [property: JsonPropertyName("requestId")] Hash256? RequestId,
    [property: JsonPropertyName("baseFeeL1")] UInt256 BaseFeeL1
);

public class WurdumRpcModule(IManualBlockProductionTrigger trigger, ChainSpec chainSpec, ILogger logger) : IWurdumRpcModule
{
    public async Task<ResultWrapper<WurdumMessageResult>> arbitrum_digestMessage(MessageParams parameters)
    {
        var transactions = L2MessageParser.ParseL2Transactions(parameters.Message.Message, chainSpec.ChainId, logger);
        _ = transactions;

        var block = await trigger.BuildBlock();
        return block is null
            ? ResultWrapper<WurdumMessageResult>.Fail("Failed to build block", ErrorCodes.InternalError)
            : ResultWrapper<WurdumMessageResult>.Success(new(block.Hash!, Hash256.Zero));
    }
}
