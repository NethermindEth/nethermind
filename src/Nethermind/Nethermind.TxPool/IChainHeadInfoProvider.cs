// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.TxPool
{
    public interface IChainHeadInfoProvider
    {
        IChainHeadSpecProvider SpecProvider { get; }

        IAccountStateProvider AccountStateProvider { get; }

        public long? BlockGasLimit { get; }

        public UInt256 CurrentBaseFee { get; }

        public UInt256 CurrentPricePerBlobGas { get; }

        event EventHandler<BlockReplacementEventArgs> HeadChanged;
    }
}
