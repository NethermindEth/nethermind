// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Ethereum.Test.Base;

namespace Ethereum.Blockchain.Pyspec.Zkevm.Test;

internal static class Constants
{
    internal const string ArchiveVersion = "tests-zkevm@v0.4.1";
    internal const string ArchiveName = "fixtures_zkevm.tar.gz";

    internal static LoadPyspecTestsStrategy Strategy => new()
    {
        ArchiveVersion = ArchiveVersion,
        ArchiveName = ArchiveName
    };
}
