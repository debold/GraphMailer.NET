using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;
using Microsoft.Identity.Client;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;

namespace GraphMailer.ConfigTool.Services;

// ── Progress types ─────────────────────────────────────────────────────────────

internal enum SetupStepState { Pending, Running, Done, Skipped, Error }

/// <summary>Callback invoked whenever a setup step changes state.</summary>
/// <param name="stepIndex">Zero-based index of the step.</param>
/// <param name="state">New state.</param>
/// <param name="detail">Optional single-line detail shown under the step label.</param>
internal delegate void StepReporter(int stepIndex, SetupStepState state, string? detail = null);

/// <summary>
/// Called when an existing, still-valid certificate is found.
/// Return <c>true</c> to replace it, <c>false</c> to keep it.
/// </summary>
internal delegate Task<bool> CertReplaceDecider(string subject, string thumbprint, int daysRemaining, CancellationToken ct);

// ── Step-index constants ───────────────────────────────────────────────────────

internal static class SetupSteps
{
    public const int SignIn = 0;
    public const int CheckExisting = 1;
    public const int GenerateCert = 2;
    public const int CreateApp = 3;
    public const int CreateSp = 4;
    public const int GrantPermission = 5;
    public const int ApplyConfig = 6;

    public static readonly string[] Labels =
    [
        "Sign in to Microsoft Entra ID",
        "Check for existing app registration",
        "Generate self-signed certificate",
        "Create app registration",
        "Create service principal",
        "Grant Graph permissions (Mail.Send, Mail.ReadWrite, User.Read.All)",
        "Apply configuration",
    ];
}

internal static class RenewSteps
{
    public const int SignIn = 0;
    public const int FindApp = 1;
    public const int GenerateCert = 2;
    public const int UploadCert = 3;
    public const int ApplyConfig = 4;

    public static readonly string[] Labels =
    [
        "Sign in to Microsoft Entra ID",
        "Find app registration",
        "Generate new certificate",
        "Upload certificate to Entra ID",
        "Apply configuration",
    ];
}

// ── Main service ───────────────────────────────────────────────────────────────

/// <summary>
/// Performs one-click Entra ID app registration for GraphMailer.
/// Signs in interactively via the system browser and uses the Microsoft Graph API
/// to create (or reuse) the app registration, service principal, and Mail.Send permission.
/// </summary>
internal static class EntraSetupService
{
    // Azure CLI public client ID – standard bootstrap for delegated Graph admin calls
    // in tooling that has no pre-registered client app.
    private const string BootstrapClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";
    private const string GraphBase = "https://graph.microsoft.com/v1.0";
    private const string MsGraphAppId = "00000003-0000-0000-c000-000000000000";
    private const string MailSendRoleId = "b633e1c5-b582-4048-a93e-9f11b44c7e96";
    // Mail.ReadWrite (application) — required for attachments ≥ 3 MB: Graph's sendMail
    // request is capped at 4 MB, so large attachments go through a draft + upload
    // session, which are mailbox write operations not covered by Mail.Send.
    private const string MailReadWriteRoleId = "e2a3a72e-5f79-4c64-b1b1-878b674786c9";
    // User.Read.All (application) — required by the optional tenant sender validation
    private const string UserReadAllRoleId = "df021288-bdef-4463-88db-98f22de89214";

    private static readonly (string RoleId, string Name)[] RequiredAppRoles =
    [
        (MailSendRoleId, "Mail.Send"),
        (MailReadWriteRoleId, "Mail.ReadWrite"),
        (UserReadAllRoleId, "User.Read.All"),
    ];
    internal const int SignInTimeoutMinutes = 3;
    internal const int CertExpiryWarningDays = 30;

    private static readonly string[] SetupScopes =
    [
        "https://graph.microsoft.com/Application.ReadWrite.All",
        "https://graph.microsoft.com/AppRoleAssignment.ReadWrite.All",
    ];

    private static readonly string[] RenewScopes =
    [
        "https://graph.microsoft.com/Application.ReadWrite.All",
    ];

    internal sealed record Result(
        string TenantId,
        string ClientId,
        string CertSubject,
        string CertThumbprint,
        DateTime CertNotAfter);

    // ── Full initial setup ──────────────────────────────────────────────────

    internal static async Task<Result> RunAsync(
        string appDisplayName,
        StepReporter report,
        Action<Uri>? onLoginUrl,
        CertReplaceDecider? certDecider,
        CancellationToken ct)
    {
        // ── Step 0: Sign in ────────────────────────────────────────────────
        report(SetupSteps.SignIn, SetupStepState.Running,
            $"Browser window opened. Waiting up to {SignInTimeoutMinutes} minutes…");

        var msalApp = PublicClientApplicationBuilder
            .Create(BootstrapClientId)
            .WithAuthority("https://login.microsoftonline.com/organizations")
            .WithDefaultRedirectUri()
            .Build();

        using var loginCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        loginCts.CancelAfter(TimeSpan.FromMinutes(SignInTimeoutMinutes));

        AuthenticationResult authResult;
        try
        {
            authResult = await msalApp
                .AcquireTokenInteractive(SetupScopes)
                .WithSystemWebViewOptions(new SystemWebViewOptions
                {
                    OpenBrowserAsync = (uri) =>
                    {
                        onLoginUrl?.Invoke(uri);
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                        return Task.CompletedTask;
                    }
                })
                .ExecuteAsync(loginCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Distinguish login timeout from user-pressed Cancel
            throw new TimeoutException(
                $"Sign-in was not completed within {SignInTimeoutMinutes} minutes.");
        }

        var tenantId = authResult.TenantId;
        report(SetupSteps.SignIn, SetupStepState.Done,
            $"Signed in as: {authResult.Account?.Username ?? "(unknown)"}");

        using var http = CreateHttpClient(authResult.AccessToken);

        // ── Step 1: Check for existing app registration ────────────────────
        report(SetupSteps.CheckExisting, SetupStepState.Running);
        var existingApp = await FindAppByNameAsync(http, appDisplayName, ct);
        string? existingClientId = existingApp?.GetProperty("appId").GetString();
        string? existingObjectId = existingApp?.GetProperty("id").GetString();

        if (existingClientId is not null)
            report(SetupSteps.CheckExisting, SetupStepState.Done,
                $"Found existing registration — App ID: {existingClientId}");
        else
            report(SetupSteps.CheckExisting, SetupStepState.Done,
                "No existing registration found.");

        // ── Step 2: Certificate — reuse or generate ────────────────────────
        report(SetupSteps.GenerateCert, SetupStepState.Running);

        // Check whether a still-valid GraphMailer cert already exists locally.
        var existing = FindExistingCert();
        bool generateNew = true;
        if (existing is not null)
        {
            var daysLeft = (int)(existing.NotAfter.Date - DateTime.UtcNow.Date).TotalDays;
            if (daysLeft > CertExpiryWarningDays)
            {
                // Cert is fine — ask the user whether to replace it.
                bool replace = certDecider is not null
                    && await certDecider(existing.Subject, existing.Thumbprint, daysLeft, ct);
                if (!replace)
                {
                    // Reuse the existing certificate.
                    report(SetupSteps.GenerateCert, SetupStepState.Skipped,
                        $"Keeping existing certificate — {existing.Subject}  |  valid until {existing.NotAfter:d}");
                    generateNew = false;
                }
            }
            // else: cert is expiring soon, fall through to generate a new one.
        }

        byte[] rawData;
        string certSubject, thumbprint;
        DateTime notAfter;

        if (generateNew)
        {
            (rawData, certSubject, thumbprint, notAfter) = GenerateAndInstallCert(appDisplayName);
            report(SetupSteps.GenerateCert, SetupStepState.Done,
                $"{certSubject}  |  valid until {notAfter:d}");
        }
        else
        {
            rawData = existing!.RawData;
            certSubject = existing.Subject;
            thumbprint = existing.Thumbprint;
            notAfter = existing.NotAfter;
        }
        var certB64 = Convert.ToBase64String(rawData);

        // ── Step 3: Create or update app registration ──────────────────────
        report(SetupSteps.CreateApp, SetupStepState.Running);
        string clientId;

        if (existingObjectId is not null)
        {
            clientId = existingClientId!;
            if (!generateNew)
            {
                // Cert was reused — nothing to change in Entra.
                report(SetupSteps.CreateApp, SetupStepState.Skipped,
                    $"Existing registration kept — App ID: {clientId}");
            }
            else
            {
                // New cert was generated — update keyCredentials in Entra.
                var patch = new
                {
                    keyCredentials = new[]
                    {
                        new { type = "AsymmetricX509Cert", usage = "Verify",
                              displayName = appDisplayName, key = certB64 }
                    }
                };
                var patchResp = await http.PatchAsJsonAsync(
                    $"{GraphBase}/applications/{existingObjectId}", patch, ct);
                await EnsureSuccessAsync(patchResp, "update app registration");
                report(SetupSteps.CreateApp, SetupStepState.Done,
                    $"Replaced certificate on existing registration — App ID: {clientId}");
            }
        }
        else
        {
            var appBody = new
            {
                displayName = appDisplayName,
                signInAudience = "AzureADMyOrg",
                keyCredentials = new[]
                {
                    new { type = "AsymmetricX509Cert", usage = "Verify",
                          displayName = appDisplayName, key = certB64 }
                }
            };
            var appResp = await http.PostAsJsonAsync($"{GraphBase}/applications", appBody, ct);
            await EnsureSuccessAsync(appResp, "create app registration");
            var appJson = await appResp.Content.ReadFromJsonAsync<JsonElement>(ct);
            clientId = appJson.GetProperty("appId").GetString()!;
            report(SetupSteps.CreateApp, SetupStepState.Done,
                $"Created — App ID: {clientId}");
        }

        // ── Step 4: Create or find service principal ───────────────────────
        report(SetupSteps.CreateSp, SetupStepState.Running);
        string spId;

        var existingSp = await FindSpByAppIdAsync(http, clientId, ct);
        if (existingSp is { } sp)
        {
            spId = sp.GetProperty("id").GetString()!;
            report(SetupSteps.CreateSp, SetupStepState.Skipped,
                $"Service principal already exists.");
        }
        else
        {
            var spResp = await http.PostAsJsonAsync(
                $"{GraphBase}/servicePrincipals", new { appId = clientId }, ct);
            await EnsureSuccessAsync(spResp, "create service principal");
            var spJson = await spResp.Content.ReadFromJsonAsync<JsonElement>(ct);
            spId = spJson.GetProperty("id").GetString()!;
            report(SetupSteps.CreateSp, SetupStepState.Done);
        }

        // ── Step 5: Grant Graph application permissions ────────────────────
        report(SetupSteps.GrantPermission, SetupStepState.Running);

        var grantedRoles = await GetGrantedRoleIdsAsync(http, spId, ct);
        var missingRoles = RequiredAppRoles.Where(r => !grantedRoles.Contains(r.RoleId)).ToList();

        if (missingRoles.Count == 0)
        {
            report(SetupSteps.GrantPermission, SetupStepState.Skipped,
                "Graph permissions already granted.");
        }
        else
        {
            // Find MS Graph service principal
            var graphSpResp = await http.GetAsync(
                $"{GraphBase}/servicePrincipals?$filter=appId eq '{MsGraphAppId}'&$select=id", ct);
            await EnsureSuccessAsync(graphSpResp, "find Microsoft Graph service principal");
            var graphSpJson = await graphSpResp.Content.ReadFromJsonAsync<JsonElement>(ct);
            var graphSpId = graphSpJson.GetProperty("value")[0].GetProperty("id").GetString()!;

            foreach (var (roleId, name) in missingRoles)
            {
                var grantResp = await http.PostAsJsonAsync(
                    $"{GraphBase}/servicePrincipals/{spId}/appRoleAssignments",
                    new { principalId = spId, resourceId = graphSpId, appRoleId = roleId },
                    ct);
                await EnsureSuccessAsync(grantResp, $"grant {name} permission");
            }

            report(SetupSteps.GrantPermission, SetupStepState.Done,
                $"Application permission(s) granted (admin consent): {string.Join(", ", missingRoles.Select(r => r.Name))}.");
        }

        // ── Step 6: Apply configuration ────────────────────────────────────
        report(SetupSteps.ApplyConfig, SetupStepState.Running);
        report(SetupSteps.ApplyConfig, SetupStepState.Done);

        return new Result(tenantId, clientId, certSubject, thumbprint, notAfter);
    }

    // ── Certificate renewal ─────────────────────────────────────────────────

    internal static async Task<Result> RenewCertificateAsync(
        string tenantId,
        string clientId,
        string appDisplayName,
        StepReporter report,
        Action<Uri>? onLoginUrl,
        CancellationToken ct)
    {
        // ── Step 0: Sign in ────────────────────────────────────────────────
        report(RenewSteps.SignIn, SetupStepState.Running,
            $"Browser window opened. Waiting up to {SignInTimeoutMinutes} minutes…");

        var msalApp = PublicClientApplicationBuilder
            .Create(BootstrapClientId)
            .WithAuthority($"https://login.microsoftonline.com/{tenantId}")
            .WithDefaultRedirectUri()
            .Build();

        using var loginCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        loginCts.CancelAfter(TimeSpan.FromMinutes(SignInTimeoutMinutes));

        AuthenticationResult authResult;
        try
        {
            authResult = await msalApp
                .AcquireTokenInteractive(RenewScopes)
                .WithSystemWebViewOptions(new SystemWebViewOptions
                {
                    OpenBrowserAsync = (uri) =>
                    {
                        onLoginUrl?.Invoke(uri);
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                        return Task.CompletedTask;
                    }
                })
                .ExecuteAsync(loginCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Sign-in was not completed within {SignInTimeoutMinutes} minutes.");
        }

        report(RenewSteps.SignIn, SetupStepState.Done,
            $"Signed in as: {authResult.Account?.Username ?? "(unknown)"}");
        using var http = CreateHttpClient(authResult.AccessToken);

        // ── Step 1: Find app registration ──────────────────────────────────
        report(RenewSteps.FindApp, SetupStepState.Running);
        var findResp = await http.GetAsync(
            $"{GraphBase}/applications?$filter=appId eq '{clientId}'&$select=id", ct);
        await EnsureSuccessAsync(findResp, "find app registration");
        var findJson = await findResp.Content.ReadFromJsonAsync<JsonElement>(ct);
        var arr = findJson.GetProperty("value");
        if (arr.GetArrayLength() == 0)
            throw new InvalidOperationException(
                $"App registration with Client ID '{clientId}' not found in Entra ID.");
        var appObjectId = arr[0].GetProperty("id").GetString()!;
        report(RenewSteps.FindApp, SetupStepState.Done, $"Object ID: {appObjectId}");

        // ── Step 2: Generate new certificate ───────────────────────────────
        report(RenewSteps.GenerateCert, SetupStepState.Running);
        var (rawData, certSubject, thumbprint, notAfter) =
            GenerateAndInstallCert(appDisplayName);
        var certB64 = Convert.ToBase64String(rawData);
        report(RenewSteps.GenerateCert, SetupStepState.Done,
            $"valid until {notAfter:d}");

        // ── Step 3: Upload certificate ──────────────────────────────────────
        report(RenewSteps.UploadCert, SetupStepState.Running);
        var patchBody = new
        {
            keyCredentials = new[]
            {
                new { type = "AsymmetricX509Cert", usage = "Verify",
                      displayName = appDisplayName, key = certB64 }
            }
        };
        var patchResp = await http.PatchAsJsonAsync(
            $"{GraphBase}/applications/{appObjectId}", patchBody, ct);
        await EnsureSuccessAsync(patchResp, "update certificate");
        report(RenewSteps.UploadCert, SetupStepState.Done);

        // ── Step 4: Apply configuration ─────────────────────────────────────
        report(RenewSteps.ApplyConfig, SetupStepState.Running);
        report(RenewSteps.ApplyConfig, SetupStepState.Done);

        return new Result(tenantId, clientId, certSubject, thumbprint, notAfter);
    }

    // ── Graph query helpers ─────────────────────────────────────────────────

    private static async Task<JsonElement?> FindAppByNameAsync(
        HttpClient http, string displayName, CancellationToken ct)
    {
        var oDataName = displayName.Replace("'", "''");
        var resp = await http.GetAsync(
            $"{GraphBase}/applications?$filter=displayName eq '{oDataName}'&$select=id,appId&$top=1", ct);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        var arr = json.GetProperty("value");
        return arr.GetArrayLength() > 0 ? arr[0] : null;
    }

    private static async Task<JsonElement?> FindSpByAppIdAsync(
        HttpClient http, string appId, CancellationToken ct)
    {
        var resp = await http.GetAsync(
            $"{GraphBase}/servicePrincipals?$filter=appId eq '{appId}'&$select=id&$top=1", ct);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        var arr = json.GetProperty("value");
        return arr.GetArrayLength() > 0 ? arr[0] : null;
    }

    private static async Task<HashSet<string>> GetGrantedRoleIdsAsync(
        HttpClient http, string spId, CancellationToken ct)
    {
        var granted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resp = await http.GetAsync(
            $"{GraphBase}/servicePrincipals/{spId}/appRoleAssignments?$select=appRoleId", ct);
        if (!resp.IsSuccessStatusCode) return granted;
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        foreach (var assignment in json.GetProperty("value").EnumerateArray())
        {
            if (assignment.GetProperty("appRoleId").GetString() is { } roleId)
                granted.Add(roleId);
        }
        return granted;
    }

    // ── Certificate helpers ─────────────────────────────────────────────────

    private sealed record ExistingCertInfo(
        byte[] RawData,
        string Subject,
        string Thumbprint,
        DateTime NotAfter);

    /// <summary>
    /// Looks for a GraphMailer Entra Auth certificate in the local machine store.
    /// Returns the one with the latest expiry, or null if none exist.
    /// </summary>
    private static ExistingCertInfo? FindExistingCert()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var matches = store.Certificates
            .Find(X509FindType.FindBySubjectName, "GraphMailer Entra Auth", validOnly: false)
            .OfType<X509Certificate2>()
            .OrderByDescending(c => c.NotAfter)
            .FirstOrDefault();

        if (matches is null) return null;
        return new ExistingCertInfo(matches.RawData, matches.Subject,
                                    matches.Thumbprint, matches.NotAfter);
    }

    private static (byte[] RawData, string Subject, string Thumbprint, DateTime NotAfter)
        GenerateAndInstallCert(string appName)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=GraphMailer Entra Auth",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: false));
        req.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") }, critical: false));
        req.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, critical: true));
        req.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(req.PublicKey, critical: false));

        using var temp = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(2));

        var rawData = temp.RawData;

        var pfx = temp.Export(X509ContentType.Pfx, (string?)null);
        using var persistent = new X509Certificate2(
            pfx, (string?)null,
            X509KeyStorageFlags.MachineKeySet |
            X509KeyStorageFlags.PersistKeySet |
            X509KeyStorageFlags.Exportable);
        persistent.FriendlyName = $"GraphMailer Entra Authentication – {appName}";

        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        store.Add(persistent);

        return (rawData, persistent.Subject, persistent.Thumbprint, persistent.NotAfter);
    }

    // ── HTTP helpers ────────────────────────────────────────────────────────

    private static HttpClient CreateHttpClient(string accessToken)
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);
        http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException(
            $"Failed to {operation}: {(int)response.StatusCode} {response.ReasonPhrase}\n{body}");
    }
}