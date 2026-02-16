// SPDX-FileCopyrightText: 2026 Anil Chinchawale
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Xdc;

/// <summary>
/// Post-processes the genesis block to replace the standard BlockHeader with XdcBlockHeader.
/// This is required because XDC block headers include additional fields (Validator, Validators, Penalties)
/// that must be encoded in the block hash calculation.
/// </summary>
public class XdcGenesisPostProcessor(ILogger logger) : IGenesisPostProcessor
{
    private readonly ILogger _logger = logger;

    public void PostProcess(Block genesis)
    {
        if (genesis.Header is XdcBlockHeader)
        {
            // Already an XdcBlockHeader, nothing to do
            if (_logger.IsDebug) _logger.Debug("Genesis header is already XdcBlockHeader");
            return;
        }

        if (_logger.IsInfo) _logger.Info("Converting genesis BlockHeader to XdcBlockHeader");

        // Create XdcBlockHeader from the standard BlockHeader
        XdcBlockHeader xdcHeader = XdcBlockHeader.FromBlockHeader(genesis.Header);

        // Set XDC-specific fields to empty arrays for genesis block
        xdcHeader.Validator = Array.Empty<byte>();
        xdcHeader.Validators = Array.Empty<byte>();
        xdcHeader.Penalties = Array.Empty<byte>();

        // Replace the Header field using reflection
        // The Block.Header property is read-only, but the backing field can be set
        FieldInfo? headerField = typeof(Block).GetField("<Header>k__BackingField", 
            BindingFlags.Instance | BindingFlags.NonPublic);

        if (headerField is null)
        {
            _logger.Error("Failed to find Header backing field in Block class");
            throw new InvalidOperationException("Cannot replace genesis header: Header field not found");
        }

        headerField.SetValue(genesis, xdcHeader);

        if (_logger.IsInfo) _logger.Info("Successfully replaced genesis header with XdcBlockHeader");
    }
}
