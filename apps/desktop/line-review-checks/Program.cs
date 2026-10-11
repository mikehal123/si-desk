using Sovereign.Desktop.Client;

var failed = 0;
void Check(bool condition, string name)
{
    if (condition) { Console.WriteLine("pass  " + name); return; }
    failed++;
    Console.WriteLine("FAIL  " + name);
}

var plan = new CursorPlanItem("1", "Write the host", "pending");
var change = new CursorFileChange("a.cs", "alpha\nbeta\n", "alpha\ngamma\n");
var documents = LineReview.Documents([plan], [change, new CursorFileChange("b.cs", "one\n", "one\n")]);
var draft = new LineReviewDraft("main", 0, documents);
var captured = draft.Capture("plan", 1, "comment", "keep the first step", false);
Check(captured.Target == "plan" && captured.File is null && captured.Line == 1
    && captured.Action == "comment" && captured.Note == "keep the first step" && draft.Notes.Count == 1,
    "comment capture records target, line, note, and action");

var file = documents.Single(item => item.Title == "a.cs");
Check(file.Lines.Any(line => line.Kind == "removed" && line.Text == "beta")
    && file.Lines.Any(line => line.Kind == "added" && line.Text == "gamma"),
    "before/after diff shows removed and added lines");
var hunkLine = file.Lines.First(line => line.Kind == "removed");
draft.Capture(file.Id, hunkLine.Number, "drop this", "remove the old line", wholeHunk: true);
Check(draft.Notes.Count(note => note.File == "a.cs" && note.Action == "drop this" && note.Hunk == hunkLine.Hunk) == file.Lines.Count(line => line.Hunk == hunkLine.Hunk),
    "drop this hunk marks every line in the hunk");

draft.Decide("a.cs", "approve");
draft.Decide("a.cs", "reject");
draft.Decide("b.cs", "approve");
var message = draft.Format();
const string expected = """
Line review
Revise only what is flagged. Leave every other line unchanged.

target: plan
line: 1
action: comment
note: keep the first step

target: file
file: a.cs
line: 2
hunk: h1
action: drop this
note: remove the old line

target: file
file: a.cs
line: 3
hunk: h1
action: drop this
note: remove the old line

target: file
file: a.cs
decision: reject

target: file
file: b.cs
decision: approve
""";
Check(message == expected.Replace("\r\n", "\n", StringComparison.Ordinal), "structured message lists file, line, note, action, and one decision per file");
Check(Count(message, "decision: reject") == 1 && Count(message, "decision: approve") == 1, "reject replaces approve on the same file");
Check(LineReview.Message(draft, approveAll: true) == LineReview.ApproveAllMessage, "approve all sends the short approval");

var patch = LineReview.File(new CursorFileChange("native.cs", null, "", "@@ -1,2 +1,2 @@\n keep\n-old\n+new\n"), "native.cs");
Check(patch.Lines.Any(line => line.Kind == "removed" && line.Text == "old" && line.Hunk == "h1")
    && patch.Lines.Any(line => line.Kind == "added" && line.Text == "new"),
    "unified diff shows removed and added lines");

var root = Path.Combine(Path.GetTempPath(), "sovereign-line-review-" + Guid.NewGuid().ToString("N"));
try
{
    var archive = new ChatArchive(root);
    var sent = draft.ToSaved(message, readOnly: true);
    var editable = new LineReviewDraft("main", 2, documents).ToSaved("Line review\nRevise only what is flagged. Leave every other line unchanged.\n\nNo lines flagged.", false);
    var broken = new SavedLineReview(Guid.NewGuid().ToString("N"), "main", 1,
        [new LineReviewNote("plan", "plan", null, 1, null, "explode", "nope")], [], "", false);
    var chat = LineReview.Remember(LineReview.Remember(LineReview.Remember(ChatArchive.New(4), sent), editable), broken);
    archive.Save(chat);
    var loaded = archive.Load().Single();
    var again = LineReview.Recall(loaded, "main", 0);
    var stillEditing = LineReview.Recall(loaded, "main", 2);
    Check(again is { ReadOnly: true } && again.Notes[0].Note == "keep the first step" && again.SentMessage == message
        && again.Files.Any(fileDecision => fileDecision.File == "a.cs" && fileDecision.Decision == "reject"),
        "sent review reloads read-only with the conversation");
    Check(stillEditing is { ReadOnly: false }, "unsent review reloads still editable");
    Check(LineReview.Recall(loaded, "main", 1) is null && loaded.LineReviews!.Length == 2, "a review with an unknown action is dropped and the conversation still loads");

    Check(LineReviewPreference.Load(root) && LineReview.ShowButton(true, true, false), "line review defaults on");
    LineReviewPreference.Save(root, false);
    var hidden = LineReviewPreference.Load(root);
    Check(!hidden && !LineReview.ShowButton(hidden, true, true), "setting off hides the review button");
}
finally { if (Directory.Exists(root)) Directory.Delete(root, true); }

return failed;

static int Count(string text, string value)
{
    var count = 0;
    var index = 0;
    while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0) { count++; index += value.Length; }
    return count;
}
