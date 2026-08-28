// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Ethereum.Blockchain.Pyspec.Test;

public class Constants
{
    public const string ARCHIVE_URL_TEMPLATE = "https://github.com/ethereum/execution-specs/releases/download/{0}/{1}";
    public const string DEFAULT_ARCHIVE_VERSION = "tests-glamsterdam-devnet@v8.1.2";
    public const string DEFAULT_ARCHIVE_NAME = "fixtures_glamsterdam-devnet.tar.gz";

    // EIP-7805 fixtures ship as a separate archive from the default release.
    public const string FOCIL_ARCHIVE_VERSION = "tests-focil-devnet@v0.2.0";
    public const string FOCIL_ARCHIVE_NAME = "fixtures_focil-devnet.tar.gz";
}
