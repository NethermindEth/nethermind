// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.JsonRpc.Data;

/// <summary>
/// Optional second parameter for <c>trace_block</c> that requests re-execution
/// under a specific hard-fork spec instead of the block's canonical spec.
/// </summary>
/// <param name="ForkName">
/// Case-insensitive name of the target fork (e.g. <c>"Berlin"</c>, <c>"Prague"</c>).
/// Must be a fork recognised by the node's spec provider.
/// </param>
public record ForkActivationParameter(string ForkName = "");
