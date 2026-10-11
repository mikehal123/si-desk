using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;

public class ChatAttachmentReadinessTests
{
    [Fact] public void TextOnlyProviderDoesNotAdvertiseImageDelivery() => Assert.Contains("cannot send images", ChatAttachmentReadiness.Describe(["image"], true, "glm", true, false, false, false));
    [Fact] public void MissingModelRequiresSelection() => Assert.Contains("choose a model", ChatAttachmentReadiness.Describe(["image"], true, "", false, false, false, false));
    [Fact] public void CursorRequiresAdvertisedImageCapability() => Assert.Contains("does not advertise", ChatAttachmentReadiness.Describe(["image"], true, "grok", false, true, false, false));
    [Fact] public void LocalVisionIsNotAssumed() => Assert.Contains("will be checked", ChatAttachmentReadiness.Describe(["image"], true, "local", false, false, false, true));
    [Fact] public void DocumentsAreDescribedAsExtractedText() => Assert.Contains("document text", ChatAttachmentReadiness.Describe(["text"], true, "glm", true, false, false, false));
    [Fact] public void PreviewFilesOverrideOtherwiseSupportedImages() => Assert.Contains("preview only", ChatAttachmentReadiness.Describe(["image", "video"], true, "grok", false, true, true, false));
    [Fact] public void DisconnectedImagesAreOnlyLocal() => Assert.Contains("Saved locally", ChatAttachmentReadiness.Describe(["image"], false, "grok", false, true, true, false));
    [Fact] public void EmptyDraftHasNoBanner() => Assert.Equal("", ChatAttachmentReadiness.Describe([], false, null, false, false, false, false));
}
