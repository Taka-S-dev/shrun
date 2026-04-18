using Shrun;

namespace shrun.Tests;

public class ConfigStoreTests
{
    private static string WriteTsv(string content)
    {
        var path = Path.GetTempFileName() + ".tsv";
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ParseTsv_ParsesBasicCommand()
    {
        var path = WriteTsv("name\tgroup\tdir\tcmd\tshell\n" +
                            "build\tmake\tC:\\app\techo building\t\n");
        try
        {
            var config = ConfigStore.ParseTsv(path);
            Assert.NotNull(config);
            Assert.Single(config.Commands);
            var cmd = config.Commands[0];
            Assert.Equal("build", cmd.Name);
            Assert.Equal("make", cmd.Group);
            Assert.Equal("C:\\app", cmd.Dir);
            Assert.Equal("echo building", cmd.Cmd);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ParseTsv_ParsesVarsColumn()
    {
        var path = WriteTsv("name\tgroup\tdir\tcmd\tshell\tvars\n" +
                            "build\tmake\t{projDir}\techo {projCmd}\t\tprojDir=project,projCmd=project\n");
        try
        {
            var config = ConfigStore.ParseTsv(path);
            Assert.NotNull(config);
            var cmd = config.Commands[0];
            Assert.NotNull(cmd.Vars);
            Assert.Equal("project", cmd.Vars["projDir"]);
            Assert.Equal("project", cmd.Vars["projCmd"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ParseTsv_SkipsEmptyLines()
    {
        var path = WriteTsv("name\tgroup\tdir\tcmd\tshell\n" +
                            "build\tmake\t\techo building\t\n" +
                            "\n" +
                            "test\tmake\t\techo testing\t\n");
        try
        {
            var config = ConfigStore.ParseTsv(path);
            Assert.NotNull(config);
            Assert.Equal(2, config.Commands.Count);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ParseTsv_ReturnsNullForMissingRequiredHeaders()
    {
        var path = WriteTsv("name\tgroup\n" +
                            "build\tmake\n");
        try
        {
            var config = ConfigStore.ParseTsv(path);
            Assert.Null(config);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ParseTsv_ReturnsEmptyCommandsForHeaderOnlyFile()
    {
        var path = WriteTsv("name\tgroup\tdir\tcmd\tshell\n");
        try
        {
            var config = ConfigStore.ParseTsv(path);
            Assert.NotNull(config);
            Assert.Empty(config.Commands);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ParseTsv_NullVarsWhenColumnEmpty()
    {
        var path = WriteTsv("name\tgroup\tdir\tcmd\tshell\tvars\n" +
                            "build\tmake\t\techo building\t\t\n");
        try
        {
            var config = ConfigStore.ParseTsv(path);
            Assert.NotNull(config);
            Assert.Null(config.Commands[0].Vars);
        }
        finally { File.Delete(path); }
    }
}
