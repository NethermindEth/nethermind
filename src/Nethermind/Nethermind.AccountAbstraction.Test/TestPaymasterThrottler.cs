// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.AccountAbstraction.Source;
using Nethermind.Core;

namespace Nethermind.AccountAbstraction.Test
{
    public class TestPaymasterThrottler : PaymasterThrottler
    {
        public new void UpdateUserOperationMaps(Object source, EventArgs args)
        {
            base.UpdateUserOperationMaps(source, args);
        }
    }
}
