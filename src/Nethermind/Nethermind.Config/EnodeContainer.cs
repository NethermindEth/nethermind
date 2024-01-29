// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Config;

public class EnodeContainer
{
    private IEnode _enode = null;

    public IEnode Enode
    {
        get
        {
            if (_enode == null)
                throw new InvalidOperationException("Enode not configured. Ensure SetupKeyStore step was executed.");
            return _enode;
        }
        set
        {
            _enode = value;
        }
    }
}
