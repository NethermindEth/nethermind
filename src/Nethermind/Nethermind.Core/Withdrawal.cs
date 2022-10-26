using System.Text;
using Nethermind.Int256;

namespace Nethermind.Core;

/// <summary>
/// Represents a withdrawal that has been validated at the consensus layer.
/// </summary>
public class Withdrawal
{
    /// <summary>
    /// Gets or sets the withdrawal amount as a big-endian value in units of Wei.
    /// </summary>
    public UInt256 Amount { get; set; }

    /// <summary>
    /// Gets or sets the withdrawal unique id.
    /// </summary>
    public ulong Index { get; set; }

    /// <summary>
    /// Gets or sets the withdrawal recipient address.
    /// </summary>
    public Address Recipient { get; set; } = Address.Zero;

    /// <summary>
    /// Gets or sets the validator index on the consensus layer the withdrawal corresponds to.
    /// </summary>
    public ulong ValidatorIndex { get; set; }

    public override string ToString() => ToString(string.Empty);

    public string ToString(string indentation) => new StringBuilder()
        .AppendLine($"{indentation}{nameof(Index)}:          {Index}")
        .AppendLine($"{indentation}{nameof(ValidatorIndex)}: {ValidatorIndex}")
        .AppendLine($"{indentation}{nameof(Recipient)}:      {Recipient}")
        .AppendLine($"{indentation}{nameof(Amount)}:         {Amount}")
        .ToString();
}
