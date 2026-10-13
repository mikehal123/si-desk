using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Sovereign.Desktop.Client;

public sealed record AccountConfiguration(string Issuer, string ClientId)
{
    public void Validate()
    {
        if (!Uri.TryCreate(Issuer, UriKind.Absolute, out var u) || u.Scheme != "https" || u.AbsolutePath != "/" || u.UserInfo.Length != 0 || u.Query.Length != 0 || u.Fragment.Length != 0)
            throw new IOException("Invalid account service address.");
        if (string.IsNullOrWhiteSpace(ClientId) || ClientId.Length > 256 || ClientId.Any(char.IsControl))
            throw new IOException("Sovereign sign-in is not configured. The release needs its public Clerk Client ID.");
    }
}

public sealed record AccountIdentity(string Subject, string Name, string Email, DateTimeOffset ExpiresAt);
// Never write this record to diagnostics, transcripts, or ordinary profile JSON.
public sealed record AccountSession(string AccessToken, string RefreshToken, string IdToken, DateTimeOffset ExpiresAt);

public sealed class SovereignAccountClient : IDisposable
{
    public string SignInStage { get; private set; } = "Not started";
    private readonly AccountConfiguration _configuration;
    private readonly HttpClient _http;
    private IReadOnlyCollection<SecurityKey>? _keys;
    private DateTimeOffset _keysAt;
    public SovereignAccountClient(AccountConfiguration configuration, HttpMessageHandler? handler = null)
    {
        configuration.Validate(); _configuration = configuration;
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { BaseAddress = new Uri(configuration.Issuer.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 128_000 };
    }
    internal static string RandomValue() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
    internal static string Challenge(string verifier) => Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    internal static bool StateMatches(string expected, string actual) => expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
    internal Uri AuthorizationUri(string redirect, string state, string nonce, string verifier)
    {
        var fields = new Dictionary<string,string> { ["response_type"]="code", ["client_id"]=_configuration.ClientId,
            ["redirect_uri"]=redirect, ["scope"]="openid profile email offline_access", ["state"]=state, ["nonce"]=nonce,
            ["code_challenge"]=Challenge(verifier), ["code_challenge_method"]="S256" };
        return new Uri(_http.BaseAddress!, "oauth/authorize?" + string.Join("&", fields.Select(x => Uri.EscapeDataString(x.Key)+"="+Uri.EscapeDataString(x.Value))));
    }
    public async Task<(AccountSession Session, AccountIdentity Identity)> SignInAsync(Func<Uri,Task> openBrowser, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var ct=timeout.Token; var state=RandomValue(); var nonce=RandomValue(); var verifier=RandomValue();
        var received=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder=WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args=[], ApplicationName=typeof(SovereignAccountClient).Assembly.FullName });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => { o.Listen(IPAddress.Loopback,0); o.Limits.MaxRequestBodySize=0; o.Limits.MaxRequestLineSize=8192; o.Limits.MaxConcurrentConnections=8; });
        await using var app=builder.Build();
        app.MapGet("/callback", async context => {
            context.Response.Headers.CacheControl="no-store";
            context.Response.Headers["Content-Security-Policy"]="default-src 'none'";
            if (context.Request.Query["state"].Count != 1 || !StateMatches(state,context.Request.Query["state"].ToString())) { context.Response.StatusCode=400; await context.Response.WriteAsync("Invalid sign-in response."); return; }
            var code=context.Request.Query["code"];
            if (code.Count != 1 || string.IsNullOrWhiteSpace(code.ToString()) || context.Request.Query.ContainsKey("error")) {
                await context.Response.WriteAsync("Sign-in was not completed. Return to xAI Desk."); received.TrySetException(new IOException("Sign-in was denied or canceled.")); return;
            }
            await context.Response.WriteAsync("Sign-in response received. Return to xAI Desk to finish verification.");
            await context.Response.CompleteAsync(); received.TrySetResult(code.ToString());
        });
        SignInStage="Opening local callback listener";
        await app.StartAsync(ct);
        try {
            var redirect=app.Urls.Single().TrimEnd('/')+"/callback";
            SignInStage="Waiting for browser callback";
            await openBrowser(AuthorizationUri(redirect,state,nonce,verifier));
            var code=await received.Task.WaitAsync(ct);
            SignInStage="Exchanging authorization code";
            var session=await ExchangeAsync(new() { ["grant_type"]="authorization_code", ["code"]=code, ["redirect_uri"]=redirect, ["code_verifier"]=verifier },ct);
            SignInStage="Verifying signed identity";
            var identity=await VerifyIdentityAsync(session.IdToken,nonce,ct);
            SignInStage="Identity verified";
            return (session,identity);
        } finally { using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(3)); await app.StopAsync(stop.Token); }
    }
    public async Task<(AccountSession Session,AccountIdentity Identity)> RefreshAsync(AccountSession old,CancellationToken ct)
    {
        var current=old;
        if (old.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2)) {
            if (string.IsNullOrEmpty(old.RefreshToken)) throw new IOException("Please sign in again.");
            current=await ExchangeAsync(new() { ["grant_type"]="refresh_token",["refresh_token"]=old.RefreshToken },ct);
            if (string.IsNullOrEmpty(current.RefreshToken)) current=current with { RefreshToken=old.RefreshToken };
        }
        var identity=await VerifyIdentityAsync(current.IdToken,null,ct);
        // Check the live service as well: locally valid signatures alone do not establish revocation status.
        using var request=new HttpRequestMessage(HttpMethod.Get,"oauth/userinfo");
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",current.AccessToken);
        using var response=await _http.SendAsync(request,ct);
        if(!response.IsSuccessStatusCode) throw new IOException("Your Sovereign session could not be verified. Please sign in again.");
        using var user=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if(user.RootElement.GetProperty("sub").GetString()!=identity.Subject) throw new IOException("Account identity mismatch.");
        return(current,identity);
    }
    private async Task<AccountSession> ExchangeAsync(Dictionary<string,string> form,CancellationToken ct)
    {
        form["client_id"]=_configuration.ClientId;
        using var reply=await _http.PostAsync("oauth/token",new FormUrlEncodedContent(form),ct);
        if(!reply.IsSuccessStatusCode) throw new IOException($"Sign-in service returned HTTP {(int)reply.StatusCode}. Please retry sign-in.");
        using var doc=JsonDocument.Parse(await reply.Content.ReadAsStringAsync(ct)); var r=doc.RootElement;
        string Field(string name)=>r.TryGetProperty(name,out var v)?v.GetString()??"":"";
        if(!string.Equals(Field("token_type"),"Bearer",StringComparison.OrdinalIgnoreCase)||string.IsNullOrEmpty(Field("access_token"))||string.IsNullOrEmpty(Field("id_token"))) throw new IOException("Sign-in response did not include a verified account session.");
        var seconds=r.GetProperty("expires_in").GetInt32(); if(seconds<=0||seconds>604800) throw new IOException("Invalid session expiry.");
        return new(Field("access_token"),Field("refresh_token"),Field("id_token"),DateTimeOffset.UtcNow.AddSeconds(seconds));
    }
    public async Task<AccountIdentity> VerifyIdentityAsync(string token,string? nonce,CancellationToken ct)
    {
        if(token.Length>32000) throw new IOException("Invalid identity token.");
        if(_keys is null || DateTimeOffset.UtcNow-_keysAt>TimeSpan.FromMinutes(15)) await LoadKeysAsync(ct);
        try { return ValidateIdentity(token,_configuration,_keys!,nonce); }
        catch(SecurityTokenSignatureKeyNotFoundException) { await LoadKeysAsync(ct); return ValidateIdentity(token,_configuration,_keys!,nonce); }
    }
    private async Task LoadKeysAsync(CancellationToken ct)
    {
        var json=await _http.GetStringAsync(".well-known/jwks.json",ct);
        _keys=new JsonWebKeySet(json).GetSigningKeys().ToArray(); _keysAt=DateTimeOffset.UtcNow;
    }
    internal static AccountIdentity ValidateIdentity(string token,AccountConfiguration configuration,IEnumerable<SecurityKey> keys,string? nonce)
    {
        var handler=new JwtSecurityTokenHandler { MapInboundClaims=false, MaximumTokenSizeInBytes=32000 };
        var principal=handler.ValidateToken(token,new TokenValidationParameters {
            RequireSignedTokens=true, ValidateIssuerSigningKey=true, IssuerSigningKeys=keys, ValidAlgorithms=[SecurityAlgorithms.RsaSha256],
            ValidateIssuer=true, ValidIssuer=configuration.Issuer.TrimEnd('/'), ValidateAudience=true, ValidAudience=configuration.ClientId,
            RequireExpirationTime=true, ValidateLifetime=true, ClockSkew=TimeSpan.FromSeconds(30)
        },out _);
        string Claim(string type)=>principal.FindFirst(type)?.Value??"";
        if(nonce is not null && !StateMatches(nonce,Claim("nonce"))) throw new SecurityTokenValidationException("Invalid sign-in nonce.");
        if(string.IsNullOrWhiteSpace(Claim("sub"))) throw new SecurityTokenValidationException("Missing account identity.");
        if(!string.Equals(Claim("email_verified"),"true",StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(Claim("email")))
            throw new SecurityTokenValidationException("A verified email address is required.");
        var name=Claim("name").Trim();
        if(string.IsNullOrEmpty(name)) name=(Claim("given_name")+" "+Claim("family_name")).Trim();
        if(string.IsNullOrEmpty(name)) name="Sovereign member";
        name=new string(name.Where(c=>!char.IsControl(c)).Take(40).ToArray());
        return new(Claim("sub"),name,Claim("email"),DateTimeOffset.FromUnixTimeSeconds(long.Parse(Claim("exp"),System.Globalization.CultureInfo.InvariantCulture)));
    }
    public async Task RevokeAsync(AccountSession session,CancellationToken ct)
    {
        foreach(var token in new[] { session.RefreshToken,session.AccessToken }.Where(t=>!string.IsNullOrEmpty(t))) {
            using var reply=await _http.PostAsync("oauth/token/revoke",new FormUrlEncodedContent(new Dictionary<string,string> { ["token"]=token,["client_id"]=_configuration.ClientId }),ct);
            if(!reply.IsSuccessStatusCode) throw new IOException("Signed out locally; the service could not confirm revocation.");
        }
    }
    public void Dispose()=>_http.Dispose();
}
