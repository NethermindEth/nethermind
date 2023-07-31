// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;

namespace Nethermind.Merge.Plugin.Handlers;

public interface IForkValidator
{
    bool ValidateFork(ISpecProvider specProvider);
}
