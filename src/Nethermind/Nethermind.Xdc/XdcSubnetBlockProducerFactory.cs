// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;

namespace Nethermind.Xdc;

internal sealed class XdcSubnetBlockProducerFactory(StartXdcSubnetBlockProducer starter) : IBlockProducerFactory
{
    public IBlockProducer InitBlockProducer() => starter.BuildProducer();
}
