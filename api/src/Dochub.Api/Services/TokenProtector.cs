using Microsoft.AspNetCore.DataProtection;

namespace Dochub.Api.Services;

/// <summary>Encrypts source-system OAuth tokens at rest.</summary>
public interface ITokenProtector
{
    string Protect(string value);
    string Unprotect(string value);
}

public class TokenProtector(IDataProtectionProvider provider) : ITokenProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("Dochub.SourceConnection.Tokens.v1");

    public string Protect(string value) => _protector.Protect(value);
    public string Unprotect(string value) => _protector.Unprotect(value);
}
