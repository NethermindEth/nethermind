// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;
using Forks = Nethermind.Specs.Forks;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

public static class SszRestPaths
{
    /// <summary>
    /// Single source of truth: fork name (Eth-Execution-Version value) → <see cref="Forks.NamedReleaseSpec"/>. Built by
    /// walking back from the latest fork via <see cref="Forks.NamedReleaseSpec.Parent"/> and
    /// keeping every fork that introduces an engine-API method-version change vs. its parent.
    /// BPO blob-parameter override forks inherit all engine-API versions and so are filtered out.
    /// </summary>
    /// <remarks>
    /// To add support for a new fork, add it as a <see cref="Forks.NamedReleaseSpec"/> with its
    /// engine-API version overrides and update the <c>latest</c> argument here.
    /// </remarks>
    private static readonly Dictionary<string, Forks.NamedReleaseSpec> _forkSpecByUrl =
        BuildForkSpecsByUrl(Forks.Bogota.Instance);

    private static Dictionary<string, Forks.NamedReleaseSpec> BuildForkSpecsByUrl(Forks.NamedReleaseSpec latest)
    {
        // Stack reverses parent-chain order (Amsterdam → … → Paris becomes Paris → … → Amsterdam),
        // so the resulting dictionary preserves chronological insertion order.
        Stack<Forks.NamedReleaseSpec> ordered = new();
        for (Forks.NamedReleaseSpec? spec = latest; spec?.EngineApiNewPayloadVersion is not null; spec = spec.Parent)
        {
            if (spec.IntroducesEngineApiChange())
                ordered.Push(spec);
        }

        Dictionary<string, Forks.NamedReleaseSpec> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (Forks.NamedReleaseSpec spec in ordered)
            result[ForkName(spec)!] = spec;
        return result;
    }

    /// <summary>Lowercase fork name used as the <c>Eth-Execution-Version</c> header value.</summary>
    private static string? ForkName(Forks.NamedReleaseSpec spec) => spec.Name?.ToLowerInvariant();

    private static bool ResourceEquals(ReadOnlySpan<char> resource, string name) =>
        resource.Equals(name.AsSpan(), StringComparison.OrdinalIgnoreCase);

    public static readonly IReadOnlyList<string> SupportedForksOrdered = [.. _forkSpecByUrl.Keys];

    public static readonly FrozenSet<string> SupportedForks =
        SupportedForksOrdered.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public const string Payloads = "payloads";

    public const string Forkchoice = "forkchoice";

    public const string Capabilities = "capabilities";

    public const string ClientVersion = "identity";

    public const string PayloadBodiesByHash = "bodies/hash";

    public const string PayloadBodiesByRange = "bodies";

    public const string Blobs = "blobs";

    public const string PayloadWithWitness = "payloads/witness";

    public const string InclusionList = "inclusion_list";

    /// <summary>How a resource's fork and version are determined by <c>SszMiddleware</c>.</summary>
    public enum ResourceScoping
    {
        /// <summary>Fork (and thus method version) comes from the <c>Eth-Execution-Version</c> header.</summary>
        ForkScoped,
        /// <summary>Version comes from a trailing <c>/v{N}</c> path segment; no fork header (blobs).</summary>
        PathVersioned,
        /// <summary>No fork and no version — a single endpoint (capabilities, identity).</summary>
        Unscoped
    }

    /// <summary>Classifies how the given first path <paramref name="resource"/> segment is routed.</summary>
    public static ResourceScoping GetScoping(ReadOnlySpan<char> resource)
    {
        if (ResourceEquals(resource, Capabilities) || ResourceEquals(resource, ClientVersion)) return ResourceScoping.Unscoped;
        if (ResourceEquals(resource, Blobs)) return ResourceScoping.PathVersioned;
        return ResourceScoping.ForkScoped;
    }

    public const string PostPayloads = "POST /engine/v2/payloads";
    public const string GetPayloads = "GET /engine/v2/payloads/{payload_id}";
    public const string PostForkchoice = "POST /engine/v2/forkchoice";
    public const string PostBodiesByHash = "POST /engine/v2/bodies/hash";
    public const string GetBodiesByRange = "GET /engine/v2/bodies";
    public const string GetCapabilities = "GET /engine/v2/capabilities";
    public const string GetIdentity = "GET /engine/v2/identity";
    public const string PostBlobsV1 = "POST /engine/v2/blobs/v1";
    public const string PostBlobsV2 = "POST /engine/v2/blobs/v2";
    public const string PostBlobsV3 = "POST /engine/v2/blobs/v3";
    public const string PostBlobsV4 = "POST /engine/v2/blobs/v4";
    public const string PostPayloadsWitness = "POST /engine/v2/payloads/witness";
    public const string GetInclusionList = "GET /engine/v2/inclusion_list";

    // Fork-scoped endpoint → selector pulling its method version off a fork spec, keyed by resource
    // (one table per HTTP method). Presence in the table means the (method, resource) pair is a
    // recognized endpoint, even when the selector returns null because this fork predates it.
    private static readonly FrozenDictionary<string, Func<Forks.NamedReleaseSpec, int?>> _postVersionByResource =
        new Dictionary<string, Func<Forks.NamedReleaseSpec, int?>>(StringComparer.OrdinalIgnoreCase)
        {
            [Payloads] = static spec => spec.EngineApiNewPayloadVersion,
            [Forkchoice] = static spec => spec.EngineApiForkchoiceVersion,
            [PayloadBodiesByHash] = static spec => spec.EngineApiPayloadBodiesByHashVersion,
            // Witness reuses the newPayload version, but only from the EIP-7928 fork onward.
            [PayloadWithWitness] = static spec => spec.IsEip7928Enabled ? spec.EngineApiNewPayloadVersion : null,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, Func<Forks.NamedReleaseSpec, int?>> _getVersionByResource =
        new Dictionary<string, Func<Forks.NamedReleaseSpec, int?>>(StringComparer.OrdinalIgnoreCase)
        {
            [Payloads] = static spec => spec.EngineApiGetPayloadVersion,
            [PayloadBodiesByRange] = static spec => spec.EngineApiPayloadBodiesByRangeVersion,
            // EIP-7805 (FOCIL): inclusion lists exist only from the Bogota fork onward.
            [InclusionList] = static spec => spec.IsEip7805Enabled ? 1 : null,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, Func<Forks.NamedReleaseSpec, int?>>.AlternateLookup<ReadOnlySpan<char>> _postVersionLookup =
        _postVersionByResource.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly FrozenDictionary<string, Func<Forks.NamedReleaseSpec, int?>>.AlternateLookup<ReadOnlySpan<char>> _getVersionLookup =
        _getVersionByResource.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>
    /// Resolves the per-fork engine API method version for the given <paramref name="resource"/>
    /// + <paramref name="httpMethod"/> by looking it up on the fork's <see cref="Forks.NamedReleaseSpec"/>.
    /// Each fork only declares the versions it changes vs. its parent; values flow forward through
    /// the spec inheritance chain.
    /// </summary>
    /// <param name="recognizedResource">
    /// <c>true</c> when <paramref name="resource"/> + <paramref name="httpMethod"/> name a known
    /// fork-scoped endpoint, even if this fork has no version for it (e.g. <c>paris</c> + <c>bodies</c>).
    /// Lets the caller distinguish "endpoint not available for this fork" (400) from "unknown
    /// endpoint" (404) when the returned version is <c>null</c>.
    /// </param>
    public static int? MapForkToVersion(string fork, ReadOnlySpan<char> resource, string httpMethod, out bool recognizedResource)
    {
        Func<Forks.NamedReleaseSpec, int?>? selectVersion = null;
        if (HttpMethods.IsPost(httpMethod))
            _postVersionLookup.TryGetValue(resource, out selectVersion);
        else if (HttpMethods.IsGet(httpMethod))
            _getVersionLookup.TryGetValue(resource, out selectVersion);

        recognizedResource = selectVersion is not null;
        return recognizedResource && _forkSpecByUrl.TryGetValue(fork, out Forks.NamedReleaseSpec? spec)
            ? selectVersion!(spec)
            : null;
    }

    /// <summary>
    /// Returns the fork name that owns <paramref name="spec"/>'s engine API surface, walking up the
    /// parent chain so BPO forks resolve to their parent (e.g. <c>bpo1 → osaka</c>). Matched
    /// case-insensitively against the <c>Eth-Execution-Version</c> value, so it is returned as-is
    /// (no per-call lowercasing).
    /// </summary>
    public static string? GetEngineApiForkName(IReleaseSpec spec)
    {
        for (Forks.NamedReleaseSpec? n = spec as Forks.NamedReleaseSpec; n is not null; n = n.Parent)
        {
            if (n.Name is { } name && _forkSpecByUrl.ContainsKey(name))
                return name;
        }
        return null;
    }
}
