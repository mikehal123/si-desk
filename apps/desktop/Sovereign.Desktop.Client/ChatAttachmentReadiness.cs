namespace Sovereign.Desktop.Client;

public static class ChatAttachmentReadiness
{
    public static string Describe(IEnumerable<string> kinds, bool connected, string? model,
        bool textOnlyConnection, bool cursor, bool cursorImages, bool ollama)
    {
        var files = kinds.ToArray();
        if (files.Length == 0) return "";
        if (files.Any(kind => kind is not ("text" or "image")))
            return "Saved locally · some files are preview only. Remove unsupported files before sending.";
        if (files.Contains("image") && textOnlyConnection)
            return "Saved locally · this GLM/Kimi connection cannot send images. Choose a supported connection or remove the image.";
        if (!connected || string.IsNullOrWhiteSpace(model))
            return "Saved locally · connect and choose a model before sending.";
        if (!files.Contains("image")) return "Ready to send extracted document text. Click a file to inspect it.";
        if (cursor && !cursorImages)
            return "Saved locally · this Cursor connection does not advertise image support.";
        if (ollama) return "Images attached · the selected Ollama model's vision support will be checked when you send.";
        return "Ready to send images with this connection. Images are not converted to text.";
    }
}
