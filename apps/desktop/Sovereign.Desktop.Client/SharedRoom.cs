using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Sovereign.Desktop.Client;

public sealed record RoomMessage(long Sequence, Guid RequestId, string ParticipantId, string Name, string Kind, string Model, string Text, DateTimeOffset Created);
public sealed record RoomMember(string Id, string Name, bool Host = false);
public sealed record RoomSnapshot(string RoomId, RoomMember[] Members, RoomMessage[] Messages);
public sealed record RoomJoin(string Invite, string Name, string? IdentityToken = null);
public sealed record RoomSession(string RoomId, string ParticipantId, string Name, string Token, string? AccountSubject = null, DateTimeOffset? ExpiresAt = null);
public sealed record RoomPost(Guid RequestId, string Kind, string Model, string Text);

/// <summary>One explicitly hosted room. Loopback only; a separate tunnel provides remote TLS.</summary>
public sealed class SharedRoomHost : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Func<string,CancellationToken,Task<AccountIdentity>>? _verifyIdentity;
    private readonly Dictionary<string, RoomSession> _sessions = new();
    private readonly List<RoomMessage> _messages = [];
    private readonly string _transcript;
    private string _invite = "";
    private DateTimeOffset _inviteExpires;
    private DateTimeOffset _joinWindow = DateTimeOffset.UtcNow;
    private int _joinAttempts;
    private WebApplication? _app;
    public string RoomId { get; } = Guid.NewGuid().ToString("N");
    public Uri Address { get; private set; } = null!;
    public RoomSession Owner { get; }
    private static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private static string Key(string token) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
    public SharedRoomHost(string profile, string name) : this(profile,name,null,null) { }
    public SharedRoomHost(string profile, AccountIdentity owner, Func<string,CancellationToken,Task<AccountIdentity>> verifyIdentity) : this(profile,owner.Name,owner,verifyIdentity) { }
    private SharedRoomHost(string profile, string name, AccountIdentity? owner, Func<string,CancellationToken,Task<AccountIdentity>>? verifyIdentity)
    {
        _verifyIdentity=verifyIdentity;
        ValidateName(name);
        var folder = Path.Combine(profile, "shared-rooms", RoomId); Directory.CreateDirectory(folder);
        _transcript = Path.Combine(folder, "transcript.json");
        Owner = AddMember(name.Trim(),owner);
    }
    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 40 || name.Any(char.IsControl)) throw new ArgumentException("Use a name of 1–40 characters.");
    }
    private RoomSession AddMember(string name, AccountIdentity? identity = null)
    {
        var member = new RoomSession(RoomId, Guid.NewGuid().ToString("N"), name, Secret(),identity?.Subject,identity?.ExpiresAt);
        _sessions.Add(Key(member.Token), member); return member;
    }
    public string NewInvitation(Uri publicAddress)
    {
        RoomConnection.ValidateAddress(publicAddress);
        lock (_gate) { _invite = Secret(); _inviteExpires = DateTimeOffset.UtcNow.AddMinutes(20); return publicAddress.GetLeftPart(UriPartial.Authority) + "/#" + RoomId + "." + _invite; }
    }
    public async Task StartAsync(CancellationToken ct = default)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(SharedRoomHost).Assembly.FullName });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => { o.Listen(IPAddress.Loopback, 0); o.Limits.MaxRequestBodySize = 32768; o.Limits.MaxConcurrentConnections = 64; o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10); });
        var app = builder.Build(); _app = app;
        app.Use(async (context, next) => {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (context.Request.Headers.ContainsKey("Origin")) { context.Response.StatusCode = 403; return; }
            try { await next(context); }
            catch (Exception ex) when (ex is JsonException or ArgumentException or BadHttpRequestException) { context.Response.StatusCode = 400; }
        });
        app.MapGet("/", () => Results.Text("SI Desk shared-room beta. Open the invitation inside SI Desk. No browser chat or file access."));
        app.MapPost("/room/{id}/join", async (string id, HttpContext c) => {
            var body = await c.Request.ReadFromJsonAsync<RoomJoin>(c.RequestAborted);
            if (body is null) return Results.BadRequest();
            lock (_gate) {
                if (DateTimeOffset.UtcNow - _joinWindow > TimeSpan.FromMinutes(1)) { _joinWindow = DateTimeOffset.UtcNow; _joinAttempts = 0; }
                if (++_joinAttempts > 30) return Results.StatusCode(429);
                if (!InviteMatches(id,body.Invite)) return Results.Unauthorized();
            }
            AccountIdentity? identity=null;
            if(_verifyIdentity is not null) {
                if(string.IsNullOrEmpty(body.IdentityToken)) return Results.Unauthorized();
                try { identity=await _verifyIdentity(body.IdentityToken,c.RequestAborted); }
                catch { return Results.Unauthorized(); }
                if(identity.ExpiresAt <= DateTimeOffset.UtcNow) return Results.Unauthorized();
            }
            var name=identity?.Name??body.Name; ValidateName(name);
            lock (_gate) {
                // Recheck after network verification: concurrent joins cannot reuse an invitation.
                if(!InviteMatches(id,body.Invite)) return Results.Unauthorized();
                if (_sessions.Count >= 8 || _sessions.Values.Any(s => identity is not null ? s.AccountSubject==identity.Subject : s.Name.Equals(name.Trim(),StringComparison.OrdinalIgnoreCase))) return Results.Conflict();
                var session = AddMember(name.Trim(),identity); _invite = ""; return Results.Json(session);
            }
        });
        app.MapGet("/room/{id}/messages", (string id, long? after, HttpContext c) => {
            lock (_gate) {
                if (Authenticate(id, c) is null) return Results.Unauthorized();
                return Results.Json(new RoomSnapshot(RoomId, _sessions.Values.Select(s => new RoomMember(s.ParticipantId, s.Name, s.ParticipantId == Owner.ParticipantId)).ToArray(), _messages.Where(m => m.Sequence > (after ?? 0)).Take(100).ToArray()));
            }
        });
        app.MapPost("/room/{id}/leave", (string id, HttpContext c) => {
            lock (_gate) { var session = Authenticate(id,c); if(session is null) return Results.Unauthorized(); _sessions.Remove(Key(session.Token)); return Results.Ok(); }
        });
        app.MapPost("/room/{id}/messages", async (string id, HttpContext c) => {
            RoomSession? session; lock (_gate) session = Authenticate(id, c);
            if (session is null) return Results.Unauthorized();
            var post = await c.Request.ReadFromJsonAsync<RoomPost>(c.RequestAborted);
            if (post is null || post.RequestId == Guid.Empty || post.Kind is not ("human" or "ai") || string.IsNullOrWhiteSpace(post.Text) || post.Text.Length > 8000 || post.Model is null || post.Model.Length > 100) return Results.BadRequest();
            lock (_gate) {
                var prior = _messages.FirstOrDefault(m => m.ParticipantId == session.ParticipantId && m.RequestId == post.RequestId);
                if (prior is not null) return prior.Text == post.Text && prior.Kind == post.Kind && prior.Model == post.Model ? Results.Json(prior) : Results.Conflict();
                if (_messages.Count >= 500) return Results.StatusCode(507);
                if (_messages.Count(m => m.ParticipantId == session.ParticipantId && m.Created > DateTimeOffset.UtcNow.AddSeconds(-10)) >= 10) return Results.StatusCode(429);
                var message = new RoomMessage(_messages.Count + 1, post.RequestId, session.ParticipantId, session.Name, post.Kind, post.Kind == "ai" ? post.Model : "", post.Text, DateTimeOffset.UtcNow);
                // Persist before acknowledging. Neither invites nor session/provider tokens are saved here.
                var updated = _messages.Append(message).ToArray();
                File.WriteAllText(_transcript + ".tmp", JsonSerializer.Serialize(updated)); File.Move(_transcript + ".tmp", _transcript, true);
                _messages.Add(message); return Results.Json(message);
            }
        });
        await app.StartAsync(ct); Address = new Uri(app.Urls.Single());
    }

    /// <summary>Posts one Shared room line as Grok Bot. Does not use a member token.</summary>
    public RoomMessage AppendNamed(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 8000 || text.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'))
            throw new ArgumentException("Grok Bot message is empty, too long, or has unsupported characters.");
        lock (_gate)
        {
            if (_messages.Count >= 500) throw new IOException("Shared room is full.");
            var message = new RoomMessage(_messages.Count + 1, Guid.NewGuid(), "grok-bot", "Grok Bot", "human", "", text.Trim(), DateTimeOffset.UtcNow);
            var updated = _messages.Append(message).ToArray();
            File.WriteAllText(_transcript + ".tmp", JsonSerializer.Serialize(updated));
            File.Move(_transcript + ".tmp", _transcript, true);
            _messages.Add(message);
            return message;
        }
    }
    private bool InviteMatches(string id,string? invite) => id==RoomId && !string.IsNullOrEmpty(_invite) && DateTimeOffset.UtcNow<=_inviteExpires && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(invite??""),System.Text.Encoding.UTF8.GetBytes(_invite));
    private RoomSession? Authenticate(string id, HttpContext c)
    {
        if (id != RoomId) return null;
        var header = c.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.Ordinal) && _sessions.TryGetValue(Key(header[7..]), out var session) && (session.ExpiresAt is null || session.ExpiresAt>DateTimeOffset.UtcNow) ? session : null;
    }
    public async ValueTask DisposeAsync() { if (_app is { } app) { _app = null; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await app.StopAsync(timeout.Token); await app.DisposeAsync(); } lock (_gate) { _sessions.Clear(); _invite = ""; } }
}

public sealed class RoomConnection : IDisposable
{
    private readonly HttpClient _http;
    public RoomSession Session { get; }
    public static void ValidateAddress(Uri address)
    {
        if (address.Scheme != "https" && !(address.Scheme == "http" && address.IsLoopback)) throw new ArgumentException("Remote rooms require HTTPS.");
        if (!string.IsNullOrEmpty(address.UserInfo) || address.AbsolutePath != "/" || !string.IsNullOrEmpty(address.Query)) throw new ArgumentException("Use a room invitation, not a custom URL path.");
    }
    private static HttpClient Http(Uri address) { ValidateAddress(address); return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = new Uri(address.GetLeftPart(UriPartial.Authority)), Timeout = TimeSpan.FromSeconds(12), MaxResponseContentBufferSize = 2_000_000 }; }
    public RoomConnection(Uri address, RoomSession session) { _http = Http(address); Session = session; _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token); }
    public static async Task<RoomConnection> JoinAsync(string invitation, string name, CancellationToken ct, string? identityToken = null, string? expectedSubject = null)
    {
        var uri = new Uri(invitation.Trim()); ValidateAddress(uri);
        var parts = uri.Fragment.TrimStart('#').Split('.');
        if (parts.Length != 2 || parts[0].Length != 32 || parts[1].Length != 64 || !parts.All(p => p.All(Uri.IsHexDigit))) throw new ArgumentException("Paste the full invitation including the part after #.");
        using var http = Http(uri);
        using var reply = await http.PostAsJsonAsync("/room/" + parts[0] + "/join", new RoomJoin(parts[1], name, identityToken), ct); Check(reply);
        var session = await reply.Content.ReadFromJsonAsync<RoomSession>(ct) ?? throw new IOException("Missing room session.");
        if (session.RoomId != parts[0]) throw new IOException("Room identity mismatch.");
        if(identityToken is not null && (string.IsNullOrEmpty(session.AccountSubject) || session.ExpiresAt is null || session.ExpiresAt<=DateTimeOffset.UtcNow || (expectedSubject is not null && session.AccountSubject!=expectedSubject)))
            throw new IOException("This room did not confirm your account identity. Both participants need the account-enabled app.");
        return new RoomConnection(uri, session);
    }
    private static void Check(HttpResponseMessage r) { if (!r.IsSuccessStatusCode) throw new IOException($"Room returned HTTP {(int)r.StatusCode}. Invitation may be expired/used, name duplicated, or room stopped. No message delivery is assumed."); }
    public async Task<RoomSnapshot> ReadAsync(long after, CancellationToken ct) { using var r = await _http.GetAsync($"/room/{Session.RoomId}/messages?after={after}", ct); Check(r); return await r.Content.ReadFromJsonAsync<RoomSnapshot>(ct) ?? throw new IOException("Missing room history."); }
    public async Task<RoomMessage> SendAsync(RoomPost post, CancellationToken ct) { using var r = await _http.PostAsJsonAsync($"/room/{Session.RoomId}/messages", post, ct); Check(r); return await r.Content.ReadFromJsonAsync<RoomMessage>(ct) ?? throw new IOException("Missing message acknowledgement."); }
    public async Task LeaveAsync(CancellationToken ct) { using var r = await _http.PostAsync($"/room/{Session.RoomId}/leave", null, ct); Check(r); }
    public void Dispose() => _http.Dispose();
}
