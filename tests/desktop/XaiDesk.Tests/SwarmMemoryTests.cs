using Sovereign.Desktop.Client;
using Sovereign.Desktop.Connector;
using Xunit;

namespace XaiDesk.Tests;

public sealed class SwarmMemoryTests
{
    [Fact]
    public void Investigator_uses_the_team_investigator_instructions_and_builder_maps_to_engineer()
    {
        var investigator = SwarmRoles.For(SwarmRole.Investigator);
        Assert.Contains(TeamRoles.Investigator.Instructions, investigator);
        Assert.Contains("Do not implement the change", investigator);
        var manager = SwarmRoles.Manager(Path.Combine(Path.GetTempPath(), "board"), "Project memory\n- Finding (unverified): old note");
        Assert.Contains("Builder means engineer", manager);
        Assert.Contains("Reviewer means verifier", manager);
        Assert.Contains("Finding (unverified): old note", manager);
        Assert.Contains("This role covers builder work", SwarmRoles.For(SwarmRole.Engineer));
        Assert.Contains("This role covers reviewer work", SwarmRoles.For(SwarmRole.Verifier));
    }

    [Fact]
    public void Team_notes_migrate_and_findings_stay_unverified_until_a_verifier_passes()
    {
        var root = Temp();
        var folder = Path.Combine(root, "project");
        Directory.CreateDirectory(folder);
        var team = Path.Combine(root, "team");
        Directory.CreateDirectory(team);
        File.WriteAllText(Path.Combine(team, "team.json"), """
            {"Version":1,"Projects":[{"Folder":"PLACEHOLDER","Assignments":[],"Handoff":{"Notes":[
              {"Id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","Kind":"decision","Text":"Keep the parser.","Source":"team"},
              {"Id":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","Kind":"finding","Text":"The line is in title.txt.","Source":"team"},
              {"Id":"cccccccccccccccccccccccccccccccc","Kind":"verified","Text":"Old check.","Source":"team"}
            ]},"Limitations":[]}]}
            """.Replace("PLACEHOLDER", folder.Replace("\\", "\\\\")));

        var loaded = SwarmMemory.Load(root, folder);
        Assert.Contains(loaded, note => note.Kind == SwarmMemory.Decision && note.Text == "Keep the parser.");
        Assert.Contains(loaded, note => note.Kind == SwarmMemory.Finding && note.Text == "The line is in title.txt.");
        Assert.Contains(loaded, note => note.Kind == SwarmMemory.Verified && note.Text == "Old check.");
        Assert.Equal("Finding (unverified)", SwarmMemory.Label(SwarmMemory.Finding));
        Assert.Equal(0, SwarmMemory.ImportTeam(root, folder));

        var weak = SwarmMemory.ReadHandoff("SOVEREIGN_HANDOFF\nverified: looks fine\nfinding: saw a gap\nEND_SOVEREIGN_HANDOFF", "reply", verifierPassed: false);
        Assert.Contains(weak, note => note.Kind == SwarmMemory.Finding && note.Text == "looks fine");
        Assert.Contains(weak, note => note.Kind == SwarmMemory.Finding && note.Text == "saw a gap");
        Assert.DoesNotContain(weak, note => note.Kind == SwarmMemory.Verified);

        var board = new SwarmBoard
        {
            Id = "run1",
            Goal = "trace it",
            Tasks =
            [
                new SwarmTask { Id = "t-find", Title = "Trace title", Role = SwarmRole.Investigator, Status = SwarmStatus.Done, Result = "title.txt line 1" },
                new SwarmTask
                {
                    Id = "t-check", Title = "Check title", Role = SwarmRole.Verifier, Status = SwarmStatus.Done,
                    VerifierNote = "Claim: title.txt line 1\nHow tested: read the file\nEvidence: TITLE=blue\nPaths covered: title.txt\nVerdict: pass because the line matches"
                }
            ]
        };
        Assert.True(SwarmMemory.VerifierPassed(board));
        Assert.True(SwarmMemory.RememberRun(root, folder, board, "SOVEREIGN_HANDOFF\nverified: title matches\nEND_SOVEREIGN_HANDOFF"));
        var saved = SwarmMemory.Load(root, folder);
        Assert.Contains(saved, note => note.Kind == SwarmMemory.Finding && note.Text.Contains("title.txt line 1"));
        Assert.Contains(saved, note => note.Kind == SwarmMemory.Verified && note.Text.Contains("title.txt line 1"));
        Assert.Contains(saved, note => note.Kind == SwarmMemory.Verified && note.Text == "title matches");
        Assert.False(SwarmMemory.RememberRun(root, folder, board, "SOVEREIGN_HANDOFF\nverified: title matches\nEND_SOVEREIGN_HANDOFF"));
    }

    private static string Temp()
    {
        var path = Path.Combine(Path.GetTempPath(), "xai-desk-swarm-memory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
