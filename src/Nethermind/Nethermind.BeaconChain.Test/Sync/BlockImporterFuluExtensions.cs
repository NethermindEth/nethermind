// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.Test.Sync;

internal static class BlockImporterFuluExtensions
{
    public static BlockImportResult Import(this IBlockImporter importer, SignedBeaconBlock block, Hash256 blockRoot, bool verifySignatures) =>
        importer.Import(new ForkedSignedBeaconBlock.OfFulu(block), blockRoot, verifySignatures);
}
