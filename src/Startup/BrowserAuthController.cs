// <copyright file="BrowserAuthController.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Startup;

using System.Threading;
using Google.Apis.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.EntityFramework;

/// <summary>
/// Google sign-in and short-lived login tickets for the browser game client.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("browser-auth")]
public sealed class BrowserAuthController : ControllerBase
{
    private const string SessionCookieName = "__Host-mu-browser-session";
    private static readonly TimeSpan SessionDuration = TimeSpan.FromDays(14);
    private static readonly TimeSpan LoginTicketDuration = TimeSpan.FromSeconds(60);

    private readonly BrowserAccountStore _accountStore;
    private readonly IConfiguration _configuration;

    /// <summary>
    /// Initializes a new instance of the <see cref="BrowserAuthController"/> class.
    /// </summary>
    /// <param name="accountStore">The database operations for browser accounts.</param>
    /// <param name="configuration">The application configuration.</param>
    public BrowserAuthController(BrowserAccountStore accountStore, IConfiguration configuration)
    {
        this._accountStore = accountStore;
        this._configuration = configuration;
    }

    /// <summary>
    /// Gets the public Google client identifier for the browser's Google Identity Services button.
    /// </summary>
    [HttpGet("config")]
    public IActionResult GetConfig()
    {
        return this.Ok(new { clientId = this._configuration["BrowserAuth:ClientId"] ?? string.Empty });
    }

    /// <summary>
    /// Gets the current browser session state.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The current session state.</returns>
    [HttpGet("session")]
    public async Task<IActionResult> GetSessionAsync(CancellationToken cancellationToken)
    {
        if (!this.Request.Cookies.TryGetValue(SessionCookieName, out var sessionToken) || string.IsNullOrEmpty(sessionToken))
        {
            return this.Ok(new BrowserSessionResponse(false));
        }

        var accountName = await this._accountStore.GetAccountNameForSessionAsync(BrowserAuthTokens.Hash(sessionToken), DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
        if (accountName is null)
        {
            this.DeleteSessionCookie();
            return this.Ok(new BrowserSessionResponse(false));
        }

        return this.Ok(new BrowserSessionResponse(true, accountName));
    }

    /// <summary>
    /// Validates a Google ID token, creates or finds its account, and establishes a browser session.
    /// </summary>
    /// <param name="request">The Google ID token supplied by Identity Services.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The signed-in account state.</returns>
    [HttpPost("google")]
    public async Task<IActionResult> SignInWithGoogleAsync([FromBody] GoogleCredentialRequest request, CancellationToken cancellationToken)
    {
        if (!this.HasExpectedOrigin())
        {
            return this.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (!this.TryGetClientId(out var clientId))
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (string.IsNullOrWhiteSpace(request.Credential))
        {
            return this.Unauthorized();
        }

        GoogleJsonWebSignature.Payload payload;
        try
        {
            payload = await GoogleJsonWebSignature.ValidateAsync(request.Credential, new GoogleJsonWebSignature.ValidationSettings
            {
                Audience = new[] { clientId },
            }).ConfigureAwait(false);
        }
        catch (InvalidJwtException)
        {
            return this.Unauthorized();
        }
        catch (ArgumentException)
        {
            return this.Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(payload.Subject)
            || payload.EmailVerified != true
            || string.IsNullOrWhiteSpace(payload.Email))
        {
            return this.Unauthorized();
        }

        var sessionToken = BrowserAuthTokens.CreateSessionToken();
        var now = DateTime.UtcNow;
        var sessionExpiresAt = now.Add(SessionDuration);
        var accountName = await this._accountStore.SignInWithGoogleAsync(
            payload.Subject,
            payload.Email.Trim(),
            BrowserAuthTokens.Hash(sessionToken),
            sessionExpiresAt,
            cancellationToken).ConfigureAwait(false);
        this.SetSessionCookie(sessionToken, sessionExpiresAt);
        return this.Ok(new BrowserSessionResponse(true, accountName));
    }

    /// <summary>
    /// Creates a short-lived one-use game login ticket for the signed-in browser account.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The account name and game login ticket.</returns>
    [HttpPost("ticket")]
    public async Task<IActionResult> CreateTicketAsync(CancellationToken cancellationToken)
    {
        if (!this.HasExpectedOrigin())
        {
            return this.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (!this.Request.Cookies.TryGetValue(SessionCookieName, out var sessionToken) || string.IsNullOrEmpty(sessionToken))
        {
            return this.Unauthorized();
        }

        var now = DateTime.UtcNow;
        var ticket = BrowserAuthTokens.CreateLoginTicket();
        var issued = await this._accountStore.IssueLoginTicketAsync(
            BrowserAuthTokens.Hash(sessionToken),
            BrowserAuthTokens.Hash(ticket),
            now,
            now.Add(LoginTicketDuration),
            cancellationToken).ConfigureAwait(false);
        if (!issued)
        {
            this.DeleteSessionCookie();
            return this.Unauthorized();
        }

        var accountName = await this._accountStore.GetAccountNameForSessionAsync(BrowserAuthTokens.Hash(sessionToken), now, cancellationToken).ConfigureAwait(false);
        if (accountName is null)
        {
            return this.Unauthorized();
        }

        return this.Ok(new BrowserTicketResponse(accountName, ticket));
    }

    /// <summary>
    /// Ends the current browser session and removes any outstanding login ticket.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An empty response when the session has ended.</returns>
    [HttpPost("logout")]
    public async Task<IActionResult> LogoutAsync(CancellationToken cancellationToken)
    {
        if (!this.HasExpectedOrigin())
        {
            return this.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (this.Request.Cookies.TryGetValue(SessionCookieName, out var sessionToken) && !string.IsNullOrEmpty(sessionToken))
        {
            await this._accountStore.EndSessionAsync(BrowserAuthTokens.Hash(sessionToken), cancellationToken).ConfigureAwait(false);
        }

        this.DeleteSessionCookie();
        return this.NoContent();
    }

    private bool TryGetClientId(out string clientId)
    {
        clientId = this._configuration["BrowserAuth:ClientId"] ?? string.Empty;
        var allowedOrigin = this._configuration["BrowserAuth:AllowedOrigin"];
        return !string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(allowedOrigin);
    }

    private bool HasExpectedOrigin()
    {
        var expectedOrigin = this._configuration["BrowserAuth:AllowedOrigin"];
        var origin = this.Request.Headers.Origin.ToString();
        return !string.IsNullOrWhiteSpace(expectedOrigin)
            && !string.IsNullOrWhiteSpace(origin)
            && string.Equals(origin, expectedOrigin, StringComparison.Ordinal);
    }

    private void SetSessionCookie(string sessionToken, DateTime expiresAt)
    {
        this.Response.Cookies.Append(SessionCookieName, sessionToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = new DateTimeOffset(expiresAt, TimeSpan.Zero),
            IsEssential = true,
        });
    }

    private void DeleteSessionCookie()
    {
        this.Response.Cookies.Delete(SessionCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
        });
    }

    /// <summary>
    /// The Google ID token received from the browser.
    /// </summary>
    /// <param name="Credential">The Google ID token.</param>
    public sealed record GoogleCredentialRequest(string Credential);

    /// <summary>
    /// The status of a browser session.
    /// </summary>
    /// <param name="Authenticated">Whether the browser session is valid.</param>
    /// <param name="AccountName">The internal account name, if signed in.</param>
    public sealed record BrowserSessionResponse(bool Authenticated, string? AccountName = null);

    /// <summary>
    /// A one-use game login ticket and the associated internal account name.
    /// </summary>
    /// <param name="Username">The internal account name.</param>
    /// <param name="Ticket">The one-use login ticket.</param>
    public sealed record BrowserTicketResponse(string Username, string Ticket);
}
