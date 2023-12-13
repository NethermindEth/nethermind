// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Derived from https://github.com/AndreyAkinshin/perfolizer
// Licensed under the MIT License

namespace Nethermind.Init.Cpu;

internal class FrequencyUnit
{
    public static readonly FrequencyUnit Hz = new FrequencyUnit("Hz", "Hertz", 1L);

    public static readonly FrequencyUnit KHz = new FrequencyUnit("KHz", "Kilohertz", 1000L);

    public static readonly FrequencyUnit MHz = new FrequencyUnit("MHz", "Megahertz", 1000000L);

    public static readonly FrequencyUnit GHz = new FrequencyUnit("GHz", "Gigahertz", 1000000000L);

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

    public Frequency ToFrequency(long value = 1L)
    {
        return new Frequency(value, this);
    }
}
