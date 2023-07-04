// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;

namespace Nethermind.Core.Test.Builders
{
    [DebuggerDisplay(nameof(Name))]
    public class NamedTransaction : Transaction
    {
        public string Name { get; set; } = null!;

        public override string ToString() => Name;
    }
}
