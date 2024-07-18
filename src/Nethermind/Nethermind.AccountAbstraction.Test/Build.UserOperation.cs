// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.AccountAbstraction.Test
{
    public class Build
    {
        private Build()
        {
        }

        public static Build A => new Build();
        public static Build An => new Build();

        public UserOperationBuilder UserOperation => new UserOperationBuilder();
    }
}
