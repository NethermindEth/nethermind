// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;

namespace Nethermind.Core;

public static class OwnerOnlyFile
{
    /// <summary>
    /// Creates options that open a file for exclusive writing, creating or truncating it.
    /// On Unix, a newly created file gets mode <c>0600</c> (owner read/write), the mode geth uses for its generated JWT secret.
    /// The mode is applied at creation, so the file never exists with a wider mode; an existing file keeps its mode.
    /// </summary>
    public static FileStreamOptions CreateWriteOptions()
    {
        FileStreamOptions options = new() { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return options;
    }
}
