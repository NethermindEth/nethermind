// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Crypto;

/// <summary>The recognized signature programs of the native Lean backend that local policy enables (EIP-8437 mode 2).</summary>
/// <remarks>Keys are read from the backend once, on first use, so a node that enables no program never loads it for this.</remarks>
public sealed class NativeRecognizedProgramVerifier : IRecognizedProgramVerifier
{
    /// <summary>The EIP-8437 profile names the backend implements, by its program identifier.</summary>
    public static readonly IReadOnlyDictionary<string, uint> Profiles = new Dictionary<string, uint>(StringComparer.Ordinal)
    {
        ["mldsa44-pure-v1"] = 1,
        ["falcon512-padded-v1"] = 2,
        ["slhdsa-shake128s-pure-v1"] = 3,
    };

    private readonly Lazy<Dictionary<ValueHash256, uint>> _enabled;

    /// <param name="profiles">The EIP-8437 profile names to enable.</param>
    /// <exception cref="ArgumentException">A name is not a profile the backend implements.</exception>
    public NativeRecognizedProgramVerifier(IEnumerable<string> profiles)
    {
        List<uint> ids = [];
        foreach (string name in profiles)
            ids.Add(Profiles.TryGetValue(name, out uint id) ? id : throw new ArgumentException($"Unknown recognized program '{name}'.", nameof(profiles)));
        _enabled = new(() =>
        {
            Dictionary<ValueHash256, uint> keys = [];
            foreach (uint id in ids) keys[NativeLeanProofVerifier.RecognizedKey(id)] = id;
            return keys;
        });
    }

    /// <summary>The enabled programs' keys, by profile identifier.</summary>
    public IReadOnlyDictionary<ValueHash256, uint> Keys => _enabled.Value;

    /// <inheritdoc/>
    public bool Recognizes(in ValueHash256 verificationKey) => _enabled.Value.ContainsKey(verificationKey);

    /// <inheritdoc/>
    public bool Verify(in ValueHash256 verificationKey, RawSignature signature, out ValueHash256 claim)
    {
        claim = default;
        return _enabled.Value.TryGetValue(verificationKey, out uint profile) && NativeLeanProofVerifier.VerifyRaw(profile, signature, out claim);
    }

    /// <inheritdoc/>
    public byte[] ProveLeaf(in ValueHash256 verificationKey, IReadOnlyList<RawSignature> signatures) =>
        _enabled.Value.TryGetValue(verificationKey, out uint profile)
            ? NativeLeanProofVerifier.ProveRawLeaf(profile, signatures)
            : throw new InvalidOperationException("Not a recognized program key.");
}
