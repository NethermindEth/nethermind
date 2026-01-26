// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Consensus.Ethash;
using Nethermind.Serialization.Rlp;

namespace Nethermind.EthereumClassic;

/// <summary>
/// Etchash (ECIP-1099): epoch length doubles from 30,000 to 60,000 blocks post-transition.
/// </summary>
internal class Etchash : Ethash, IEthash
{
    private readonly ILogger _logger;
    private readonly long _ecip1099Transition;
    private readonly Dictionary<uint, (uint DagEpoch, Task<IEthashDataSet> DataSet)> _cache = new(); // keyed by seedEpoch
    private readonly object _lock = new();
    private const long EtchashEpochLength = 60000;
    private readonly uint _transitionEpoch;

    public Etchash(ILogManager logManager, long ecip1099Transition) : base(logManager)
    {
        _logger = logManager.GetClassLogger();
        _ecip1099Transition = ecip1099Transition;
        _transitionEpoch = (uint)(ecip1099Transition / EpochLength);
        if (_logger.IsInfo) _logger.Info($"Etchash initialized with ECIP-1099 transition at block {ecip1099Transition} (epoch {_transitionEpoch})");
    }

    private uint GetEtchashEpoch(long blockNumber) =>
        blockNumber < _ecip1099Transition
            ? (uint)(blockNumber / EpochLength)
            : (_transitionEpoch / 2) + (uint)((blockNumber - _ecip1099Transition) / EtchashEpochLength);

    private uint GetSeedEpoch(uint dagEpoch, bool ecip1099Active) =>
        ecip1099Active ? dagEpoch * 2 : dagEpoch;

    void IEthash.HintRange(Guid guid, long start, long end)
    {
        uint startEpoch = GetEtchashEpoch(start);
        uint endEpoch = GetEtchashEpoch(end);
        bool ecip1099Active = start >= _ecip1099Transition;
        lock (_lock)
        {
            for (uint e = startEpoch; e <= endEpoch && e - startEpoch <= 10; e++)
            {
                uint dagEpoch = e;
                uint seedEpoch = GetSeedEpoch(dagEpoch, ecip1099Active);
                _cache.TryAdd(seedEpoch, (dagEpoch, Task.Run<IEthashDataSet>(() => new EthashCache(GetCacheSize(dagEpoch), GetSeedHash(seedEpoch).Bytes))));
            }
        }
    }

    bool IEthash.Validate(BlockHeader header)
    {
        uint dagEpoch = GetEtchashEpoch(header.Number);

        if (!TryGetDataSet(dagEpoch, header.Number, out var dataSet))
        {
            if (_logger.IsWarn) _logger.Warn($"Etchash cache miss for block {header.Number}, dagEpoch {dagEpoch}");
            return false;
        }

        ulong dataSize = GetDataSize(dagEpoch);
        var headerHash = Keccak.Compute(new HeaderDecoder().Encode(header, RlpBehaviors.ForSealing).Bytes);
        (byte[]? mixHash, _, bool valid) = Hashimoto(dataSize, dataSet, headerHash, header.MixHash, header.Nonce);

        if (!valid && _logger.IsWarn)
            _logger.Warn($"Etchash validation failed for block {header.Number}, dagEpoch {dagEpoch}, difficulty {header.Difficulty}");

        return valid;
    }

    (Hash256, ulong) IEthash.Mine(BlockHeader header, ulong? startNonce)
    {
        uint dagEpoch = GetEtchashEpoch(header.Number);
        bool ecip1099Active = header.Number >= _ecip1099Transition;
        uint seedEpoch = GetSeedEpoch(dagEpoch, ecip1099Active);

        TryGetDataSet(dagEpoch, header.Number, out var dataSet);
        dataSet ??= new EthashCache(GetCacheSize(dagEpoch), GetSeedHash(seedEpoch).Bytes);
        var headerHash = Keccak.Compute(new HeaderDecoder().Encode(header, RlpBehaviors.ForSealing).Bytes);
        ulong nonce = startNonce ?? (ulong)Random.Shared.NextInt64();
        while (true)
        {
            (byte[]? mix, _, bool ok) = Hashimoto(GetDataSize(dagEpoch), dataSet, headerHash, null, nonce);
            if (ok && mix is not null) return (new Hash256(mix), nonce);
            nonce++;
        }
    }

    private bool TryGetDataSet(uint dagEpoch, long blockNumber, out IEthashDataSet dataSet)
    {
        bool ecip1099Active = blockNumber >= _ecip1099Transition;
        uint seedEpoch = GetSeedEpoch(dagEpoch, ecip1099Active);
        lock (_lock) { if (_cache.TryGetValue(seedEpoch, out var t)) { dataSet = t.DataSet.Result; return true; } }
        ((IEthash)this).HintRange(Guid.Empty, blockNumber, blockNumber);
        lock (_lock) { if (_cache.TryGetValue(seedEpoch, out var t)) { dataSet = t.DataSet.Result; return true; } }
        dataSet = null!; return false;
    }
}
