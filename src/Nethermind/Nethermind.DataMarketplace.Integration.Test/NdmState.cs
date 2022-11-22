// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Overseer.Test.Framework;

namespace Nethermind.DataMarketplace.Integration.Test
{
    public class NdmState : ITestState
    {
        public string DataAssetId { get; set; }
        public string DepositId { get; set; }
    }
}
