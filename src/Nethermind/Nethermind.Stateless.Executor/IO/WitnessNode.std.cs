// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Stateless.Execution.IO;

public readonly partial struct WitnessNode
{
    private static partial bool CanPlaceInInput => true;
}
