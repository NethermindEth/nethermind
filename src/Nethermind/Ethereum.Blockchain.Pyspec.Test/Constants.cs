// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Ethereum.Blockchain.Pyspec.Test;

public class Constants
{
    public const string ARCHIVE_URL_TEMPLATE = "https://github.com/ethereum/execution-specs/releases/download/{0}/{1}";
    // Must stay on the devnet line the zkEVM release in ZkEvmFixtures.Constants was filled from,
    // or the two sets disagree on the EIP-8038 gas parameters.
    public const string DEFAULT_ARCHIVE_VERSION = "tests-glamsterdam-devnet@v8.1.0";
    public const string DEFAULT_ARCHIVE_NAME = "fixtures_glamsterdam-devnet.tar.gz";
}
