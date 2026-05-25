// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Ethereum.Test.Base;

namespace Ethereum.Blockchain.Pyspec.Test;

public class Constants
{
    public const string ARCHIVE_URL_TEMPLATE = "https://github.com/ethereum/execution-specs/releases/download/{0}/{1}";
    public const string DEFAULT_ARCHIVE_VERSION = "tests-bal@v7.2.0";
    public const string DEFAULT_ARCHIVE_NAME = "fixtures_bal.tar.gz";

    public static LoadPyspecTestsStrategy Strategy => new()
    {
        ArchiveVersion = DEFAULT_ARCHIVE_VERSION,
        ArchiveName = DEFAULT_ARCHIVE_NAME
    };
}
