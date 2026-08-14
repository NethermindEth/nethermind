// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api.Extensions;

namespace Nethermind.Api.Test
{
    public class TestPlugin2 : INethermindPlugin
    {
        public string Name { get; }
        public string Description { get; }
        public string Author { get; }

        public bool Enabled => true;
    }
}
