// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Google.Protobuf;
using Nethermind.Eez.Prove;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Eez.Prove.V2;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Eez.Attester;

internal sealed class RollupXStreamingFinalizer
{
    private readonly ISpecProvider _l1Specs;
    private readonly EezChainRules _rules;
    private readonly EezSettlementContext _context;
    private readonly EezAttestationSigner _signer;
    private readonly RollupXParentAuthorizer _authorizer;
    private readonly RollupXSettlementExecution _execution;

    internal RollupXStreamingFinalizer(ISpecProvider l1Specs, ISpecProvider l2Specs, EezChainRules rules,
        EezSettlementContext context, EezAttestationSigner signer, RollupXParentAuthorizer authorizer, ILogManager logs)
    {
        if (rules.Schedule is not { } schedule || schedule.L1Reference.ChainId != l1Specs.ChainId || context.ChainId != l2Specs.ChainId)
            throw new ArgumentException("finalization requires matching RollupX chain identities");
        _l1Specs = l1Specs;
        _rules = rules;
        _context = context;
        _signer = signer;
        _authorizer = authorizer;
        _execution = new RollupXSettlementExecution(l2Specs, rules, context, logs);
    }

    internal Task<PreparedOrdinaryBlock> Prepare(EezStatelessBlock block, CancellationToken cancellation) =>
        Task.Run(() => _execution.Prepare(block, cancellation), CancellationToken.None);

    internal async Task<Proof> Finalize(SessionOperation operation, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        Finalize selection = operation.Finalize ?? throw new InvalidOperationException("not a finalization operation");
        ValidateSupportedSettlement(selection.Settlement);
        using AdmittedWindow window = StreamingFinalizer.ToWindow(operation, selection);
        ValueHash256 publicInputs = await ValidateAndRecheck(window, selection.Settlement, operation.Predecessor!, null, cancellation).ConfigureAwait(false);
        return operation.SignCurrent(_signer, publicInputs, cancellation);
    }

    internal async Task<ProveResponse> Prove(AdmittedWindow window, SettlementInput settlement, Begin predecessor,
        ParentProvenance provenance, CancellationToken cancellation)
    {
        ValueHash256 publicInputs = await ValidateAndRecheck(window, settlement, predecessor, provenance, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        return new ProveResponse
        {
            PublicInputsHash = ByteString.CopyFrom(publicInputs.Bytes),
            Signature = ByteString.CopyFrom(_signer.Sign(publicInputs)),
        };
    }

    private async Task<ValueHash256> ValidateAndRecheck(AdmittedWindow window, SettlementInput settlement, Begin predecessor,
        ParentProvenance? provenance, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        ValidateSupportedSettlement(settlement);
        RollupXSettlementInput input = RollupXSettlementInput.Decode(settlement, _context, _l1Specs, cancellation);
        RollupXAuthorization authorization = await _authorizer.Authorize(predecessor, settlement.PostingContext, cancellation, provenance).ConfigureAwait(false);
        if (authorization.Context != _context || authorization.Signer != _signer.Address ||
            authorization.Target != input.Target || authorization.L1ParentNumber != input.L1ParentNumber)
            throw RollupXAuthorizationEvidence.Rejected();
        ValueHash256 publicInputs = await Task.Run(() => Validate(window, input, authorization, cancellation), CancellationToken.None).ConfigureAwait(false);
        await _authorizer.Recheck(authorization, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        return publicInputs;
    }

    internal static void ValidateSupportedSettlement(SettlementInput? settlement)
    {
        if (settlement is null || settlement.PostingContext is null || settlement.Blobs.Count == 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "RollupX settlement input missing"));
    }

    private ValueHash256 Validate(AdmittedWindow window, RollupXSettlementInput input, RollupXAuthorization authorization,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        try
        {
            if (window.Blocks.Length == 0 || window.Claims.Length == 0)
                throw new EezSettlementException("Finalization has no exact claimed execution window.");
            BlockHeader parent = Rlp.Decode<BlockHeader>(authorization.ParentHeader.ToByteArray())
                ?? throw new EezSettlementException("Authorized predecessor header is missing.");
            ValidateRange(window, input, parent);
            return _execution.Validate(window, input, parent, authorization.DescendantSeed, authorization.RegistryRoot, cancellation);
        }
        catch (Exception exception) when (exception is EezSettlementException or EezStatelessException or WitnessValidationException or RlpException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "RollupX settlement validation rejected"));
        }
    }

    private void ValidateRange(AdmittedWindow window, RollupXSettlementInput input, BlockHeader parent)
    {
        RollupXSchedule schedule = _rules.Schedule!;
        if (parent.Number == ulong.MaxValue || window.FromBlock != parent.Number + 1 || window.ToBlock < window.FromBlock ||
            window.ToBlock - window.FromBlock + 1 != (ulong)window.Claims.Length || !schedule.IsSyncPosition(parent.Number) ||
            schedule.AnchorKind(window.ToBlock, input.Target.Timestamp) is not { } kind)
            throw new EezSettlementException("Settlement range is outside the authenticated Sync grid.");
        if (kind == RollupXAnchorKind.CatchUp && input.Stream.Messages.Length != 0)
            throw new EezSettlementException("Catch-up settlement carries cross-chain actions.");
    }
}
