using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration.Json;

namespace GraphMailer.Service.Infrastructure.Encryption;

internal sealed class EncryptedJsonConfigurationSource : JsonConfigurationSource
{
    public required IDataProtector Protector { get; init; }

    public override IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        EnsureDefaults(builder);
        return new EncryptedJsonConfigurationProvider(this, Protector);
    }
}
