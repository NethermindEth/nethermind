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
using Nethermind.Serialization.Rlp;
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

    private readonly EezStatelessExecutor _executor = new(specProvider, logManager);

    public EezSettlementContext Context => context;

    /// <exception cref="RpcException">The window or its batch cannot be attested.</exception>
    /// <exception cref="System.OperationCanceledException"><paramref name="cancellation"/> fired between stages.</exception>
    public ProveResponse Run(AdmittedWindow window, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        int[] checkpoints = SettlingBlock.EffectTransactionsOf(DecodeSettlingBlock(window));
        EnsureEffectCountCanMatch(window.PostBatchCalldata, checkpoints.Length);

        EezStatelessBlockResult[] executed;
        try
        {
            executed = _executor.Execute(window.Blocks, checkpoints);
        }
        catch (EezStatelessException e)
        {
            throw e.Failure == EezStatelessFailure.Rejected ? Failure(StatusCode.FailedPrecondition, WindowRejected) : Failure(StatusCode.Internal, "request pipeline invariant failed");
        }

        EnsureClaimsMatch(window, executed);
        cancellation.ThrowIfCancellationRequested();

        ValueHash256 publicInputsHash;
        try
        {
            publicInputsHash = EezSettlementVerifier.Verify(window.PostBatchCalldata, executed, context, specProvider);
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

    private static Block DecodeSettlingBlock(AdmittedWindow window)
    {
        try
        {
            return Rlp.Decode<Block>(window.Blocks[^1].Rlp) ?? throw Failure(StatusCode.FailedPrecondition, WindowRejected);
        }
        catch (RlpException)
        {
            throw Failure(StatusCode.FailedPrecondition, WindowRejected);
        }
    }

    /// <summary>
    /// Refuses a batch that claims fewer or more effects than the settling block can hold before re-executing it,
    /// because every checkpoint of a padded settling block re-hashes the whole transaction prefix.
    /// </summary>
    private static void EnsureEffectCountCanMatch(byte[] calldata, int effectTransactions)
    {
        SettlementBatch batch;
        try
        {
            batch = EezCalldata.DecodePostAndVerifyBatch(calldata);
        }
        catch (EezAbiException)
        {
            return;
        }

        if (batch.Entries.Length - 1 != effectTransactions)
        {
            throw Failure(StatusCode.FailedPrecondition, SettlementRejected);
        }
    }

    private static void EnsureClaimsMatch(AdmittedWindow window, EezStatelessBlockResult[] executed)
    {
        for (int i = 0; i < executed.Length; i++)
        {
            BlockHeader header = executed[i].Block.Header;
            if (header.Number != window.FromBlock + (ulong)i || header.Hash != window.Claims[i].Hash || header.ParentHash != window.Claims[i].ParentHash)
            {
                throw Failure(StatusCode.FailedPrecondition, WindowRejected);
            }
        }
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
