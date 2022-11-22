// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.DataAssets.Domain;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Consumers.DataAssets
{
    public interface IDataAssetService
    {
        bool IsAvailable(DataAsset dataAsset);
        DataAsset? GetDiscovered(Keccak dataAssetId);
        IReadOnlyList<DataAsset> GetAllDiscovered();
        Task<IReadOnlyList<DataAssetInfo>> GetAllKnownAsync();
        void AddDiscovered(DataAsset dataAsset, INdmPeer peer);
        void AddDiscovered(DataAsset[] dataAssets, INdmPeer peer);
        void ChangeState(Keccak dataAssetId, DataAssetState state);
        void RemoveDiscovered(Keccak dataAssetId);
    }
}
