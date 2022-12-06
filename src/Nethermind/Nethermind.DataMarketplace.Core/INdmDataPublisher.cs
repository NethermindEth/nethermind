// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Core
{
    public interface INdmDataPublisher
    {
        EventHandler<NdmDataEventArgs>? DataPublished { get; set; }
        void Publish(DataAssetData dataAssetData);
    }
}
