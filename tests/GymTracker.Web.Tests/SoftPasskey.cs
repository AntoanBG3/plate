using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GymTracker.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace GymTracker.Web.Tests;

/// <summary>
/// A software passkey (ES256) for tests: registered straight into the user's passkeys, then used to answer real
/// WebAuthn assertion options, so the server's whole assertion check runs.
/// </summary>
public sealed class SoftPasskey : IDisposable
{
    private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private uint counter;

    public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(16);
    public string UserId { get; private set; } = "";

    public static async Task<SoftPasskey> RegisterAsync(IServiceProvider services, string email)
    {
        var passkey = new SoftPasskey();
        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByEmailAsync(email) ?? throw new InvalidOperationException($"No user {email}.");
        passkey.UserId = user.Id;
        var result = await users.AddOrUpdatePasskeyAsync(user, new UserPasskeyInfo(passkey.CredentialId, passkey.CoseKey(), DateTimeOffset.UtcNow, 0,
            ["internal"], isUserVerified: true, isBackupEligible: false, isBackedUp: false, attestationObject: [0xA0], clientDataJson: Encoding.UTF8.GetBytes("{}")));
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join(" ", result.Errors.Select(e => e.Description)));
        }

        return passkey;
    }

    /// <summary>The credential JSON a browser would send for these request options.</summary>
    public string Assert(string optionsJson, bool userVerified = true, string origin = "https://localhost", string rpId = "localhost")
    {
        var options = JsonNode.Parse(optionsJson)!;
        var challenge = options["challenge"]!.GetValue<string>();
        var clientData = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "webauthn.get", challenge, origin, crossOrigin = false }));
        // rpIdHash, flags (user present, and user verified when asked), signature counter.
        var authData = new List<byte>(SHA256.HashData(Encoding.UTF8.GetBytes(rpId))) { (byte)(userVerified ? 0x05 : 0x01) };
        var count = ++counter;
        authData.AddRange([(byte)(count >> 24), (byte)(count >> 16), (byte)(count >> 8), (byte)count]);
        var signed = authData.Concat(SHA256.HashData(clientData)).ToArray();
        var signature = key.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        var id = WebEncoders.Base64UrlEncode(CredentialId);
        return JsonSerializer.Serialize(new
        {
            id,
            rawId = id,
            type = "public-key",
            authenticatorAttachment = "platform",
            clientExtensionResults = new { },
            response = new
            {
                clientDataJSON = WebEncoders.Base64UrlEncode(clientData),
                authenticatorData = WebEncoders.Base64UrlEncode(authData.ToArray()),
                signature = WebEncoders.Base64UrlEncode(signature),
                userHandle = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(UserId)),
            },
        });
    }

    /// <summary>The public key as a COSE_Key (EC2, ES256, P-256), CBOR-encoded by hand: a map of five entries.</summary>
    private byte[] CoseKey()
    {
        var p = key.ExportParameters(includePrivateParameters: false);
        return [0xA5, 0x01, 0x02, 0x03, 0x26, 0x20, 0x01, 0x21, 0x58, 0x20, .. p.Q.X!, 0x22, 0x58, 0x20, .. p.Q.Y!];
    }

    public void Dispose() => key.Dispose();
}
