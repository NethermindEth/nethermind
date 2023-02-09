// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Core2
{
    public interface IForkChoice
    {
        Task<Root> GetAncestorAsync(IStore store, Root root, Slot slot);
        Task<Root> GetHeadAsync(IStore store);
        Task InitializeForkChoiceStoreAsync(IStore store, BeaconState anchorState);
        Task OnAttestationAsync(IStore store, Attestation attestation);
        Task OnBlockAsync(IStore store, SignedBeaconBlock signedBlock);
        Task OnTickAsync(IStore store, ulong time);
    }
}
