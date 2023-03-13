// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Merge.Plugin.GC;

public interface IGCStrategy
{
    bool ShouldTryToPreventGCDuringBlockProcessing();
    int GCGenerationToCollectBetweenBlockProcessing();
}
