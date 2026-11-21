// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Grpc.Core;
using Nethermind.Eez.Prove.V2;

namespace Nethermind.Eez.Attester;

internal sealed class StreamingProverService(StreamingIdentity identity, StreamingTransport transport, StreamingCapabilities? capabilities = null) : Prover.ProverBase
{
    public override Task<CapabilitiesResponse> Capabilities(CapabilitiesRequest request, ServerCallContext context)
    {
        _ = identity.Authenticate(context);
        return Task.FromResult(capabilities?.Read() ?? throw new RpcException(new Status(StatusCode.Unimplemented, "capabilities unavailable")));
    }

    public override Task ProveStream(IAsyncStreamReader<ClientFrame> requestStream, IServerStreamWriter<ServerFrame> responseStream,
        ServerCallContext context)
    {
        string owner = identity.Authenticate(context);
        StreamingIdentity.RequireMatchingValidationIdentity(context.RequestHeaders, capabilities?.RequiredStreamIdentity);
        return transport.Run(requestStream, responseStream, owner, context.CancellationToken,
            context.Method == StreamingServiceMethods.Rollup0Path ? "prove.v1" : "prove.v2");
    }
}
