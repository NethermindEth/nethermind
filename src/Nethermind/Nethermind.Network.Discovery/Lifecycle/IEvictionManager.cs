// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Lifecycle;

public interface IEvictionManager
{
    void StartEvictionProcess(INodeLifecycleManager evictionCandidate, INodeLifecycleManager replacementCandidate);
}
