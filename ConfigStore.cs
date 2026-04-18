using System.Text.Json;
using static Shrun.Tui;
using static Shrun.Selectors;
using static Shrun.ListSystem;

namespace Shrun;

static class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static (Config? config, List<Workflow> workflows, string? lastWorkflow,
                   string workflowsPath, string lastPath,
                   List<Alias> aliases, string aliasesPath,
                   Dictionary<string, List<ListEntry>> lists, string listsDir)
        LoadConfig(string projectDir)
    {
        var jsonPath      = Path.Combine(projectDir, "config.json");
        var tsvPath       = Path.Combine(projectDir, "config.tsv");
        var workflowsPath = Path.Combine(projectDir, "workflows.json");
        var lastPath      = Path.Combine(projectDir, ".last");
        var aliasesPath   = Path.Combine(projectDir, "aliases.json");
        var listsDir      = Path.Combine(projectDir, "lists");

        // Migrate vars/ → lists/ if needed
        var oldVarsDir = Path.Combine(projectDir, "vars");
        if (Directory.Exists(oldVarsDir) && !Directory.Exists(listsDir))
        {
            try { Directory.Move(oldVarsDir, listsDir); } catch { }
        }

        Config? config = null;
        try
        {
            if (File.Exists(jsonPath))
                config = JsonSerializer.Deserialize<Config>(File.ReadAllText(jsonPath));
            else if (File.Exists(tsvPath))
                config = ParseTsv(tsvPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to parse config: {ex.Message}");
            Console.WriteLine($"  File: {(File.Exists(jsonPath) ? jsonPath : tsvPath)}");
            Pause();
            return (null, [], null, "", "", [], "", [], "");
        }

        if (config == null || config.Commands.Count == 0)
        {
            Console.WriteLine("Failed to load config. Add config.json or config.tsv.");
            Pause();
            return (null, [], null, "", "", [], "", [], "");
        }

        List<Workflow> workflows = [];
        try { workflows = File.Exists(workflowsPath)
            ? JsonSerializer.Deserialize<List<Workflow>>(File.ReadAllText(workflowsPath)) ?? []
            : []; }
        catch { Console.WriteLine($"  Warning: Could not parse workflows.json — starting fresh."); }

        List<Alias> aliases = [];
        try { aliases = File.Exists(aliasesPath)
            ? JsonSerializer.Deserialize<List<Alias>>(File.ReadAllText(aliasesPath)) ?? []
            : []; }
        catch { Console.WriteLine($"  Warning: Could not parse aliases.json — starting fresh."); }

        var lists = LoadListsFromDir(listsDir);

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

        return (config, workflows, lastWorkflow, workflowsPath, lastPath, aliases, aliasesPath, lists, listsDir);
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
        string[] lines;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var sr = new StreamReader(fs))
            lines = sr.ReadToEnd().Split(["\r\n", "\n"], StringSplitOptions.None);

        if (lines.Length < 2) return null;

        var headers = lines[0].Split('\t').Select(h => h.Trim().ToLower()).ToList();
        int Col(string name) => headers.IndexOf(name);

        var iName  = Col("name");
        var iGroup = Col("group");
        var iDir   = Col("dir");
        var iCmd   = Col("cmd");
        var iShell = Col("shell");
        var iVars  = Col("vars");

        if (iName < 0 || iCmd < 0) return null;

        var commands = new List<Command>();
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cols = line.Split('\t');
            string Get(int i) => i >= 0 && i < cols.Length ? cols[i].Trim() : "";

            var varsStr = Get(iVars);
            Dictionary<string, string>? vars = null;
            if (!string.IsNullOrEmpty(varsStr))
            {
                vars = varsStr.Split(',')
                    .Select(s => s.Split('=', 2))
                    .Where(p => p.Length == 2 && !string.IsNullOrEmpty(p[0].Trim()))
                    .ToDictionary(p => p[0].Trim(), p => p[1].Trim());
                if (vars.Count == 0) vars = null;
            }

            commands.Add(new Command(
                Name:  Get(iName),
                Group: Get(iGroup),
                Dir:   Get(iDir),
                Cmd:   Get(iCmd),
                Shell: string.IsNullOrEmpty(Get(iShell)) ? null : Get(iShell),
                Vars:  vars
            ));
        }

        return new Config(commands);
    }

    public static void SaveWorkflows(string path, List<Workflow> workflows) =>
        AtomicWrite(path, JsonSerializer.Serialize(workflows, JsonOpts), "workflows");

    public static void SaveAliases(string path, List<Alias> aliases) =>
        AtomicWrite(path, JsonSerializer.Serialize(aliases, JsonOpts), "aliases");

    private static void AtomicWrite(string path, string content, string label)
    {
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, content);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n  Failed to save {label}: {ex.Message}");
            try { File.Delete(tmp); } catch { }
            Pause();
        }
    }
}
