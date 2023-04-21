// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using NSubstitute;

namespace Nethermind.Core.Test;

public static class NullBlockFinder
{
    public static IBlockFinder Instance = Substitute.For<IBlockFinder>();
}
