// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Nethermind.Core;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Prove;
using WirePostBatch = Nethermind.Eez.Prove.PostBatch;

namespace Nethermind.Eez.Proving;

/// <summary>One attester the composer asks to sign a window.</summary>
public interface IAttester
{
    Address ProofSystem { get; }

    Address Signer { get; }

    /// <returns>The attester's signature over the window's public inputs hash, as it came back: 65 bytes are not guaranteed.</returns>
    /// <exception cref="ProveException">The attester did not sign the window.</exception>
    Task<byte[]> Prove(ProveRequest request, CancellationToken token);
}

/// <summary>
/// An attester behind <c>prove.v1</c>: the window streams as a header with the batch calldata, then one witnessed block
/// per chunk. The channel is kept for the node's life. Requests carry whole witnesses, so their size cap is raised;
/// a response is a hash and a signature, so a larger one is refused.
/// </summary>
public sealed class RemoteAttester(GrpcChannel channel, Address proofSystem, Address signer) : IAttester, IDisposable
{
    private const int MaxRequestBytes = 1 << 30;
    private const int MaxResponseBytes = 4 * 1024;
    private const string StatusDetailsTrailer = "grpc-status-details-bin";

    public RemoteAttester(Uri endpoint, Address proofSystem, Address signer)
        : this(GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions { MaxSendMessageSize = MaxRequestBytes, MaxReceiveMessageSize = MaxResponseBytes }),
            proofSystem, signer)
    {
    }

    public Address ProofSystem => proofSystem;

    public Address Signer => signer;

    public async Task<byte[]> Prove(ProveRequest request, CancellationToken token)
    {
        Prover.ProverClient client = new(channel);
        try
        {
            using AsyncClientStreamingCall<ProveChunk, ProveResponse> call = client.Prove(cancellationToken: token);
            await call.RequestStream.WriteAsync(Header(request), token);
            foreach (ProvedBlock block in request.Blocks)
            {
                await call.RequestStream.WriteAsync(Chunk(block), token);
            }

            await call.RequestStream.CompleteAsync();
            ProveResponse response = await call.ResponseAsync;
            return response.Signature.ToByteArray();
        }
        catch (RpcException e)
        {
            throw Classify(e, request);
        }
    }

    private static ProveChunk Header(ProveRequest request) => new()
    {
        Header = new ProveHeader
        {
            RollupId = request.RollupId,
            FromBlock = request.FromBlock,
            ToBlock = request.ToBlock,
            PostBatch = new WirePostBatch { AbiCalldata = ByteString.CopyFrom(EezCalldata.EncodePostAndVerifyBatch(request.Batch)) },
        },
    };

    private static ProveChunk Chunk(ProvedBlock block)
    {
        ExecutionWitness witness = new();
        foreach (byte[] node in block.Witness.State)
        {
            witness.State.Add(UnsafeByteOperations.UnsafeWrap(node));
        }

        foreach (byte[] code in block.Witness.Codes)
        {
            witness.Codes.Add(UnsafeByteOperations.UnsafeWrap(code));
        }

        foreach (byte[] key in block.Witness.Keys)
        {
            witness.Keys.Add(UnsafeByteOperations.UnsafeWrap(key));
        }

        foreach (byte[] header in block.Witness.Headers)
        {
            witness.Headers.Add(UnsafeByteOperations.UnsafeWrap(header));
        }

        return new ProveChunk
        {
            Block = new BlockWitness
            {
                Number = block.Number,
                Hash = UnsafeByteOperations.UnsafeWrap(block.Hash.BytesToArray()),
                ParentHash = UnsafeByteOperations.UnsafeWrap(block.ParentHash.BytesToArray()),
                Rlp = UnsafeByteOperations.UnsafeWrap(block.Rlp),
                Witness = witness,
            },
        };
    }

    /// <summary>
    /// Unavailable, DeadlineExceeded and Aborted clear on their own. A refusal naming one effect lets the composer drop
    /// it; any other status refuses the batch as it is.
    /// </summary>
    private static ProveException Classify(RpcException e, ProveRequest request)
    {
        string message = $"Prove {request.FromBlock}-{request.ToBlock}: {e.StatusCode} {e.Status.Detail}";
        return e.StatusCode switch
        {
            StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.Aborted => new ProveException(ProveFailureKind.Retryable, message),
            StatusCode.FailedPrecondition when Refused(e.Trailers) is { } refused => new ProveException(ProveFailureKind.Actionable, message, refused),
            _ => new ProveException(ProveFailureKind.Backend, message),
        };
    }

    private static RefusedEffect? Refused(Metadata trailers)
    {
        byte[]? details = trailers.GetValueBytes(StatusDetailsTrailer);
        if (details is not { Length: > 0 })
        {
            return null;
        }

        ProveFailure failure;
        try
        {
            failure = ProveFailure.Parser.ParseFrom(details);
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }

        return failure.ActionableFailureCase switch
        {
            ProveFailure.ActionableFailureOneofCase.Outbound when failure.Outbound.TransactionHash.Length == 32 =>
                new RefusedEffect(true, failure.Outbound.TransactionIndex, new(failure.Outbound.TransactionHash.Span)),
            ProveFailure.ActionableFailureOneofCase.Inbound when failure.Inbound.EntryHash.Length == 32 =>
                new RefusedEffect(false, failure.Inbound.EntryIndex, new(failure.Inbound.EntryHash.Span)),
            _ => null,
        };
    }

    public void Dispose() => channel.Dispose();
}
