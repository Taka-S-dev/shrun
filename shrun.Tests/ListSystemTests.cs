using Shrun;

namespace shrun.Tests;

public class ListSystemTests
{
    private static Command Cmd(string cmd, string? dir = null, Dictionary<string, string>? vars = null) =>
        new("test", null, dir, cmd, null, vars);

    [Fact]
    public void HasPlaceholders_ReturnsTrueWhenCmdHasSlot()
    {
        Assert.True(ListSystem.HasPlaceholders(Cmd("echo {project}")));
    }

    [Fact]
    public void HasPlaceholders_ReturnsTrueWhenDirHasSlot()
    {
        Assert.True(ListSystem.HasPlaceholders(Cmd("echo done", dir: "{project}")));
    }

    [Fact]
    public void HasPlaceholders_ReturnsFalseWhenNoSlots()
    {
        Assert.False(ListSystem.HasPlaceholders(Cmd("echo done", dir: "C:\\app")));
    }

    [Fact]
    public void ExtractAllSlotNames_ReturnsUniqueNames()
    {
        var cmd = Cmd("echo {project} {project}", dir: "{env}");
        var names = ListSystem.ExtractAllSlotNames(cmd);
        Assert.Equal(2, names.Count);
        Assert.Contains("project", names);
        Assert.Contains("env", names);
    }

    [Fact]
    public void ExtractAllSlotNames_ReturnsEmptyWhenNoSlots()
    {
        var names = ListSystem.ExtractAllSlotNames(Cmd("echo done"));
        Assert.Empty(names);
    }

    [Fact]
    public void ApplyVarValues_ReplacesAllOccurrences()
    {
        var cmd = Cmd("echo {env} and {env}", dir: "{env}");
        var result = ListSystem.ApplyVarValues(cmd, new Dictionary<string, string> { ["env"] = "prod" });
        Assert.Equal("echo prod and prod", result.Cmd);
        Assert.Equal("prod", result.Dir);
    }

    [Fact]
    public void ApplyVarValues_IgnoresUnknownVars()
    {
        var cmd = Cmd("echo {env}");
        var result = ListSystem.ApplyVarValues(cmd, new Dictionary<string, string> { ["other"] = "x" });
        Assert.Equal("echo {env}", result.Cmd);
    }

    [Fact]
    public void ApplyVarValues_ReturnsOriginalWhenEmptyVars()
    {
        var cmd = Cmd("echo {env}");
        var result = ListSystem.ApplyVarValues(cmd, new Dictionary<string, string>());
        Assert.Equal("echo {env}", result.Cmd);
    }
}
