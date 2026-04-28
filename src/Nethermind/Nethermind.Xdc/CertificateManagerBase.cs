// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Xdc;

public abstract class CertificateManagerBase
{
    protected static readonly EthereumEcdsa _ethereumEcdsa = new(0);

    /// <summary>
    /// Verifies each signature against <paramref name="allowedSigners"/>, deduplicates by
    /// recovered address, and returns the number of distinct valid signers.
    /// </summary>
    /// <returns>
    /// Signatures count if all are valid, or <c>null</c>
    /// if there's any validation <paramref name="error"/>.
    /// </returns>
    protected static int? CountValidSignatures(
        Address[] allowedSigners,
        Signature[] signatures,
        ValueHash256 messageHash,
        out string? error)
    {
        //Possible optimize here
        Dictionary<Address, int> signedBy = allowedSigners.ToDictionary(static a => a, static _ => 0);

        int count = 0;
        string? localError = null;
        Parallel.ForEach(signatures, (s, state) =>
        {
            Address signer = _ethereumEcdsa.RecoverAddress(s, messageHash);
            ref int signCount = ref CollectionsMarshal.GetValueRefOrNullRef(signedBy, signer);

            if (Unsafe.IsNullRef(ref signCount))
            {
                localError = "Certificate contains an invalid signature";
                state.Stop();
                return;
            }

            if (Interlocked.Increment(ref signCount) != 1)
            {
                localError = $"Certificate contains a duplicate signature from {signer}";
                state.Stop();
                return;
            }

            Interlocked.Increment(ref count);
        });

        error = localError;
        return error is null ? count : null;
    }
}
