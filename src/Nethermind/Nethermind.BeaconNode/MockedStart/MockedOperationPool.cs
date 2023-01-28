// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.BeaconNode.Services;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;

namespace Nethermind.BeaconNode.MockedStart
{
    public class MockedOperationPool : IOperationPool
    {
        public async IAsyncEnumerable<Attestation> GetAttestationsAsync(ulong maximum)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<AttesterSlashing> GetAttesterSlashingsAsync(ulong maximum)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<ProposerSlashing> GetProposerSlashingsAsync(ulong maximum)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<SignedVoluntaryExit> GetSignedVoluntaryExits(ulong maximum)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
