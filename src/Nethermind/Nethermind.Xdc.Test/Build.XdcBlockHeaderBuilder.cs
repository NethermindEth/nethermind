// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Test.Builders;

public static class BuildExtentions
{
    public static XdcBlockHeaderBuilder XdcBlockHeader(this Build build)
    {
        return new XdcBlockHeaderBuilder();
    }
}
