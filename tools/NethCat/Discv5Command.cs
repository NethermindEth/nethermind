// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;
using NethCat.Discv5;

namespace NethCat;

internal static class Discv5Command
{
    public static void Setup(RootCommand root)
    {
        Command discv5Command = new("discv5")
        {
            Description = "Discovery v5 protocol utilities"
        };

        DiscoverCommand.Setup(discv5Command);
        ConnectCommand.Setup(discv5Command);

        root.Add(discv5Command);
    }
}

