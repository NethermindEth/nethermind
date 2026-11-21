// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Google.Protobuf;
using Grpc.AspNetCore.Server.Model;
using Grpc.Core;
using Nethermind.Eez.Prove.V2;

namespace Nethermind.Eez.Attester;

internal sealed class StreamingServiceMethods : IServiceMethodProvider<StreamingProverService>
{
    internal const string Rollup0Path = "/prove.v1.Prover/ProveStream";
    private static readonly Method<ClientFrame, ServerFrame> Rollup0 = new(MethodType.DuplexStreaming,
        "prove.v1.Prover", "ProveStream", CreateMarshaller(ClientFrame.Parser), CreateMarshaller(ServerFrame.Parser));

    /// <inheritdoc/>
    public void OnServiceMethodDiscovery(ServiceMethodProviderContext<StreamingProverService> context) =>
        context.AddDuplexStreamingMethod(Rollup0, [], static (service, input, output, call) => service.ProveStream(input, output, call));

    private static Marshaller<T> CreateMarshaller<T>(MessageParser<T> parser) where T : class, IMessage<T> =>
        Marshallers.Create<T>(static (message, context) =>
        {
            context.SetPayloadLength(message.CalculateSize());
            message.WriteTo(context.GetBufferWriter());
            context.Complete();
        }, context => parser.ParseFrom(context.PayloadAsReadOnlySequence()));
}
