// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Prove.V2;

namespace Nethermind.Eez.Attester;

internal sealed class Rollup0ConditionalFinalizer(AttestationPipeline pipeline, EezAttestationSigner signer)
{
    internal async Task<Proof> Finalize(SessionOperation operation, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        Finalize finalization = operation.Finalize ?? throw new InvalidOperationException("not a finalization operation");
        StreamingFinalizer.ValidateSupportedSettlement(finalization.Settlement);
        StreamingFinalizer.ValidateProfile(finalization, pipeline.Context);
        using AdmittedWindow window = StreamingFinalizer.ToWindow(operation, finalization);
        ValueHash256 publicInputs = await Task.Run(() => pipeline.Validate(window, cancellation), CancellationToken.None).ConfigureAwait(false);
        return operation.SignCurrent(signer, publicInputs, cancellation);
    }
}
