// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Era1.Test;

public class TmpDirectory: IDisposable
{
    public string DirectoryPath { get; }

    public TmpDirectory()
    {
        DirectoryPath = Path.Join(Path.GetTempPath(), "nethermind_test", Random.Shared.Next().ToString());
    }

    public void Dispose()
    {
        Directory.Delete(DirectoryPath, true);
    }
}
