// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Api.Common;
using Nethermind.BeaconChain.Types;
using Nethermind.BeaconChain.ValidatorApi;
using Nethermind.Config;
using Nethermind.Core.Exceptions;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.ValidatorClient;

/// <summary>The registry index of each configured validator key, found through <see cref="IBeaconNodeValidatorApi.GetValidators"/>.</summary>
/// <remarks>
/// <para>Keys not in the registry yet are looked up again in a later epoch, at most once an epoch; a lookup that fails is tried again at the next call.</para>
/// <para>
/// Constructing one throws <see cref="InvalidConfigurationException"/> when <see cref="IValidatorClientConfig.PublicKeys"/> is empty or holds a
/// malformed key.
/// </para>
/// <para>Thread safety: not thread-safe; each duty runner owns one and uses it from its own loop only.</para>
/// </remarks>
internal sealed class ValidatorIndexLookup(IValidatorClientConfig config, IBeaconNodeValidatorApi node, ILogger logger)
{
    private BlsPublicKey[] _unresolvedKeys = ParsePublicKeys(config.PublicKeys);
    private ulong? _lookedUpInEpoch;

    /// <summary>The number of configured keys.</summary>
    public int KeyCount { get; } = config.PublicKeys.Length;

    /// <summary>The indices of the keys found so far, in the order they were found.</summary>
    public ulong[] Indices { get; private set; } = [];

    /// <summary>Looks up the keys not found yet, unless that was already done in <paramref name="epoch"/>.</summary>
    public async Task Update(ulong epoch, CancellationToken cancellationToken)
    {
        if (_unresolvedKeys.Length == 0 || _lookedUpInEpoch == epoch) return;
        try
        {
            HashSet<BlsPublicKey> found = [];
            foreach (ValidatorSummary validator in await node.GetValidators(_unresolvedKeys, cancellationToken))
            {
                if (!found.Add(validator.PublicKey)) continue;
                Indices = [.. Indices, validator.ValidatorIndex];
                if (logger.IsInfo) logger.Info($"Validator {validator.PublicKey} is index {validator.ValidatorIndex}");
            }

            _unresolvedKeys = Array.FindAll(_unresolvedKeys, key => !found.Contains(key));
            if (_unresolvedKeys.Length > 0 && logger.IsInfo) logger.Info($"{_unresolvedKeys.Length} validator keys are not in the registry yet; looking them up again next epoch");
            _lookedUpInEpoch = epoch;
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            DutyCalls.LogFailure(logger, "Looking up the validator keys", e);
        }
    }

    private static BlsPublicKey[] ParsePublicKeys(string[] publicKeys)
    {
        if (publicKeys.Length == 0)
            throw new InvalidConfigurationException("ValidatorClient.PublicKeys is empty; name at least one validator key.", ExitCodes.ForbiddenOptionValue);

        BlsPublicKey[] parsed = new BlsPublicKey[publicKeys.Length];
        for (int i = 0; i < parsed.Length; i++)
        {
            if (!HexConvert.TryParsePubKey(publicKeys[i], out parsed[i]))
                throw new InvalidConfigurationException($"ValidatorClient.PublicKeys entry '{publicKeys[i]}' is not a 0x-prefixed 48-byte BLS public key.", ExitCodes.ForbiddenOptionValue);
        }

        return parsed;
    }
}
