// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;

namespace Nethermind.Merge.Plugin.Handlers;

public interface IForkValidator
{
    /// <param name="version">The engine-API method version the payload arrived on.</param>
    bool ValidateFork(ISpecProvider specProvider, int version);
}
