// <copyright file="BrowserAuthTokens.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Creates and hashes opaque browser session and game login tokens.
/// </summary>
public static class BrowserAuthTokens
{
    /// <summary>
    /// The reserved prefix for one-use browser login tickets.
    /// </summary>
    public const string LoginTicketPrefix = "g_";

    private const int SessionTokenByteCount = 32;
    private const int LoginTicketByteCount = 12;

    /// <summary>
    /// Creates an opaque session token suitable for an HttpOnly cookie.
    /// </summary>
    public static string CreateSessionToken()
    {
        return Base64UrlEncode(RandomNumberGenerator.GetBytes(SessionTokenByteCount));
    }

    /// <summary>
    /// Creates a ticket which fits in the 20 byte password field of the long login packet.
    /// </summary>
    public static string CreateLoginTicket()
    {
        return LoginTicketPrefix + Base64UrlEncode(RandomNumberGenerator.GetBytes(LoginTicketByteCount));
    }

    /// <summary>
    /// Hashes a token for storage in the account database.
    /// </summary>
    /// <param name="token">The raw token value.</param>
    /// <returns>The uppercase hexadecimal SHA-256 hash.</returns>
    public static string Hash(string token)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    /// <summary>
    /// Validates the reserved ticket format and returns its storage hash.
    /// </summary>
    /// <param name="ticket">The ticket from the login packet password field.</param>
    /// <param name="hash">The storage hash when the format is valid.</param>
    /// <returns><see langword="true"/> when the ticket has the reserved format.</returns>
    public static bool TryHashLoginTicket(string ticket, out string hash)
    {
        hash = string.Empty;
        if (ticket.Length != LoginTicketPrefix.Length + 16 || !ticket.StartsWith(LoginTicketPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var encodedSecret = ticket.AsSpan(LoginTicketPrefix.Length);
        foreach (var character in encodedSecret)
        {
            if (!IsBase64UrlCharacter(character))
            {
                return false;
            }
        }

        hash = Hash(ticket);
        return true;
    }

    private static bool IsBase64UrlCharacter(char character)
    {
        return character is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '-'
            or '_';
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
