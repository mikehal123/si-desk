using Sovereign.Desktop.Connector;
using Sovereign.Desktop.Core;
using Xunit;

namespace XaiDesk.Tests;

public sealed class WorkDoctrineTests
{
    [Fact]
    public void Standing_skill_is_indexed_and_loaded_by_every_swarm_role()
    {
        var path = WorkDoctrine.FindFile();
        Assert.NotNull(path);
        Assert.EndsWith(Path.Combine(".cursor", "skills", "work-doctrine", "SKILL.md"), path, StringComparison.OrdinalIgnoreCase);
        var skill = WorkDoctrine.Text();
        Assert.StartsWith("# Work Doctrine", skill, StringComparison.Ordinal);
        Assert.Contains("REVERSIBLE", skill, StringComparison.Ordinal);
        Assert.Contains("nothing is done without proof per the verify-result skill", skill, StringComparison.Ordinal);
        var index = File.ReadAllText(Path.Combine(Directory.GetParent(path)!.Parent!.FullName, "index.json"));
        foreach (var role in new[] { "main", "subagent", "engineer", "researcher", "verifier", "grokbot-research", "grokbot-ops", "manager" })
            Assert.Contains("\"" + role + "\"", index, StringComparison.Ordinal);

        var prefix = WorkDoctrine.PromptPrefix();
        Assert.Contains("main agent loads it by default", prefix, StringComparison.Ordinal);
        Assert.Contains("put the Work Doctrine text below at the top of that helper's task", prefix, StringComparison.Ordinal);

        foreach (var role in new[] { SwarmRole.Engineer, SwarmRole.Researcher, SwarmRole.Verifier, SwarmRole.GrokbotResearch, SwarmRole.GrokbotOps, "worker" })
        {
            var prompt = SwarmRoles.For(role);
            Assert.Contains("# Work Doctrine", prompt, StringComparison.Ordinal);
            Assert.Contains("put the Work Doctrine text below at the top of that helper's task", prompt, StringComparison.Ordinal);
        }

        var manager = SwarmRoles.Manager(Path.Combine(Path.GetTempPath(), "board"));
        Assert.Contains("# Work Doctrine", manager, StringComparison.Ordinal);
        Assert.Contains("# Verify Result", manager, StringComparison.Ordinal);
        var worker = SwarmRoles.Worker(new SwarmTask { Id = "t-1", Title = "Check the file", Role = SwarmRole.Engineer }, Path.Combine(Path.GetTempPath(), "board.json"));
        Assert.Contains("# Work Doctrine", worker, StringComparison.Ordinal);
        Assert.Contains("swarm engineer", worker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_skill_folder_uses_the_built_in_copy_and_warns()
    {
        var empty = Path.Combine(Path.GetTempPath(), "xai-desk-no-skills-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            var doctrine = WorkDoctrine.Load(empty, false);
            Assert.Contains("# Work Doctrine", doctrine.Text, StringComparison.Ordinal);
            Assert.Contains("not found", doctrine.Warning, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("built-in copy", doctrine.Warning, StringComparison.OrdinalIgnoreCase);

            var verify = VerifyResultSkill.Load(empty, false);
            Assert.Contains("name: verify-result", verify.Text, StringComparison.Ordinal);
            Assert.Contains("not found", verify.Warning, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("built-in copy", verify.Warning, StringComparison.OrdinalIgnoreCase);

            var prompt = "Warning: " + doctrine.Warning + "\n\n" + doctrine.Text;
            Assert.Contains("# Work Doctrine", prompt, StringComparison.Ordinal);
            Assert.Contains("Warning:", prompt, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(empty)) Directory.Delete(empty, true);
        }
    }
}
