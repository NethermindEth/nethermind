// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.State;
using System;
using System.Collections.Generic;
using Nethermind.Logging;
using Nethermind.Crypto;

namespace Nethermind.Evm;
public class AuthorizedCodeInfoRepository : ICodeInfoRepository
{
    public IEnumerable<Address> AuthorizedAddresses => _authorizedCode.Keys;
    private readonly Dictionary<Address, CodeInfo> _authorizedCode = new();
    private readonly EthereumEcdsa _ethereumEcdsa;
    private readonly ICodeInfoRepository _codeInfoRepository;
    private readonly ulong _chainId;
    private readonly ILogger _logger;
    private readonly byte[] _internalBuffer = new byte[128];

    public AuthorizedCodeInfoRepository(ICodeInfoRepository codeInfoRepository, ulong chainId, ILogger? logger = null)
    {
        _codeInfoRepository = codeInfoRepository;
        _chainId = chainId;
        _ethereumEcdsa = new EthereumEcdsa(_chainId, NullLogManager.Instance);
        _logger = logger ?? NullLogger.Instance;
        _internalBuffer[0] = Eip7702Constants.Magic;
    }
    public CodeInfo GetCachedCodeInfo(IWorldState worldState, Address codeSource, IReleaseSpec vmSpec) =>
        _authorizedCode.TryGetValue(codeSource, out CodeInfo result)
            ? result
            : _codeInfoRepository.GetCachedCodeInfo(worldState, codeSource, vmSpec);

    public CodeInfo GetOrAdd(ValueHash256 codeHash, ReadOnlySpan<byte> initCode) => _codeInfoRepository.GetOrAdd(codeHash, initCode);

    public void InsertCode(IWorldState state, ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec, bool isSystemEnv) =>
        _codeInfoRepository.InsertCode(state, code, codeOwner, spec, isSystemEnv);

    /// <summary>
    /// Copy code from <paramref name="codeSource"/> and set it to override <paramref name="target"/>.
    /// Main use for this is for https://eips.ethereum.org/EIPS/eip-7702
    /// </summary>
    public void CopyCodeAndOverwrite(
        IWorldState worldState,
        Address codeSource,
        Address target,
        IReleaseSpec vmSpec)
    {
        if (!_authorizedCode.ContainsKey(target))
        {
            _authorizedCode.Add(target, GetCachedCodeInfo(worldState, codeSource, vmSpec));
        }
    }

    /// <summary>
    /// Build a code cache from transaction authorization_list authorized by signature.
    /// eip-7702
    /// </summary>
    public void InsertFromAuthorizations(
        IWorldState worldState,
        AuthorizationTuple?[] authorizations,
        IReleaseSpec spec)
    {
        _authorizedCode.Clear();

        //TODO optimize
        foreach (AuthorizationTuple? authTuple in authorizations)
        {
            if (authTuple is null)
                continue;
            authTuple.Authority = authTuple.Authority ?? _ethereumEcdsa.RecoverAddress(authTuple);

            string? error;
            if (!authTuple.IsValidForExecution(worldState, _chainId, out error))
            {
                if (_logger.IsDebug) _logger.Debug($"Skipping tuple in authorization_list: {error}");
                continue;
            }
            CopyCodeAndOverwrite(worldState, authTuple.CodeAddress, authTuple.Authority, spec);
        }
    }

    public void ClearAuthorizations()
    {
        _authorizedCode.Clear();
    }
}
