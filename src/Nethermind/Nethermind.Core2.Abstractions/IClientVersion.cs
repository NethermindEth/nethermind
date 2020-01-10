namespace Nethermind.Core2
{
    public interface IClientVersion
    {
        /// <summary>
        /// Product, version, platform, and environment details to identify the application.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Format similar to a  [HTTP User-Agent](https://tools.ietf.org/html/rfc7231#section-5.5.3) field.
        /// </para>
        /// <para>
        /// This consists of one or more product identifiers, each followed by zero or more comments.
        /// Each product identifier consists of a name and optional version (separated by a slash).
        /// </para>
        /// <para>
        /// By convention, the product identifiers are listed in decreasing order of their significance for identifying the software.
        /// Commonly there may be only one product.
        /// </para>
        /// <para>
        /// Comments are enclosed in parentheses, [Section 3.2 of RFC 7230](https://tools.ietf.org/html/rfc7230#section-3.2.6).
        /// </para>
        /// </remarks>
        string Description { get; }
    }
}