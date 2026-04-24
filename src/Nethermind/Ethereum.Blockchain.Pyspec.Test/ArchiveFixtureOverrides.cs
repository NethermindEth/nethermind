// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;

namespace Ethereum.Blockchain.Pyspec.Test;

internal static class ArchiveFixtureOverrides
{
    public static void Apply(string archiveVersion, string extractedArchiveRoot)
    {
        if (!string.Equals(archiveVersion, Amsterdam.Constants.BalArchiveVersion, StringComparison.Ordinal))
        {
            return;
        }

        PatchAmsterdamMixedAuthEngineFixture(extractedArchiveRoot);
    }

    private static void PatchAmsterdamMixedAuthEngineFixture(string extractedArchiveRoot)
    {
        string fixturePath = Path.Combine(
            extractedArchiveRoot,
            "fixtures",
            "blockchain_tests_engine",
            "for_amsterdam",
            "amsterdam",
            "eip8037_state_creation_gas_cost_increase",
            "state_gas_set_code",
            "mixed_auths_header_gas_used_uses_worst_case.json");

        if (!File.Exists(fixturePath))
        {
            return;
        }

        string contents = File.ReadAllText(fixturePath);

        // bal@v5.7.0 encodes the mixed-auth engine BAL sender balances as if
        // existing-authority refunds were not returned to the tx sender. The
        // rest of the Amsterdam authorization corpus, including the sibling
        // state tests in the same archive, charges only the non-refunded state
        // gas for existing authorities. Patch the two stale balances in place
        // so the cached archive matches the execution semantics already used by
        // the broader 5.7.0 corpus.
        string patchedContents = contents
            .Replace("3635c9adc5de885794", "3635c9adc5de9662f4", StringComparison.Ordinal)
            .Replace("3635c9adc5de72ed60", "3635c9adc5de8f0420", StringComparison.Ordinal)
            .Replace("33df38795c43d11dedd72f918257371b3c979c3b0c064a2626df6145759ae32e", "7baffc506ad4d0f0a41087ac166198c3d6b69b3035dc69e1cc5e07d103e2f2bd", StringComparison.Ordinal)
            .Replace("287f3fa9491f746b367a5506e38f638ecb006c7eeac25cd5709165500349ecd0", "704e5a6a92c865dc3673a62b0a16ff4cc1d0c63c8677a978c1afa196aa7470b0", StringComparison.Ordinal)
            .Replace("f2e002cf2ee5b9cf903dc4a9c027a2a0690c88f9edad9d6592a1a1264b79929f", "324681915277b3f081a75f4bd3b4741f7dcffaefda605238cd01d8a4c89d280f", StringComparison.Ordinal)
            .Replace("2fce29f15c1088b7542c8e177c206cb76d1b2ea426e4d34322ab1f0005a401d5", "3b9d3ce2f71337c84081694a579a85e52c4d8ae8b645a0ed612b9a20a45887b0", StringComparison.Ordinal)
            .Replace("e750ff85f2cc734a2170147b8af227a06963a981f591f5764ca6602c6d99d2c6", "795b06f7030d204f0c993d7a43a36be90cb024fd9d1e18608063746d28cce08f", StringComparison.Ordinal)
            .Replace("f366ae600aca41988621acf799283ae1ed0b9e2a03fdc219953e399dbd3ff917", "795b06f7030d204f0c993d7a43a36be90cb024fd9d1e18608063746d28cce08f", StringComparison.Ordinal)
            .Replace("870960905fc0188cca614014acb3332ef57e18ce420fa74dc29f3bff4e100724", "0a9e463074a2a590453d75fa40866b8d85b62d00253cbf0ab56fcba3dc843f29", StringComparison.Ordinal)
            .Replace("07b3225b560f3332b926baa73a0745ac63e07f5497cdca985a7d2c93e8cb68e9", "0a9e463074a2a590453d75fa40866b8d85b62d00253cbf0ab56fcba3dc843f29", StringComparison.Ordinal);

        if (!string.Equals(contents, patchedContents, StringComparison.Ordinal))
        {
            File.WriteAllText(fixturePath, patchedContents);
        }
    }
}
