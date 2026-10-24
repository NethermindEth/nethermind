// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Ethereum.Blockchain.Pyspec.Test;

public class Constants
{
    public const string ARCHIVE_URL_TEMPLATE = "https://github.com/ethereum/execution-specs/releases/download/{0}/{1}";
    public const string DEFAULT_ARCHIVE_VERSION = "tests@v21.0.1";
    public const string DEFAULT_ARCHIVE_NAME = "fixtures.tar.gz";

    // Heze EIP-7805 and EIP-8198 fixtures ship separately from the default release.
    public const string FOCIL_ARCHIVE_VERSION = "tests-heze-devnet@v2.0.0";
    public const string FOCIL_ARCHIVE_NAME = "fixtures_heze-devnet.tar.gz";
}
