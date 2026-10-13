using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class SovereignAccountTests
{
    private static readonly AccountConfiguration Configuration=new("https://example.clerk.accounts.dev","public-desktop-client");
    private static string Token(SecurityKey key,string? issuer=null,string? audience=null,bool verified=true,string nonce="expected",bool expired=false) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(issuer??Configuration.Issuer,audience??Configuration.ClientId,
            [new Claim("sub","user-one"),new Claim("name","Caesar"),new Claim("email","test@example.com"),new Claim("email_verified",verified?"true":"false",ClaimValueTypes.Boolean),new Claim("nonce",nonce)],
            DateTime.UtcNow.AddHours(-2),expired?DateTime.UtcNow.AddHours(-1):DateTime.UtcNow.AddMinutes(10),new SigningCredentials(key,SecurityAlgorithms.RsaSha256)));
    [Fact]
    public void Identity_requires_signature_issuer_audience_expiry_nonce_and_verified_email()
    {
        using var rsa=RSA.Create(2048);var key=new RsaSecurityKey(rsa);
        var identity=SovereignAccountClient.ValidateIdentity(Token(key),Configuration,[key],"expected");
        Assert.Equal("user-one",identity.Subject);Assert.Equal("Caesar",identity.Name);
        Assert.ThrowsAny<SecurityTokenException>(()=>SovereignAccountClient.ValidateIdentity(Token(key,issuer:"https://other.example"),Configuration,[key],"expected"));
        Assert.ThrowsAny<SecurityTokenException>(()=>SovereignAccountClient.ValidateIdentity(Token(key,audience:"different-client"),Configuration,[key],"expected"));
        Assert.ThrowsAny<SecurityTokenException>(()=>SovereignAccountClient.ValidateIdentity(Token(key,expired:true),Configuration,[key],"expected"));
        Assert.ThrowsAny<SecurityTokenException>(()=>SovereignAccountClient.ValidateIdentity(Token(key,nonce:"wrong"),Configuration,[key],"expected"));
        Assert.ThrowsAny<SecurityTokenException>(()=>SovereignAccountClient.ValidateIdentity(Token(key,verified:false),Configuration,[key],"expected"));
        using var other=RSA.Create(2048);
        Assert.ThrowsAny<SecurityTokenException>(()=>SovereignAccountClient.ValidateIdentity(Token(new RsaSecurityKey(other)),Configuration,[key],"expected"));
    }
    [Fact]
    public void Authorization_uses_S256_state_nonce_and_no_embedded_secret()
    {
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",SovereignAccountClient.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
        using var client=new SovereignAccountClient(Configuration);
        var url=client.AuthorizationUri("http://127.0.0.1:12345/callback","state","nonce","verifier").AbsoluteUri;
        Assert.Contains("code_challenge_method=S256",url);Assert.Contains("state=state",url);Assert.Contains("nonce=nonce",url);
        Assert.DoesNotContain("prompt=",url); // Clerk rejects select_account.
        Assert.DoesNotContain("client_secret",url);Assert.DoesNotContain("code_verifier",url);
        Assert.False(SovereignAccountClient.StateMatches("expected","wrong"));
        Assert.False(SovereignAccountClient.StateMatches("expected","Expected"));
    }
    [Fact]
    public async Task Verified_room_rejects_unsigned_join_and_uses_signed_name_instead_of_claimed_name()
    {
        using var rsa=RSA.Create(2048);var key=new RsaSecurityKey(rsa);
        Task<AccountIdentity> Verify(string proof,CancellationToken ct)=>Task.FromResult(SovereignAccountClient.ValidateIdentity(proof,Configuration,[key],null));
        var owner=new AccountIdentity("owner","Micha","owner@example.com",DateTimeOffset.UtcNow.AddMinutes(10));
        var folder=Path.Combine(Path.GetTempPath(),"sovereign-account-test-"+Guid.NewGuid().ToString("N"));
        await using var host=new SharedRoomHost(folder,owner,Verify);await host.StartAsync();
        var invitation=host.NewInvitation(host.Address);
        await Assert.ThrowsAsync<IOException>(()=>RoomConnection.JoinAsync(invitation,"Spoofed",CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(()=>RoomConnection.JoinAsync(invitation,"Spoofed",CancellationToken.None,"garbage"));
        using var peer=await RoomConnection.JoinAsync(invitation,"Spoofed",CancellationToken.None,Token(key));
        Assert.Equal("Caesar",peer.Session.Name);Assert.Equal("user-one",peer.Session.AccountSubject);
        await peer.SendAsync(new RoomPost(Guid.NewGuid(),"human","","Hello"),CancellationToken.None);
        var transcript=await File.ReadAllTextAsync(Path.Combine(folder,"shared-rooms",host.RoomId,"transcript.json"));
        Assert.DoesNotContain("test@example.com",transcript);Assert.DoesNotContain("eyJ",transcript);
        await Assert.ThrowsAsync<IOException>(()=>RoomConnection.JoinAsync(host.NewInvitation(host.Address),"Other name",CancellationToken.None,Token(key)));
    }
    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://example.com/path")]
    [InlineData("https://user:password@example.com")]
    public void Insecure_or_ambiguous_configuration_is_rejected(string issuer)=>Assert.Throws<IOException>(()=>new AccountConfiguration(issuer,"client").Validate());
}
