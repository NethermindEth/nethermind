// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

internal static class TestDirectories
{
    public static void TryDelete(string path)
    {
        if (!Directory.Exists(path)) return;

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception e)
        {
            TestContext.Out.WriteLine($"Failed to delete test directory {path}: {e.Message}");
        }
    }
}
