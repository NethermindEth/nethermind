// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Core.Services
{
    public class NdmDataPublisher : INdmDataPublisher
    {
        public EventHandler<NdmDataEventArgs>? DataPublished { get; set; }

        public void Publish(DataAssetData dataAssetData)
        {
            DataPublished?.Invoke(this, new NdmDataEventArgs(dataAssetData));
        }
    }
}
