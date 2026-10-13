// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Grpc.Core;
using Nethermind.Eez.Prove.V2;

namespace Nethermind.Eez.Attester;

internal sealed class StreamingProverService(StreamingIdentity identity, StreamingTransport transport) : Prover.ProverBase
{
    public override Task ProveStream(IAsyncStreamReader<ClientFrame> requestStream, IServerStreamWriter<ServerFrame> responseStream,
        ServerCallContext context) => transport.Run(requestStream, responseStream, identity.Authenticate(context), context.CancellationToken);
}
