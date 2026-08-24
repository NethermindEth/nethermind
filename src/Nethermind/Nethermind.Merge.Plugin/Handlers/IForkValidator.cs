// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>Decides whether a <c>getPayload</c> result may be returned under the fork its block falls in.</summary>
/// <remarks>Each result type is tied to exactly one <c>getPayload</c> version, so the version is implicit
/// here; the <c>newPayload</c> side gates on its own version through
/// <c>ExecutionPayload.ValidateForkOnNewPayload</c>, which payload subclasses override instead of this.</remarks>
public interface IForkValidator
{
    bool ValidateFork(ISpecProvider specProvider);
}
