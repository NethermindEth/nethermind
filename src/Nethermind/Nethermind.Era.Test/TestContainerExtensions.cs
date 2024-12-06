// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core.Test.IO;

namespace Nethermind.Era1.Test;

public static class TestContainerExtensions
{
    public static string ResolveTempFilePath(this IContainer container)
    {
        return container.ResolveNamed<TempPath>("file").Path;
    }

    public static string ResolveTempDirPath(this IContainer container)
    {
        return container.ResolveNamed<TempPath>("directory").Path;
    }
}
