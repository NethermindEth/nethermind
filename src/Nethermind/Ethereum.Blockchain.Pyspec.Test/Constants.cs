// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Ethereum.Blockchain.Pyspec.Test;

public class Constants
{
    public const string ARCHIVE_URL_TEMPLATE = "https://github.com/ethereum/execution-specs/releases/download/{0}/{1}";
    public const string DEFAULT_ARCHIVE_VERSION = "tests-bal@v7.2.0";
    public const string DEFAULT_ARCHIVE_NAME = "fixtures_bal.tar.gz";
    // EIP-7805 (FOCIL): execution-specs PR #2643 places fixtures under
    // tests/amsterdam/eip7805_focil/, so once a release tag containing them
    // is published, bump DEFAULT_ARCHIVE_VERSION (and possibly DEFAULT_ARCHIVE_NAME
    // if the release names a focil-specific bundle) and
    // AmsterdamBlockchainTests / AmsterdamEngineBlockchainTests will pick them
    // up automatically — they already glob the for_amsterdam tree.
}
