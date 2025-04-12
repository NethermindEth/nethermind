// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.State;

public interface ILastNStateRootTracker
{
    bool HasStateRoot(Hash256 stateRoot);
}
