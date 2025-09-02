// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;

namespace Nethermind.Db.Rocks.Config;

public class PerTableDbConfig : IRocksDbConfig
{
    private readonly string _tableName;
    private readonly string? _columnName;
    private readonly IDbConfig _dbConfig;
    private readonly string[] _prefixes;
    private readonly string[] _reversedPrefixes;

    public PerTableDbConfig(IDbConfig dbConfig, string dbName, string? columnName = null)
    {
        _dbConfig = dbConfig;
        _tableName = dbName;
        _columnName = columnName;
        _prefixes = GetPrefixes();
        _reversedPrefixes = _prefixes.Reverse().ToArray();

#if DEBUG
        EnsureConfigIsAvailable(nameof(RocksDbOptions));
        EnsureConfigIsAvailable(nameof(AdditionalRocksDbOptions));
#endif
    }

    private void EnsureConfigIsAvailable(string propertyName)
    {
        Type type = typeof(IDbConfig);
        foreach (var prefix in _prefixes)
        {
            string prefixed = string.Concat(prefix, propertyName);
            if (GetPrefixedConfigProperty(type, prefixed) is null)
            {
                throw new InvalidConfigurationException($"Configuration {propertyName} not available with prefix {prefix}. Add {prefix}{propertyName} to {nameof(IDbConfig)}.", -1);
            }
        }
    }

    public ulong? WriteBufferSize => ReadConfig<ulong?>(nameof(WriteBufferSize));
    public ulong? WriteBufferNumber => ReadConfig<ulong?>(nameof(WriteBufferNumber));

    public string RocksDbOptions => ReadRocksdbOptions(_dbConfig, nameof(RocksDbOptions), _prefixes);
    public string AdditionalRocksDbOptions => ReadRocksdbOptions(_dbConfig, nameof(AdditionalRocksDbOptions), _prefixes);

    public int? MaxOpenFiles => ReadConfig<int?>(nameof(MaxOpenFiles));
    public bool WriteAheadLogSync => ReadConfig<bool>(nameof(WriteAheadLogSync));
    public ulong? ReadAheadSize => ReadConfig<ulong?>(nameof(ReadAheadSize));
    public bool EnableDbStatistics => _dbConfig.EnableDbStatistics;
    public uint StatsDumpPeriodSec => _dbConfig.StatsDumpPeriodSec;
    public bool? VerifyChecksum => ReadConfig<bool?>(nameof(VerifyChecksum));
    public ulong? RowCacheSize => ReadConfig<ulong?>(nameof(RowCacheSize));
    public bool EnableFileWarmer => ReadConfig<bool>(nameof(EnableFileWarmer));
    public double CompressibilityHint => ReadConfig<double>(nameof(CompressibilityHint));
    public bool FlushOnExit => ReadConfig<bool?>(nameof(FlushOnExit)) ?? true;

    private T? ReadConfig<T>(string propertyName)
    {
        return ReadConfig<T>(_dbConfig, propertyName, _reversedPrefixes);
    }

    private string[] GetPrefixes()
    {
        if (_tableName.StartsWith("State"))
        {
            return ["StateDb"];
        }

        if (_columnName != null)
        {
            return [
                string.Concat(_tableName, "Db"),
                string.Concat(_tableName, _columnName, "Db"),
            ];
        }

        return [string.Concat(_tableName, "Db")];
    }

    private static string ReadRocksdbOptions(IDbConfig dbConfig, string propertyName, string[] prefixes)
    {
        Type type = dbConfig.GetType();
        PropertyInfo? propertyInfo;

        // TODO: clarify if this can be case-insensitive
        string val = (string)type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)!.GetValue(dbConfig)!;

        foreach (var prefix in prefixes)
        {
            string prefixed = string.Concat(prefix, propertyName);

            propertyInfo = GetPrefixedConfigProperty(type, prefixed);
            if (propertyInfo is not null)
            {
                string? valObj = (string?)propertyInfo.GetValue(dbConfig);
                if (!string.IsNullOrEmpty(valObj))
                {
                    if (!valObj.EndsWith(';')) throw new InvalidConfigurationException($"Rocksdb config must end with `;`. Invalid property is {propertyName} in {prefixed}.", -1);
                    val += valObj;
                }
            }
        }

        return val;
    }

    private static T? ReadConfig<T>(IDbConfig dbConfig, string propertyName, string[] prefixes)
    {
        try
        {
            Type type = dbConfig.GetType();
            PropertyInfo? propertyInfo;

            foreach (var prefix in prefixes)
            {
                string prefixed = string.Concat(prefix, propertyName);

                propertyInfo = GetPrefixedConfigProperty(type, prefixed);
                if (propertyInfo is not null)
                {
                    if (propertyInfo.PropertyType.CanBeAssignedNull())
                    {
                        // If its nullable check if its null first
                        object? valObj = propertyInfo.GetValue(dbConfig);
                        if (valObj is not null)
                        {
                            T? val = (T?)valObj;
                            if (val is not null)
                            {
                                return val;
                            }
                        }
                    }
                    else
                    {
                        // If not nullable just use it directly
                        return (T?)propertyInfo.GetValue(dbConfig);
                    }
                }
            }

            // Use generic one even if its available
            propertyInfo = GetPrefixedConfigProperty(type, propertyName);
            return (T?)propertyInfo?.GetValue(dbConfig);
        }
        catch (Exception e)
        {
            throw new InvalidDataException($"Unable to read property from DB config. Prefixes: ${prefixes}", e);
        }
    }

    private static PropertyInfo? GetPrefixedConfigProperty(Type type, string name)
    {
        return type.GetProperty(name, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
    }
}
