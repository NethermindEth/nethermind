// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Google.Protobuf;
using Grpc.Core;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Eez.Attester.Rpc;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Logging;
using SettlementBatch = Nethermind.Eez.Execution.Settlement.PostBatch;

namespace Nethermind.Eez.Attester;

/// <summary>
/// Re-executes an admitted window, checks every settlement claim against it and signs the public inputs hash.
/// Failures surface as the gRPC statuses the reference signer returns, so a composer handles either signer alike.
/// </summary>
internal sealed class AttestationPipeline(ISpecProvider specProvider, EezSettlementContext context, EezAttestationSigner signer, ILogManager logManager)
{
    public const string WindowRejected = "window validation rejected";
    public const string SettlementRejected = "settlement validation rejected";

    private readonly SettlementCheck _check = new(specProvider, context, logManager);

    public EezSettlementContext Context => context;

    /// <exception cref="RpcException">The window or its batch cannot be attested.</exception>
    /// <exception cref="System.OperationCanceledException"><paramref name="cancellation"/> fired between stages.</exception>
    public ProveResponse Run(AdmittedWindow window, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        EezStatelessBlockResult[] executed;
        try
        {
            executed = _check.Execute(window.PostBatchCalldata, window.Blocks, window.Claims, window.FromBlock);
        }
        catch (EezStatelessException e)
        {
            throw e.Failure == EezStatelessFailure.Rejected ? Failure(StatusCode.FailedPrecondition, WindowRejected) : Failure(StatusCode.Internal, "request pipeline invariant failed");
        }
        catch (EezSettlementException)
        {
            throw Failure(StatusCode.FailedPrecondition, SettlementRejected);
        }

        cancellation.ThrowIfCancellationRequested();
        ValueHash256 publicInputsHash;
        try
        {
            publicInputsHash = _check.Verify(window.PostBatchCalldata, executed);
        }
        catch (EezSettlementException e)
        {
            throw ToStatus(e, window.PostBatchCalldata, executed[^1].Block);
        }

        cancellation.ThrowIfCancellationRequested();
        return new ProveResponse
        {
            PublicInputsHash = ByteString.CopyFrom(publicInputsHash.Bytes),
            Signature = ByteString.CopyFrom(signer.Sign(publicInputsHash)),
        };
    }

    internal static RpcException ToStatus(EezSettlementException e, byte[] calldata, Block settling) => e.Failure switch
    {
        EezSettlementFailure.InvalidPostBatch => Failure(StatusCode.InvalidArgument, "invalid PostBatch calldata"),
        EezSettlementFailure.InvalidDaPayload => Failure(StatusCode.InvalidArgument, "invalid batch callData"),
        EezSettlementFailure.InternalInvariant => Failure(StatusCode.Internal, "validation backend returned invalid output"),
        _ => Failure(StatusCode.FailedPrecondition, SettlementRejected, Actionable(e, calldata, settling)),
    };

    /// <summary>The effect a composer can evict and retry without, as the reference signer reports it.</summary>
    private static ProveFailure? Actionable(EezSettlementException e, byte[] calldata, Block settling)
    {
        if (e.PoisonedTransactionIndex is { } transaction && transaction < settling.Transactions.Length)
        {
            Transaction user = settling.Transactions[transaction];
            return new ProveFailure
            {
                Outbound = new OutboundFailure
                {
                    TransactionIndex = (uint)transaction,
                    TransactionHash = ByteString.CopyFrom(user.Hash!.Bytes),
                },
            };
        }

        if (e.PoisonedEntryIndex is { } entry)
        {
            SettlementBatch batch = EezCalldata.DecodePostAndVerifyBatch(calldata);
            return new ProveFailure
            {
                Inbound = new InboundFailure
                {
                    EntryIndex = (uint)entry,
                    EntryHash = ByteString.CopyFrom(EezCalldata.EntryHash(batch.Entries[entry]).Bytes),
                },
            };
        }

        return null;
    }

    public static RpcException Failure(StatusCode code, string message, ProveFailure? details = null)
    {
        Metadata trailers = [];
        if (details is not null)
        {
            trailers.Add("grpc-status-details-bin", details.ToByteArray());
        }

        return new RpcException(new Status(code, message), trailers);
    }
}
