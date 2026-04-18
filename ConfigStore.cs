using System.Text.Json;
using static Shrun.Tui;
using static Shrun.Selectors;
using static Shrun.VarSystem;

namespace Shrun;

static class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static (Config? config, List<Workflow> workflows, string? lastWorkflow,
                   string workflowsPath, string lastPath,
                   List<Alias> aliases, string aliasesPath,
                   Dictionary<string, List<VarEntry>> vars, string varsDir)
        LoadConfig(string projectDir)
    {
        var jsonPath      = Path.Combine(projectDir, "config.json");
        var tsvPath       = Path.Combine(projectDir, "config.tsv");
        var workflowsPath = Path.Combine(projectDir, "workflows.json");
        var lastPath      = Path.Combine(projectDir, ".last");
        var aliasesPath   = Path.Combine(projectDir, "aliases.json");
        var varsDir       = Path.Combine(projectDir, "vars");

        Config? config = null;
        if (File.Exists(jsonPath))
            config = JsonSerializer.Deserialize<Config>(File.ReadAllText(jsonPath));
        else if (File.Exists(tsvPath))
            config = ParseTsv(tsvPath);

        if (config == null || config.Commands.Count == 0)
        {
            Console.WriteLine("Failed to load config. Add config.json or config.tsv.");
            Pause();
            return (null, new(), null, "", "", new(), "", new(), "");
        }

        var workflows = File.Exists(workflowsPath)
            ? JsonSerializer.Deserialize<List<Workflow>>(File.ReadAllText(workflowsPath)) ?? new()
            : new List<Workflow>();

        var aliases = File.Exists(aliasesPath)
            ? JsonSerializer.Deserialize<List<Alias>>(File.ReadAllText(aliasesPath)) ?? new()
            : new List<Alias>();

        var vars = LoadVarsFromDir(varsDir);

        var lastWorkflow = File.Exists(lastPath) ? File.ReadAllText(lastPath).Trim() : null;

        // Startup check: warn if any workflow references a command not found in config
        var commandNames = config.Commands.Select(c => c.Name).ToHashSet();
        var warnings = new List<string>();
        foreach (var workflow in workflows)
        {
            var missing = workflow.Commands.Where(c => !commandNames.Contains(c)).ToList();
            if (missing.Count > 0)
                warnings.Add($"  \"{workflow.Name}\": {string.Join(", ", missing)}");
        }
        if (warnings.Count > 0)
        {
            Header("Warning");
            Console.WriteLine("  Some workflows reference commands not found in config:\n");
            foreach (var w in warnings) Console.WriteLine(w);
            Console.WriteLine("\n  Check config or edit the affected workflows.");
            Pause();
        }

        return (config, workflows, lastWorkflow, workflowsPath, lastPath, aliases, aliasesPath, vars, varsDir);
    }

    public static string? SelectProjectDir(List<string> dirs)
    {
        var names = dirs.Select(Path.GetFileName).ToList();
        var selected = SingleSelectStr("Select project", names!);
        if (selected == null) return null;
        return dirs[names.IndexOf(selected)];
    }

    public static Config? ParseTsv(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length < 2) return null;

        var headers = lines[0].Split('\t').Select(h => h.Trim().ToLower()).ToList();
        int Col(string name) => headers.IndexOf(name);

        var iName  = Col("name");
        var iGroup = Col("group");
        var iDir   = Col("dir");
        var iCmd   = Col("cmd");
        var iShell = Col("shell");

        if (iName < 0 || iCmd < 0) return null;

        var commands = new List<Command>();
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cols = line.Split('\t');
            string Get(int i) => i >= 0 && i < cols.Length ? cols[i].Trim() : "";
            commands.Add(new Command(
                Name:  Get(iName),
                Group: Get(iGroup),
                Dir:   Get(iDir),
                Cmd:   Get(iCmd),
                Shell: string.IsNullOrEmpty(Get(iShell)) ? null : Get(iShell)
            ));
        }

        return new Config(commands);
    }

    public static void SaveWorkflows(string path, List<Workflow> workflows)
    {
        try { File.WriteAllText(path, JsonSerializer.Serialize(workflows, JsonOpts)); }
        catch (Exception ex) { Console.WriteLine($"\n  Failed to save workflows: {ex.Message}"); Pause(); }
    }

    public static void SaveAliases(string path, List<Alias> aliases)
    {
        try { File.WriteAllText(path, JsonSerializer.Serialize(aliases, JsonOpts)); }
        catch (Exception ex) { Console.WriteLine($"\n  Failed to save aliases: {ex.Message}"); Pause(); }
    }
}
