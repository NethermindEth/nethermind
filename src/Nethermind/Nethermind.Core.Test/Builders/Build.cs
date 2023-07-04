// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Test.Builders
{
    public partial class Build
    {
        private Build()
        {
        }

        public static Build A => new();
        public static Build An => new();
    }
}
