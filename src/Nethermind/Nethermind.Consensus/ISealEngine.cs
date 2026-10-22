// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus
{
    // Note: Prefer to override `ISealer, ISealValidator` instead.
    // Default `SealEngine` combine the two.
    public interface ISealEngine : ISealer, ISealValidator
    {
    }
}
