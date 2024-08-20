// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Init.Steps;

namespace Nethermind.Taiko;

public class InitializeBlockProducerTaiko(TaikoNethermindApi api) : InitializeBlockProducer(api)
{
    private readonly TaikoNethermindApi _api = api;

    protected override IBlockProducer BuildProducer()
    {
        // This method should not be called. Taiko block production works differently from traditional ethereum clients.
        // Refer to the Taiko documentation for more information regarding the block production process.
        // Throw an exception to detect if this method is called.
        throw new InvalidOperationException("Taiko block production should not be initialized.");
    }
}
