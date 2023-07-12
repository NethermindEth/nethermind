// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;

namespace Nethermind.Merge.Plugin.Handlers;

public interface IValidateFork
{
    bool ValidateFork(ISpecProvider specProvider);
}
