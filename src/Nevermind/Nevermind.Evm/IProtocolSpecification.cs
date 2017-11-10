namespace Nevermind.Evm
{
    /// <summary>
    /// https://github.com/ethereum/EIPs
    /// </summary>
    public interface IProtocolSpecification
    {
        /// <summary>
        /// CREATE instruction cost set to 32000 (previously 0)
        /// </summary>
        bool IsEip2Enabled { get; }

        /// <summary>
        /// DELEGATECALL insstruction added
        /// </summary>
        bool IsEip7Enabled { get; }

        /// <summary>
        /// Gas cost of IO operations increased
        /// </summary>
        bool IsEip150Enabled { get; }

        /// <summary>
        /// Chain ID in signatures
        /// </summary>
        bool IsEip155Enabled { get; }

        /// <summary>
        /// Code size limit
        /// </summary>
        bool IsEip170Enabled { get; }
    }
}