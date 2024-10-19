// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Era1.Test;

public class TmpFile: IDisposable
{
    public string FilePath { get; }

    public TmpFile()
    {
        FilePath = Path.GetTempFileName();
    }

    public void Dispose()
    {
        File.Delete(FilePath);
    }
}
