// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.BeaconChain.Types;

namespace Nethermind.BeaconChain.Api.Common;

/// <summary>Indexes immutable API validator registries without retaining expired state snapshots.</summary>
internal sealed class ValidatorIndexCache
{
    private readonly ConditionalWeakTable<Validator[], Lazy<Dictionary<BlsPublicKey, int>>> _indices = [];

    internal Dictionary<BlsPublicKey, int> Get(Validator[] validators) =>
        _indices.GetValue(validators, static registry => new(() => Build(registry))).Value;

    private static Dictionary<BlsPublicKey, int> Build(Validator[] validators)
    {
        Dictionary<BlsPublicKey, int> indices = [with(validators.Length)];
        for (int i = 0; i < validators.Length; i++)
            indices[validators[i].Pubkey] = i;
        return indices;
    }
}
