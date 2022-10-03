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

    public override string ToString() => ToString(string.Empty);

    public string ToString(string indentation)
    {
        var sb = new StringBuilder();

        sb.Append(indentation).Append("Index:     ").AppendLine(Index.ToString());
        sb.Append(indentation).Append("Recipient: ").AppendLine(Recipient.ToString());
        sb.Append(indentation).Append("Amount:    ").AppendLine(Amount.ToString());

        return sb.ToString();
    }
}
