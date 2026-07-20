using GraphMailer.Service.Services.Reporting;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;
using Microsoft.Identity.Client;
using Microsoft.Kiota.Abstractions.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace GraphMailer.ConfigTool.Services;

/// <summary>
/// Sends a single test email via Microsoft Graph API using the credentials
/// currently configured in the ConfigTool (without requiring a saved config file).
/// </summary>
internal static class GraphApiTestService
{
    private static readonly string[] Scopes = ["https://graph.microsoft.com/.default"];

    /// <param name="tenantId">Entra ID Tenant ID.</param>
    /// <param name="clientId">Application (client) ID.</param>
    /// <param name="clientSecret">Client secret — pass null when using certificate auth.</param>
    /// <param name="certThumbprint">Certificate thumbprint — pass null when using secret auth.</param>
    /// <param name="from">Sender address (must be a licensed mailbox in the tenant).</param>
    /// <param name="to">Recipient address.</param>
    /// <param name="ct">Cancellation token.</param>
    internal static async Task SendAsync(
        string tenantId,
        string clientId,
        string? clientSecret,
        string? certThumbprint,
        string from,
        string to,
        CancellationToken ct)
    {
        // ── Build MSAL confidential-client application ────────────────────
        IConfidentialClientApplication msal;

        if (!string.IsNullOrWhiteSpace(certThumbprint))
        {
            var cert = FindCertificate(certThumbprint);
            if (cert is null)
                throw new InvalidOperationException(
                    $"Certificate with thumbprint '{certThumbprint}' not found " +
                    "in LocalMachine\\My or CurrentUser\\My.");

            msal = ConfidentialClientApplicationBuilder
                .Create(clientId)
                .WithTenantId(tenantId)
                .WithCertificate(cert)
                .Build();
        }
        else if (!string.IsNullOrWhiteSpace(clientSecret))
        {
            msal = ConfidentialClientApplicationBuilder
                .Create(clientId)
                .WithTenantId(tenantId)
                .WithClientSecret(clientSecret)
                .Build();
        }
        else
        {
            throw new InvalidOperationException(
                "No authentication configured. " +
                "Enter a Client Secret or select a certificate.");
        }

        // ── Acquire token (client-credentials flow) ───────────────────────
        var tokenResult = await msal
            .AcquireTokenForClient(Scopes)
            .ExecuteAsync(ct);

        // ── Build Graph client with static token ──────────────────────────
        var graphClient = new GraphServiceClient(
            new BaseBearerTokenAuthenticationProvider(
                new StaticBearerProvider(tokenResult.AccessToken)));

        // ── Send test email ───────────────────────────────────────────────
        var bodyHtml = NotificationHtmlRenderer.Render(new NotificationEmail
        {
            Severity = NotificationSeverity.Success,
            Title = "Microsoft Graph API connection is working",
            Intro = "This is a test message sent by the GraphMailer ConfigTool. " +
                    "If you received this, the Microsoft Graph API connection is working correctly.",
            Fields = [new("Sent at", $"{DateTime.Now:f}")],
            Kicker = "Connection Test",
            FooterNote = "Sent manually from the ConfigTool → Graph API page (Send test email).",
        });

        var body = new SendMailPostRequestBody
        {
            Message = new Message
            {
                Subject = "[GraphMailer] Connection test",
                Body = new ItemBody
                {
                    ContentType = BodyType.Html,
                    Content = bodyHtml,
                },
                ToRecipients =
                [
                    new Recipient
                    {
                        EmailAddress = new EmailAddress { Address = to },
                    },
                ],
            },
            SaveToSentItems = false,
        };

        await graphClient.Users[from].SendMail.PostAsync(body, cancellationToken: ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static X509Certificate2? FindCertificate(string thumbprint)
    {
        var normalized = thumbprint.Replace(" ", "").ToUpperInvariant();

        foreach (var (location, name) in new[]
        {
            (StoreLocation.LocalMachine, StoreName.My),
            (StoreLocation.CurrentUser,  StoreName.My),
        })
        {
            using var store = new X509Store(name, location);
            try { store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly); }
            catch { continue; }

            var match = store.Certificates
                .Find(X509FindType.FindByThumbprint, normalized, validOnly: false)
                .OfType<X509Certificate2>()
                .FirstOrDefault();

            if (match is not null) return match;
        }

        return null;
    }

    /// <summary>
    /// Simple <see cref="IAccessTokenProvider"/> that returns a pre-acquired static token.
    /// Graph SDK's <see cref="BaseBearerTokenAuthenticationProvider"/> calls this per request.
    /// </summary>
    private sealed class StaticBearerProvider(string accessToken) : IAccessTokenProvider
    {
        public Task<string> GetAuthorizationTokenAsync(
            Uri uri,
            Dictionary<string, object>? additionalAuthenticationContext = null,
            CancellationToken ct = default)
            => Task.FromResult(accessToken);

        public AllowedHostsValidator AllowedHostsValidator { get; } = new();
    }
}
