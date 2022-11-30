// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;

namespace Nethermind.BeaconNode.Services
{
    public interface IOperationPool
    {
        IAsyncEnumerable<Attestation> GetAttestationsAsync(ulong maximum);
        IAsyncEnumerable<AttesterSlashing> GetAttesterSlashingsAsync(ulong maximum);
        IAsyncEnumerable<ProposerSlashing> GetProposerSlashingsAsync(ulong maximum);
        IAsyncEnumerable<SignedVoluntaryExit> GetSignedVoluntaryExits(ulong maximum);
    }
}
