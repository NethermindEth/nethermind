// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle.Json;

namespace Nethermind.Specs.ChainSpecStyle;

/// <summary>
/// Provides default fork settings including EIP activations, blob schedules, and contract addresses.
/// Settings are loaded from the embedded ForkSettings.json resource.
/// </summary>
public class ForkSettings
{
    private static readonly Lazy<ForkSettings> _instance = new(LoadFromEmbeddedResource);

    /// <summary>
    /// Gets the singleton instance of ForkSettings loaded from embedded resource.
    /// </summary>
    public static ForkSettings Instance => _instance.Value;

    private readonly ForkSettingsJson _settings;
    private readonly Dictionary<string, ForkDefinitionJson> _forks;

    private ForkSettings(ForkSettingsJson settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        // Create case-insensitive dictionary for fork lookups
        _forks = new Dictionary<string, ForkDefinitionJson>(StringComparer.OrdinalIgnoreCase);
        if (settings.Forks is not null)
        {
            foreach (var kvp in settings.Forks)
            {
                _forks[kvp.Key] = kvp.Value;
            }
        }
    }

    /// <summary>
    /// Gets the list of EIPs activated at the specified fork.
    /// </summary>
    public IReadOnlyList<int> GetForkEips(string forkName)
    {
        if (_forks.TryGetValue(forkName, out var fork) && fork.Eips is not null)
        {
            return fork.Eips;
        }

        return [];
    }

    /// <summary>
    /// Gets the default blob schedule for a fork, if defined.
    /// </summary>
    public BlobScheduleEntryJson? GetBlobSchedule(string forkName)
    {
        if (_forks.TryGetValue(forkName, out var fork))
        {
            return fork.BlobSchedule;
        }

        return null;
    }

    /// <summary>
    /// Checks if a specific EIP is activated at or before the given fork.
    /// </summary>
    public bool IsEipActiveAtFork(int eipNumber, string forkName)
    {
        var forkOrder = GetForkOrder();
        int targetForkIndex = FindForkIndex(forkOrder, forkName);
        if (targetForkIndex < 0)
        {
            return false;
        }

        for (int i = 0; i <= targetForkIndex; i++)
        {
            var eips = GetForkEips(forkOrder[i]);
            foreach (int eip in eips)
            {
                if (eip == eipNumber)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the name of the fork that first activates the specified EIP.
    /// </summary>
    /// <returns>The fork name, or null if the EIP is not found in any fork.</returns>
    public string? GetActivatingFork(int eipNumber)
    {
        var forkOrder = GetForkOrder();
        foreach (string forkName in forkOrder)
        {
            var eips = GetForkEips(forkName);
            foreach (int eip in eips)
            {
                if (eip == eipNumber)
                {
                    return forkName;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the index of a fork name in the fork order using case-insensitive comparison.
    /// </summary>
    private static int FindForkIndex(string[] forkOrder, string forkName)
    {
        for (int i = 0; i < forkOrder.Length; i++)
        {
            if (string.Equals(forkOrder[i], forkName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Gets the ordered list of fork names.
    /// </summary>
    public string[] GetForkOrder() =>
    [
        "homestead",
        "tangerineWhistle",
        "spuriousDragon",
        "byzantium",
        "constantinople",
        "petersburg",
        "istanbul",
        "berlin",
        "london",
        "shanghai",
        "cancun",
        "prague",
        "osaka",
        "bpo1",
        "bpo2"
    ];

    /// <summary>
    /// Gets the default contract addresses.
    /// </summary>
    public ContractAddressesJson Contracts => _settings.Contracts ?? new ContractAddressesJson();

    /// <summary>
    /// Gets the default chain parameters.
    /// </summary>
    public DefaultParametersJson Defaults => _settings.Defaults ?? new DefaultParametersJson();

    /// <summary>
    /// Gets the beacon roots contract address (EIP-4788).
    /// </summary>
    public Address BeaconRootsAddress =>
        Contracts.BeaconRoots ?? Eip4788Constants.BeaconRootsAddress;

    /// <summary>
    /// Gets the block hash history contract address (EIP-2935).
    /// </summary>
    public Address BlockHashHistoryAddress =>
        Contracts.BlockHashHistory ?? Eip2935Constants.BlockHashHistoryAddress;

    /// <summary>
    /// Gets the withdrawal request contract address (EIP-7002).
    /// </summary>
    public Address WithdrawalRequestAddress =>
        Contracts.WithdrawalRequest ?? Eip7002Constants.WithdrawalRequestPredeployAddress;

    /// <summary>
    /// Gets the consolidation request contract address (EIP-7251).
    /// </summary>
    public Address ConsolidationRequestAddress =>
        Contracts.ConsolidationRequest ?? Eip7251Constants.ConsolidationRequestPredeployAddress;

    /// <summary>
    /// Gets the default deposit contract address.
    /// </summary>
    public Address? DepositContractAddress => Contracts.DepositContract;

    private static ForkSettings LoadFromEmbeddedResource()
    {
        var assembly = typeof(ForkSettings).Assembly;
        var resourceName = "Nethermind.Specs.ChainSpecStyle.ForkSettings.json";

        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        }

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new AddressConverter() }
        };

        var settings = JsonSerializer.Deserialize<ForkSettingsJson>(stream, options);
        if (settings is null)
        {
            throw new InvalidOperationException("Failed to deserialize ForkSettings.json");
        }

        return new ForkSettings(settings);
    }

    /// <summary>
    /// Creates ForkSettings from a JSON stream. Used for testing or custom settings.
    /// </summary>
    public static ForkSettings LoadFromStream(Stream stream)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new AddressConverter() }
        };

        var settings = JsonSerializer.Deserialize<ForkSettingsJson>(stream, options);
        if (settings is null)
        {
            throw new InvalidOperationException("Failed to deserialize fork settings");
        }

        return new ForkSettings(settings);
    }
}
