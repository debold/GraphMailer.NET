using GraphMailer.Service.Configuration;

namespace GraphMailer.Service.Services;

/// <summary>
/// One Graph application permission GraphMailer may need, with the Entra app-role id used to
/// grant it and the plain-language reason it is required. <see cref="Purpose"/> goes straight
/// into operator-facing text, so it reads as the tail of "needed for …".
/// </summary>
internal sealed record GraphAppRole(string RoleId, string Name, string Purpose);

/// <summary>
/// The single source of truth for the Graph application permissions GraphMailer needs.
///
/// Three code paths depend on this list and used to keep their own copy: the Entra setup wizard
/// (which grants them), the Graph monitor (which alerts on gaps) and the ConfigTool (which now
/// shows the gap). They drifted apart once already — 1.5.0 added Domain.Read.All and
/// Group.Read.All to the wizard and the monitor, and installations upgraded from 1.4.x got an
/// alert mail with nothing in the ConfigTool to act on.
/// </summary>
internal static class GraphPermissions
{
    // Application permissions on the Microsoft Graph service principal. The ids are Graph's own
    // and identical in every tenant.
    internal static readonly GraphAppRole MailSend =
        new("b633e1c5-b582-4048-a93e-9f11b44c7e96", "Mail.Send", "mail delivery");

    /// <summary>Attachments ≥ 3 MB: Graph's sendMail request is capped at 4 MB, so large mail goes
    /// through a draft + upload session — mailbox writes that Mail.Send does not cover.</summary>
    internal static readonly GraphAppRole MailReadWrite =
        new("e2a3a72e-5f79-4c64-b1b1-878b674786c9", "Mail.ReadWrite", "attachments ≥ 3 MB");

    internal static readonly GraphAppRole UserReadAll =
        new("df021288-bdef-4463-88db-98f22de89214", "User.Read.All", "sender validation");

    /// <summary>Mail-enabled groups as senders, behind SenderValidation.AcceptMailboxlessSenders.</summary>
    internal static readonly GraphAppRole GroupReadAll =
        new("5b567255-7703-4780-807c-7be8301ae99b", "Group.Read.All", "groups as senders");

    /// <summary>The verified tenant mail domains, which decide which of a mailbox's synced
    /// addresses may send at all — read whenever sender validation runs, not only for the
    /// mailbox-less opt-in.</summary>
    internal static readonly GraphAppRole DomainReadAll =
        new("dbb9058a-0e50-45d7-ae91-66909b5d4664", "Domain.Read.All", "recognising the tenant's mail domains");

    /// <summary>
    /// Every permission the wizard grants, regardless of configuration. Granting the optional
    /// directory roles up front means switching the matching ConfigTool option on later does not
    /// need a second trip to the Entra portal; they stay unused until then.
    /// </summary>
    internal static readonly IReadOnlyList<GraphAppRole> All =
    [
        MailSend,
        MailReadWrite,
        UserReadAll,
        GroupReadAll,
        DomainReadAll,
    ];

    /// <summary>
    /// The permissions the current configuration actually needs. A role that is not required
    /// must not be reported as missing — an installation without sender validation runs
    /// correctly on Mail.Send + Mail.ReadWrite alone.
    /// </summary>
    internal static IReadOnlyList<GraphAppRole> Required(SenderValidationOptions senderValidation)
    {
        var required = new List<GraphAppRole> { MailSend, MailReadWrite };

        if (senderValidation.Enabled)
        {
            required.Add(UserReadAll);
            required.Add(DomainReadAll);

            if (senderValidation.AcceptMailboxlessSenders)
                required.Add(GroupReadAll);
        }

        return required;
    }

    /// <summary>
    /// The required permissions absent from <paramref name="granted"/> (the token's "roles"
    /// claim), in the order of <see cref="Required"/>.
    /// </summary>
    internal static IReadOnlyList<GraphAppRole> Missing(
        IReadOnlyCollection<string> granted,
        SenderValidationOptions senderValidation)
        => [.. Required(senderValidation)
                .Where(r => !granted.Contains(r.Name, StringComparer.OrdinalIgnoreCase))];

    /// <summary>"Mail.ReadWrite, User.Read.All" — the bare role names, for a compact one-liner.</summary>
    internal static string NameList(IEnumerable<GraphAppRole> roles)
        => string.Join(", ", roles.Select(r => r.Name));

    /// <summary>"Mail.ReadWrite (needed for attachments ≥ 3 MB), …" — the operator-facing detail.</summary>
    internal static string DetailList(IEnumerable<GraphAppRole> roles)
        => string.Join(", ", roles.Select(r => $"{r.Name} (needed for {r.Purpose})"));
}
