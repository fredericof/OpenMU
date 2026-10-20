// <copyright file="BrowserAccountStore.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.EntityFramework;

using System.Security.Cryptography;
using System.Text;
using System.Threading;
using BCrypt.Net;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using AccountEntity = MUnique.OpenMU.Persistence.EntityFramework.Model.Account;
using AccountState = MUnique.OpenMU.DataModel.Entities.AccountState;
using ItemStorage = MUnique.OpenMU.Persistence.EntityFramework.Model.ItemStorage;

/// <summary>
/// Database operations used by the browser Google sign-in endpoints.
/// </summary>
public sealed class BrowserAccountStore
{
    private const int LoginNameLength = 10;
    private const int LoginNameCreationAttempts = 8;
    private const int PasswordHashWorkFactor = 11;
    private const string LoginNameAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>
    /// Gets or creates the account identified by a Google subject and starts a browser session.
    /// </summary>
    /// <param name="googleSubject">The stable Google account subject.</param>
    /// <param name="googleEmail">The verified Google account e-mail address.</param>
    /// <param name="sessionTokenHash">The hash of the new browser session token.</param>
    /// <param name="sessionExpiresAt">The new session expiration time.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The internal OpenMU login name.</returns>
    public async Task<string> SignInWithGoogleAsync(
        string googleSubject,
        string googleEmail,
        string sessionTokenHash,
        DateTime sessionExpiresAt,
        CancellationToken cancellationToken = default)
    {
        await using var context = new EntityDataContext();

        var existing = await context.Set<AccountEntity>()
            .Where(account => account.GoogleSubject == googleSubject)
            .Select(account => new { account.Id, account.LoginName })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            await this.StartSessionAsync(context, existing.Id, googleEmail, sessionTokenHash, sessionExpiresAt, cancellationToken).ConfigureAwait(false);
            return existing.LoginName;
        }

        var passwordHash = BCrypt.HashPassword(GenerateRandomPassword(), workFactor: PasswordHashWorkFactor);
        for (var attempt = 0; attempt < LoginNameCreationAttempts; attempt++)
        {
            var account = new AccountEntity
            {
                Id = Guid.NewGuid(),
                LoginName = GenerateLoginName(),
                PasswordHash = passwordHash,
                EMail = googleEmail,
                GoogleSubject = googleSubject,
                Vault = new ItemStorage { Id = Guid.NewGuid() },
                BrowserSessionTokenHash = sessionTokenHash,
                BrowserSessionExpiresAt = sessionExpiresAt,
            };

            context.Add(account);
            try
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return account.LoginName;
            }
            catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
            {
                context.ChangeTracker.Clear();
                existing = await context.Set<AccountEntity>()
                    .Where(candidate => candidate.GoogleSubject == googleSubject)
                    .Select(candidate => new { candidate.Id, candidate.LoginName })
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (existing is not null)
                {
                    await this.StartSessionAsync(context, existing.Id, googleEmail, sessionTokenHash, sessionExpiresAt, cancellationToken).ConfigureAwait(false);
                    return existing.LoginName;
                }
            }
        }

        throw new InvalidOperationException("Could not allocate a unique browser account name.");
    }

    /// <summary>
    /// Gets the account name associated with an unexpired browser session token hash.
    /// </summary>
    /// <param name="sessionTokenHash">The hash of the browser session token.</param>
    /// <param name="now">The current UTC time.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The account login name when the session is valid.</returns>
    public async Task<string?> GetAccountNameForSessionAsync(string sessionTokenHash, DateTime now, CancellationToken cancellationToken = default)
    {
        await using var context = new EntityDataContext();
        return await context.Set<AccountEntity>()
            .Where(account => account.BrowserSessionTokenHash == sessionTokenHash
                && account.BrowserSessionExpiresAt > now
                && account.State != AccountState.Banned
                && account.State != AccountState.TemporarilyBanned
                && account.GoogleSubject != null)
            .Select(account => account.LoginName)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Creates or replaces the outstanding login ticket for an authenticated browser session.
    /// </summary>
    /// <param name="sessionTokenHash">The hash of the browser session token.</param>
    /// <param name="ticketHash">The hash of the new game login ticket.</param>
    /// <param name="now">The current UTC time.</param>
    /// <param name="ticketExpiresAt">The ticket expiration time.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> when a valid session was updated.</returns>
    public async Task<bool> IssueLoginTicketAsync(
        string sessionTokenHash,
        string ticketHash,
        DateTime now,
        DateTime ticketExpiresAt,
        CancellationToken cancellationToken = default)
    {
        await using var context = new EntityDataContext();
        var affected = await context.Set<AccountEntity>()
            .Where(account => account.BrowserSessionTokenHash == sessionTokenHash
                && account.BrowserSessionExpiresAt > now
                && account.State != AccountState.Banned
                && account.State != AccountState.TemporarilyBanned
                && account.GoogleSubject != null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(account => account.BrowserLoginTicketHash, ticketHash)
                    .SetProperty(account => account.BrowserLoginTicketExpiresAt, ticketExpiresAt),
                cancellationToken)
            .ConfigureAwait(false);
        return affected == 1;
    }

    /// <summary>
    /// Invalidates the browser session and any outstanding login ticket.
    /// </summary>
    /// <param name="sessionTokenHash">The hash of the browser session token.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> when the session was invalidated.</returns>
    public async Task<bool> EndSessionAsync(string sessionTokenHash, CancellationToken cancellationToken = default)
    {
        await using var context = new EntityDataContext();
        var affected = await context.Set<AccountEntity>()
            .Where(account => account.BrowserSessionTokenHash == sessionTokenHash)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(account => account.BrowserSessionTokenHash, (string?)null)
                    .SetProperty(account => account.BrowserSessionExpiresAt, (DateTime?)null)
                    .SetProperty(account => account.BrowserLoginTicketHash, (string?)null)
                    .SetProperty(account => account.BrowserLoginTicketExpiresAt, (DateTime?)null),
                cancellationToken)
            .ConfigureAwait(false);
        return affected == 1;
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception)
    {
        return exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
    }

    private static string GenerateLoginName()
    {
        var name = new StringBuilder(LoginNameLength);
        while (name.Length < LoginNameLength)
        {
            name.Append(LoginNameAlphabet[RandomNumberGenerator.GetInt32(LoginNameAlphabet.Length)]);
        }

        return name.ToString();
    }

    private static string GenerateRandomPassword()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    }

    private async Task StartSessionAsync(
        EntityDataContext context,
        Guid accountId,
        string googleEmail,
        string sessionTokenHash,
        DateTime sessionExpiresAt,
        CancellationToken cancellationToken)
    {
        await context.Set<AccountEntity>()
            .Where(account => account.Id == accountId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(account => account.EMail, googleEmail)
                    .SetProperty(account => account.BrowserSessionTokenHash, sessionTokenHash)
                    .SetProperty(account => account.BrowserSessionExpiresAt, sessionExpiresAt)
                    .SetProperty(account => account.BrowserLoginTicketHash, (string?)null)
                    .SetProperty(account => account.BrowserLoginTicketExpiresAt, (DateTime?)null),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
