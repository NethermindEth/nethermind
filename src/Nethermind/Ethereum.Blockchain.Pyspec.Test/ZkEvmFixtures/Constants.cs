// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Ethereum.Blockchain.Pyspec.Test.ZkEvmFixtures;

// zkEVM fixtures moved to ethereum/execution-specs after v0.4.0; the loader's default
// ARCHIVE_URL_TEMPLATE already points there, so only the version/name need overriding.
public static class Constants
{
    public const string ArchiveVersion = "tests-zkevm@v0.6.0";
    public const string ArchiveName = "fixtures_zkevm.tar.gz";
}
