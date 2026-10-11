using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;
public sealed class ChatDocumentReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Sovereign-document-qa-" + Guid.NewGuid().ToString("N"));
    private readonly string _id = Guid.NewGuid().ToString("N");
    private ChatAttachmentStore Store => new(_root, _id);
    private Task<ChatAttachment> Text(string name, string value) => Store.ImportAsync(new MemoryStream(Encoding.UTF8.GetBytes(value)), name, default);
    private async Task<ChatAttachment> Zip(string name, params (string Name, string Value)[] entries)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
            foreach (var entry in entries) { using var w = new StreamWriter(zip.CreateEntry(entry.Name).Open()); w.Write(entry.Value); }
        bytes.Position = 0; return await Store.ImportAsync(bytes, name, default);
    }
    [Theory] [InlineData("plan.md")] [InlineData("plan.MD")] [InlineData("plan.markdown")] [InlineData("notes.txt")] [InlineData("package.zip.sha256")]
    public async Task Text_reaches_model_context(string name)
    {
        var item = await Text(name, "Document marker ORBIT-58291");
        Assert.Equal("text", item.Kind);
        var prompt = ChatAttachmentStore.WithContext("Read this", [item]);
        Assert.Contains("ORBIT-58291", prompt); Assert.Contains(name, prompt);
        if (name.EndsWith("sha256")) Assert.Contains("not the ZIP contents", item.Note);
    }
    [Fact] public async Task Zip_reads_named_sources_and_does_not_execute_or_extract_paths()
    {
        var item = await Zip("brief.zip", ("docs/plan.md", "ZIP-TOKEN-321"), ("readme.txt", "SECOND-TOKEN-456"), ("../escape.md", "DONT-SEND"), ("run.exe", "BINARY-DONT-SEND"));
        Assert.Equal("text", item.Kind); Assert.Contains("ZIP-TOKEN-321", item.ExtractedText); Assert.Contains("docs/plan.md", item.ExtractedText);
        Assert.Contains("2 entries omitted", item.Note); Assert.DoesNotContain("DONT-SEND", item.ExtractedText);
        Assert.False(File.Exists(Path.Combine(_root,"escape.md")));
    }
    [Fact] public async Task Archive_limits_are_explicit()
    {
        var item = await Zip("huge.zip", ("huge.md", new string('z', 21 * 1024 * 1024)));
        Assert.Equal("file", item.Kind); Assert.Null(item.ExtractedText);
    }
    [Fact] public async Task Binary_masquerading_as_text_is_not_sent()
    { var item = await Text("bad.md", "MZ\0\u0001garbage"); Assert.Equal("file",item.Kind); Assert.Null(item.ExtractedText); }
    [Fact] public async Task Previously_blocked_draft_is_upgraded_from_verified_copy()
    {
        var item = await Text("package.zip.sha256", "abc CHECKSUM-123");
        var old = item with { Kind="file", ExtractedText=null, Note="unsupported" };
        var current = await Store.RefreshAsync(old, default);
        Assert.Equal("text",current.Kind); Assert.Contains("CHECKSUM-123",current.ExtractedText);
        File.WriteAllText(Store.PathFor(item), "tampered");
        await Assert.ThrowsAsync<IOException>(() => Store.RefreshAsync(old,default));
    }
    [Fact] public async Task Xlsx_shared_cells_and_formula_provenance_are_read()
    {
        var item = await Zip("sheet.xlsx", ("xl/sharedStrings.xml", "<sst xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><si><t>WORKBOOK-712</t></si></sst>"),
            ("xl/worksheets/sheet1.xml", "<worksheet xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><sheetData><row><c r='A1' t='s'><v>0</v></c><c r='B1'><f>1+2</f><v>3</v></c></row></sheetData></worksheet>"));
        Assert.Equal("text",item.Kind); Assert.Contains("WORKBOOK-712",item.ExtractedText); Assert.Contains("cached value, not recalculated",item.ExtractedText);
    }
    [Fact] public async Task Pptx_slide_text_is_read()
    {
        var item = await Zip("slides.pptx", ("ppt/slides/slide1.xml", "<a:p xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main'><a:r><a:t>SLIDE-813</a:t></a:r></a:p>"));
        Assert.Equal("text",item.Kind); Assert.Contains("SLIDE-813",item.ExtractedText);
    }
    [Fact] public async Task Legacy_doc_explains_conversion()
    { var item=await Text("legacy.doc","not a docx"); Assert.Equal("file",item.Kind); Assert.Contains("Save it as .docx",item.Note); }
    [Fact] public async Task Cursor_prompt_contains_document_bytes_not_just_name()
    {
        var item=await Text("plan.md","ONLY-IN-DOCUMENT-91273");
        string Reply(int id, object result) => JsonSerializer.Serialize(new {id,result});
        var lines=new[]{Reply(1,new{protocolVersion=1}),Reply(2,new{sessionId="s",models=new{availableModels=new[]{new{modelId="test",name="test"}}}}),Reply(3,new{}),Reply(4,new{}),JsonSerializer.Serialize(new { method="session/update", @params=new { sessionId="s", update=new { sessionUpdate="agent_message_chunk", content=new { type="text", text="read" } } } }),Reply(5,new{stopReason="end_turn"})};
        var output=new StringWriter();var client=new CursorAcpSession(new StringReader(string.Join('\n',lines)+"\n"),output);
        await client.ConnectAsync(_root,default);
        await client.SendAsync("test",ChatAttachmentStore.WithContext("What marker is in the file?",[item]),_=>{},default);
        using var prompt=JsonDocument.Parse(output.ToString().Split('\n',StringSplitOptions.RemoveEmptyEntries).Last());
        Assert.Contains("ONLY-IN-DOCUMENT-91273",prompt.RootElement.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
    }
    public void Dispose(){if(Directory.Exists(_root))Directory.Delete(_root,true);}
}
