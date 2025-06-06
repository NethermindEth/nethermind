// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


namespace Nethermind.Consensus.Stateless;

public struct Witness
{
    public Witness()
    {

    }


    public byte[][] Codes;
    public byte[][] State;
    public byte[][] Keys;
    public byte[][] Headers;
}
