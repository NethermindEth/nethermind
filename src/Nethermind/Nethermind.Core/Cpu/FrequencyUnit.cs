// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Derived from https://github.com/AndreyAkinshin/perfolizer
// Licensed under the MIT License

namespace Nethermind.Core.Cpu;

public class FrequencyUnit
{
    public static readonly FrequencyUnit Hz = new("Hz", "Hertz", 1L);

    public static readonly FrequencyUnit KHz = new("KHz", "Kilohertz", 1000L);

    public static readonly FrequencyUnit MHz = new("MHz", "Megahertz", 1000000L);

    public static readonly FrequencyUnit GHz = new("GHz", "Gigahertz", 1000000000L);

    public static readonly FrequencyUnit[] All = new FrequencyUnit[4] { Hz, KHz, MHz, GHz };

    public string Name { get; }

    public string Description { get; }

    public long HertzAmount { get; }

    private FrequencyUnit(string name, string description, long hertzAmount)
    {
        Name = name;
        Description = description;
        HertzAmount = hertzAmount;
    }

    public Frequency ToFrequency(long value = 1L) => new(value, this);
}
