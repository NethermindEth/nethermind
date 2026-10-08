// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>Decides whether a <c>getPayload</c> result may be returned under the fork its block falls in.</summary>
/// <remarks>The version is implicit: each result type serves exactly one <c>getPayload</c> version. The
/// <c>newPayload</c> side gates separately through <c>ExecutionPayload.ValidateForkOnNewPayload</c>.</remarks>
public interface IForkValidator
{
    bool ValidateFork(ISpecProvider specProvider);
}
