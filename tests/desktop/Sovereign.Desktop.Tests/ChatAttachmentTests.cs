using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class ChatAttachmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Sovereign-attachment-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _chat = Guid.NewGuid().ToString("N");
    private ChatAttachmentStore Store => new(_root, _chat);
    private Task<ChatAttachment> Import(string name, string text) => Store.ImportAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), name, CancellationToken.None);
    [Fact] public async Task Owned_copy_survives_original_move_and_archive_reload()
    {
        Directory.CreateDirectory(_root); var source = Path.Combine(_root, "Sov 测试 data.csv"); File.WriteAllText(source, "ticker,price\nABC,42");
        var item = await Store.ImportAsync(source, CancellationToken.None); File.Move(source, source + ".moved");
        var archive = new ChatArchive(_root); archive.Save(ChatArchive.New() with { Id = _chat, DraftAttachments = [item] });
        var loaded = Assert.Single(archive.Load()); var restored = Assert.Single(loaded.DraftAttachments!);
        Assert.Equal("text", restored.Kind); Assert.Contains("ABC,42", restored.ExtractedText);
        Assert.Equal("ticker,price\nABC,42", Encoding.UTF8.GetString(await Store.ReadVerifiedAsync(restored, CancellationToken.None)));
    }
    [Fact] public async Task Same_names_have_distinct_copies_and_deletion_is_conversation_scoped()
    {
        var a = await Import("report.txt", "first"); var b = await Import("report.txt", "second"); Assert.NotEqual(a.Id, b.Id);
        var otherChat = Guid.NewGuid().ToString("N"); var other = new ChatAttachmentStore(_root, otherChat);
        var c = await other.ImportAsync(new MemoryStream("keep"u8.ToArray()), "report.txt", default);
        new ChatArchive(_root).Delete(_chat);
        Assert.False(File.Exists(Store.PathFor(a))); Assert.False(File.Exists(Store.PathFor(b)));
        Assert.Equal("keep", Encoding.UTF8.GetString(await other.ReadVerifiedAsync(c, default)));
        Assert.Throws<ArgumentException>(() => new ChatAttachmentStore(_root, "../escape"));
        Assert.Throws<ArgumentException>(() => other.PathFor(c with { Id = "../escape" }));
    }
    [Fact] public async Task Changed_copy_is_not_sent()
    {
        var a = await Import("a.txt", "hello"); File.WriteAllText(Store.PathFor(a), "world");
        await Assert.ThrowsAsync<IOException>(() => Store.ReadVerifiedAsync(a, default));
    }
    [Fact] public async Task Large_text_is_visibly_truncated_and_context_identifies_source()
    {
        var a = await Import("large.md", new string('x', 80000)); Assert.Contains("truncated", a.Note);
        var prompt = ChatAttachmentStore.WithContext("summarize", [a]); Assert.Contains("EXTRACTION TRUNCATED", prompt); Assert.Contains("large.md", prompt); Assert.Contains("untrusted", prompt);
    }
    [Theory] [InlineData("clip.mp4", "video")] [InlineData("sound.wav", "audio")] [InlineData("stuff.zip", "file")]
    public async Task Unsupported_analysis_is_explicit_not_fake_extracted_text(string name, string kind)
    { var a = await Import(name, "bytes"); Assert.Equal(kind, a.Kind); Assert.Null(a.ExtractedText); }
    [Fact] public async Task Docx_text_is_extracted_without_executing_embedded_content()
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        { using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open()); writer.Write("<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><w:body><w:p><w:r><w:t>Attachment test 1729</w:t></w:r></w:p></w:body></w:document>"); }
        bytes.Position = 0; var a = await Store.ImportAsync(bytes, "test.docx", default); Assert.Contains("1729", a.ExtractedText);
    }
    [Fact] public async Task Corrupt_pdf_is_saved_but_not_claimed_readable()
    { var a = await Import("broken.pdf", "not pdf"); Assert.Equal("file", a.Kind); Assert.Contains("extraction failed", a.Note); }
    [Fact] public async Task Cancel_does_not_leave_partial_copy()
    {
        using var token = new CancellationTokenSource(); token.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.ImportAsync(new MemoryStream(new byte[100]), "a.txt", token.Token));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "chats", "attachments", _chat)));
    }
    [Fact] public async Task Reconnect_text_context_keeps_document_extraction_and_attachment_metadata()
    {
        var a = await Import("a.csv", "secret-test-value,1729"); var sent = ChatAttachmentStore.WithContext("read", [a]);
        var chat = ChatArchive.New() with { Turns = [new("read", "test", "answer", "completed", [], [a], sent)] };
        var context = ChatArchive.RestoreContext(chat, "what was the value?"); Assert.Contains("1729", context); Assert.Contains("a.csv", context);
    }
    [Fact] public async Task Cursor_images_require_advertised_capability_and_include_bytes()
    {
        string Reply(int id, object result) => JsonSerializer.Serialize(new { id, result });
        var lines = new[] { Reply(1, new { protocolVersion = 1, agentCapabilities = new { promptCapabilities = new { image = true } } }),
            Reply(2, new { sessionId = "s", models = new { availableModels = new[] { new { modelId = "test", name = "test" } } } }), Reply(3, new {}), Reply(4, new {}),
            JsonSerializer.Serialize(new { method = "session/update", @params = new { sessionId = "s", update = new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "seen" } } } }), Reply(5, new { stopReason = "end_turn" }) };
        var output = new StringWriter(); var client = new CursorAcpSession(new StringReader(string.Join('\n', lines) + "\n"), output);
        await client.ConnectAsync(_root, default); Assert.True(client.SupportsImages);
        await client.SendAsync("test", "look", _ => {}, default, [new("image/png", "aGVsbG8=")]);
        using var prompt = JsonDocument.Parse(output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Last());
        var image = prompt.RootElement.GetProperty("params").GetProperty("prompt")[1]; Assert.Equal("image", image.GetProperty("type").GetString()); Assert.Equal("aGVsbG8=", image.GetProperty("data").GetString());
    }
    [Fact] public void Restored_document_context_obeys_history_budget()
    {
        var chat = ChatArchive.New() with { Turns = [new("small prompt", "test", "answer", "completed", [], [], new string('x', 110000))] };
        var context = ChatArchive.RestoreContext(chat, "next");
        Assert.Contains("omitted", context); Assert.True(context.Length < 2000);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task Ollama_sends_image_bytes_only_to_advertised_vision_model(bool vision)
    {
        var handler = new ImageHandler(vision); using var client = new OllamaChatClient(handler);
        if (vision) {
            await client.SendAsync("test", [new("user", "look", ["aGVsbG8="])], _=>{}, default);
            Assert.Contains("aGVsbG8=", handler.ChatBody);
        } else {
            await Assert.ThrowsAsync<IOException>(()=>client.SendAsync("test", [new("user", "look", ["aGVsbG8="])], _=>{}, default));
            Assert.Null(handler.ChatBody);
        }
    }
    private sealed class ImageHandler(bool vision) : HttpMessageHandler
    {
        public string? ChatBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/api/show") return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(vision ? "{\"capabilities\":[\"vision\"]}" : "{\"capabilities\":[\"completion\"]}") };
            ChatBody = await request.Content!.ReadAsStringAsync(ct);
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"message\":{\"content\":\"seen\"},\"done\":true,\"done_reason\":\"stop\"}\n") };
        }
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
