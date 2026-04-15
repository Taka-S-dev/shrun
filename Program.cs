using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding  = System.Text.Encoding.UTF8;
C.BoxCharCols = DetectBoxCharCols();

var baseDir     = AppContext.BaseDirectory;
var projectsDir = Path.Combine(baseDir, "projects");

if (!Directory.Exists(projectsDir))
{
    Console.WriteLine("projects/ folder not found");
    Pause(); return;
}

var projectDirs = Directory.GetDirectories(projectsDir)
    .Where(d => File.Exists(Path.Combine(d, "config.json")) || File.Exists(Path.Combine(d, "config.tsv")))
    .OrderBy(d => d).ToList();

if (projectDirs.Count == 0)
{
    Console.WriteLine("No projects found. Create a subfolder in projects/ with config.json.");
    Pause(); return;
}

var projectDir = projectDirs.Count == 1
    ? projectDirs[0]
    : SelectProjectDir(projectDirs);

if (projectDir == null) return;

var (config, workflows, lastWorkflow, workflowsPath, lastPath, aliases, aliasesPath, vars, varsDir) = LoadConfig(projectDir);
if (config == null) return;

var modes = new List<string> { "Run workflow", "Run manually", "Create workflow", "Edit workflow", "Delete workflow", "Manage aliases", "Manage vars", "Switch config", "Exit" };

try
{

while (true)
{
    // Reload config and vars on each iteration so selection screens always show latest
    try
    {
        var reloadJson = Path.Combine(projectDir, "config.json");
        var reloadTsv  = Path.Combine(projectDir, "config.tsv");
        var reloaded = File.Exists(reloadJson)
            ? JsonSerializer.Deserialize<Config>(File.ReadAllText(reloadJson))
            : File.Exists(reloadTsv) ? ParseTsv(reloadTsv) : null;
        if (reloaded != null && reloaded.Commands.Count > 0) config = reloaded;

        vars = LoadVarsFromDir(varsDir);
    }
    catch (Exception ex)
    {
        Header("Warning");
        Console.WriteLine($"  Failed to reload config: {ex.Message}");
        Console.WriteLine("  Using last loaded config.");
        Pause();
    }

    var mode = SingleSelectStr("Mode", modes, isMain: true, subtitle: Path.GetFileName(projectDir));
    if (mode == null || mode == "Exit") return;

    // --- Switch config ---
    if (mode == "Switch config")
    {
        var newDir = SelectProjectDir(projectDirs);
        if (newDir == null) continue;
        projectDir = newDir;
        (config, workflows, lastWorkflow, workflowsPath, lastPath, aliases, aliasesPath, vars, varsDir) = LoadConfig(projectDir);
        if (config == null) return;
        continue;
    }

    // --- Delete workflow ---
    if (mode == "Delete workflow")
    {
        if (workflows.Count == 0)
        {
            Header("Delete workflow"); Console.WriteLine("  No workflows found."); Pause(); continue;
        }
        var toDelete = MultiSelectGeneric("Delete workflow", workflows, p => p.Name);
        if (toDelete.Count == 0) continue;

        foreach (var p in toDelete) workflows.Remove(p);
        SaveWorkflows(workflowsPath, workflows);
        Header("Delete workflow");
        Console.WriteLine($"  Deleted: {string.Join(", ", toDelete.Select(p => p.Name))}");
        Pause(); continue;
    }

    // --- Edit workflow ---
    if (mode == "Edit workflow")
    {
        if (workflows.Count == 0)
        {
            Header("Edit workflow"); Console.WriteLine("  No workflows found."); Pause(); continue;
        }
        var toEdit = SingleSelect("Edit workflow", workflows, p => p.Name);
        if (toEdit == null) continue;

        var editModes = new List<string> { "Rename", "Change commands" };
        var editMode = SingleSelectStr($"Edit: {toEdit.Name}", editModes);
        if (editMode == null) continue;

        if (editMode == "Rename")
        {
            Header($"Rename: {toEdit.Name}");
            Console.WriteLine("  Esc: cancel\n");
            var newName = ReadInput("  New name > ");
            if (string.IsNullOrEmpty(newName)) continue;
            if (workflows.Any(p => p.Name == newName && p != toEdit))
            {
                Console.Write($"  \"{newName}\" already exists. Overwrite? (y/n) > ");
                if (Console.ReadLine()?.Trim().ToLower() != "y") { Pause(); continue; }
                workflows.RemoveAll(p => p.Name == newName && p != toEdit);
            }
            workflows[workflows.IndexOf(toEdit)] = toEdit with { Name = newName };
        }
        else
        {
            var preSelected = new HashSet<string>(toEdit.Commands);
            var newCommands = MultiSelect("Change commands", config.Commands, preSelected);
            if (newCommands.Count == 0) continue;
            workflows[workflows.IndexOf(toEdit)] = toEdit with { Commands = newCommands.Select(c => c.Name).ToList(), Vars = null };
        }

        SaveWorkflows(workflowsPath, workflows);
        Header("Edit workflow"); Console.WriteLine("  Saved."); Pause(); continue;
    }

    // --- Manage aliases ---
    if (mode == "Manage aliases")
    {
        var aliasModes = new List<string> { "Create alias", "Edit alias", "Delete alias" };
        var aliasMode = SingleSelectStr("Manage aliases", aliasModes);
        if (aliasMode == null) continue;

        if (aliasMode == "Create alias")
        {
            var steps = MultiSelect("Select commands for alias", config.Commands);
            if (steps.Count == 0) continue;

            var aliasVarMap = new Dictionary<string, Dictionary<string, string>>();
            bool aliasCancelled = false;
            var stepNames = steps.Select(c => c.Name).ToList();
            var aliasCmdNotes = new List<string?>(new string?[steps.Count]);
            for (int si = 0; si < steps.Count; si++)
            {
                var cmd = steps[si];
                var varNames = ExtractVarNames(cmd);
                if (varNames.Count == 0) continue;
                var cmdVars = new Dictionary<string, string>();
                foreach (var varName in varNames)
                {
                    var entries = vars.TryGetValue(varName, out var ch) ? ch : new List<VarEntry>();
                    var value = PromptVar(varName, entries, stepNames, si, aliasCmdNotes);
                    if (value == null) { aliasCancelled = true; break; }
                    cmdVars[varName] = value;
                    aliasCmdNotes[si] = string.Join(" ", cmdVars.Select(kv => $"{{{kv.Key}={kv.Value}}}"));
                }
                if (aliasCancelled) break;
                aliasVarMap[cmd.Name] = cmdVars;
            }
            if (aliasCancelled) continue;
            if (aliasVarMap.Count > 0) ReviewWorkflowVars(steps, aliasVarMap, vars);

            Header("Create alias");
            Console.WriteLine("  Esc: cancel\n");
            var name = ReadInput("  Alias name > ");
            if (!string.IsNullOrEmpty(name))
            {
                var existing = aliases.FirstOrDefault(a => a.Name == name);
                if (existing != null)
                {
                    Console.Write($"  \"{name}\" already exists. Overwrite? (y/n) > ");
                    if (Console.ReadLine()?.Trim().ToLower() != "y") { Pause(); continue; }
                    aliases.Remove(existing);
                }
                aliases.Add(new Alias(name, steps.Select(c => c.Name).ToList(),
                    aliasVarMap.Count > 0 ? aliasVarMap : null));
                SaveAliases(aliasesPath, aliases);
                Console.WriteLine("  Saved.");
                Pause();
            }
            continue;
        }

        if (aliasMode == "Edit alias")
        {
            if (aliases.Count == 0)
            {
                Header("Edit alias"); Console.WriteLine("  No aliases found."); Pause(); continue;
            }
            var toEditAlias = SingleSelect("Edit alias", aliases, a => a.Name);
            if (toEditAlias == null) continue;

            var editAliasModes = new List<string> { "Rename", "Change commands" };
            var editAliasMode = SingleSelectStr($"Edit: {toEditAlias.Name}", editAliasModes);
            if (editAliasMode == null) continue;

            if (editAliasMode == "Rename")
            {
                Header($"Rename: {toEditAlias.Name}");
                Console.WriteLine("  Esc: cancel\n");
                var newName = ReadInput("  New name > ");
                if (string.IsNullOrEmpty(newName)) continue;
                if (aliases.Any(a => a.Name == newName && a != toEditAlias))
                {
                    Console.Write($"  \"{newName}\" already exists. Overwrite? (y/n) > ");
                    if (Console.ReadLine()?.Trim().ToLower() != "y") { Pause(); continue; }
                    aliases.RemoveAll(a => a.Name == newName && a != toEditAlias);
                }
                aliases[aliases.IndexOf(toEditAlias)] = toEditAlias with { Name = newName };
            }
            else
            {
                var preSelectedSteps = new HashSet<string>(toEditAlias.Steps);
                var newSteps = MultiSelect("Change commands", config.Commands, preSelectedSteps);
                if (newSteps.Count == 0) continue;
                aliases[aliases.IndexOf(toEditAlias)] = toEditAlias with { Steps = newSteps.Select(c => c.Name).ToList() };
            }

            SaveAliases(aliasesPath, aliases);
            Header("Edit alias"); Console.WriteLine("  Saved."); Pause(); continue;
        }

        if (aliasMode == "Delete alias")
        {
            if (aliases.Count == 0)
            {
                Header("Delete alias"); Console.WriteLine("  No aliases found."); Pause(); continue;
            }
            var toDeleteAliases = MultiSelectGeneric("Delete alias", aliases, a => a.Name);
            if (toDeleteAliases.Count == 0) continue;
            foreach (var a in toDeleteAliases) aliases.Remove(a);
            SaveAliases(aliasesPath, aliases);
            Header("Delete alias");
            Console.WriteLine($"  Deleted: {string.Join(", ", toDeleteAliases.Select(a => a.Name))}");
            Pause();
        }
        continue;
    }

    // --- Manage vars ---
    if (mode == "Manage vars")
    {
        var varModes = new List<string> { "Create var", "Edit var", "Delete var" };
        var varMode = SingleSelectStr("Manage vars", varModes);
        if (varMode == null) continue;

        if (varMode == "Create var")
        {
            Header("Create var");
            Console.WriteLine("  Esc: cancel\n");
            var varName = ReadInput("  Var name > ");
            if (string.IsNullOrEmpty(varName)) continue;

            if (vars.ContainsKey(varName))
            {
                Console.Write($"  \"{varName}\" already exists. Overwrite? (y/n) > ");
                if (Console.ReadLine()?.Trim().ToLower() != "y") { Pause(); continue; }
            }

            var entries = EditVarEntries(varName, new List<VarEntry>());
            if (entries.Count > 0)
            {
                Directory.CreateDirectory(varsDir);
                SaveVar(varsDir, varName, entries);
                vars[varName] = entries;
                Header("Create var"); Console.WriteLine("  Saved."); Pause();
            }
            continue;
        }

        if (varMode == "Edit var")
        {
            if (vars.Count == 0)
            {
                Header("Edit var"); Console.WriteLine("  No vars found."); Pause(); continue;
            }
            var varNames = vars.Keys.ToList();
            var selectedVarName = SingleSelectStr("Edit var", varNames);
            if (selectedVarName == null) continue;

            var editVarModes = new List<string> { "Rename", "Edit values" };
            var editVarMode = SingleSelectStr($"Edit: {selectedVarName}", editVarModes);
            if (editVarMode == null) continue;

            if (editVarMode == "Rename")
            {
                Header($"Rename: {selectedVarName}");
                Console.WriteLine("  Esc: cancel\n");
                var newName = ReadInput("  New name > ");
                if (string.IsNullOrEmpty(newName)) continue;
                if (vars.ContainsKey(newName))
                {
                    Console.Write($"  \"{newName}\" already exists. Overwrite? (y/n) > ");
                    if (Console.ReadLine()?.Trim().ToLower() != "y") { Pause(); continue; }
                    var oldFile = Path.Combine(varsDir, $"{newName}.tsv");
                    File.Delete(oldFile);
                    vars.Remove(newName);
                }
                var entries = vars[selectedVarName];
                var srcFile = Path.Combine(varsDir, $"{selectedVarName}.tsv");
                var dstFile = Path.Combine(varsDir, $"{newName}.tsv");
                try { File.Move(srcFile, dstFile); } catch (FileNotFoundException) { }
                vars.Remove(selectedVarName);
                vars[newName] = entries;
            }
            else
            {
                var newEntries = EditVarEntries(selectedVarName, vars[selectedVarName]);
                SaveVar(varsDir, selectedVarName, newEntries);
                vars[selectedVarName] = newEntries;
            }

            Header("Edit var"); Console.WriteLine("  Saved."); Pause(); continue;
        }

        if (varMode == "Delete var")
        {
            if (vars.Count == 0)
            {
                Header("Delete var"); Console.WriteLine("  No vars found."); Pause(); continue;
            }
            var varNames = vars.Keys.ToList();
            var toDeleteVars = MultiSelectGeneric("Delete var", varNames, v => v);
            if (toDeleteVars.Count == 0) continue;
            foreach (var v in toDeleteVars)
            {
                vars.Remove(v);
                var f = Path.Combine(varsDir, $"{v}.tsv");
                File.Delete(f);
            }
            Header("Delete var");
            Console.WriteLine($"  Deleted: {string.Join(", ", toDeleteVars)}");
            Pause();
        }
        continue;
    }

    // --- Select commands ---
    List<RunItem> selected;

    if (mode == "Run workflow")
    {
        if (workflows.Count == 0)
        {
            Header("Run workflow");
            Console.WriteLine("  No workflows found.");
            Pause(); continue;
        }

        var initialCursor = lastWorkflow != null ? Math.Max(0, workflows.FindIndex(p => p.Name == lastWorkflow)) : 0;
        var workflow = SingleSelect("Run workflow", workflows, p => p.Name, initialCursor);
        if (workflow == null) continue;

        selected = workflow.Commands
            .Select(name => config.Commands.FirstOrDefault(c => c.Name == name))
            .Where(c => c != null)
            .Select(c =>
            {
                var cmdVars = workflow.Vars?.TryGetValue(c!.Name, out var v) == true ? v : null;
                var resolved = cmdVars != null ? ApplyVars(c!, cmdVars) : c!;
                return new RunItem(resolved.Name, resolved, null, null);
            }).ToList();

        lastWorkflow = workflow.Name;
        File.WriteAllText(lastPath, lastWorkflow);
    }
    else if (mode == "Run manually")
    {
        var rawSelected = MultiSelectWithAliases("Select commands", config.Commands, aliases);
        if (rawSelected.Count == 0) continue;

        var varMap = new Dictionary<string, string>();
        bool cancelled = false;
        var itemNames = rawSelected.Select(i => i.Name).ToList();
        for (int ri = 0; ri < rawSelected.Count; ri++)
        {
            if (cancelled) break;
            var item = rawSelected[ri];
            // Aliases with stored vars skip runtime prompting
            var cmdsToCheck = item.IsAlias
                ? (item.Alias!.Vars != null
                    ? Enumerable.Empty<Command>()
                    : item.Alias.Steps.Select(s => config.Commands.FirstOrDefault(c => c.Name == s)).Where(c => c != null).Select(c => c!))
                : new[] { item.Cmd! }.AsEnumerable();

            foreach (var cmd in cmdsToCheck)
            {
                foreach (var varName in ExtractVarNames(cmd))
                {
                    if (varMap.ContainsKey(varName)) continue;
                    var entries = vars.TryGetValue(varName, out var ch) ? ch : new List<VarEntry>();
                    var value = PromptVar(varName, entries, itemNames, ri);
                    if (value == null) { cancelled = true; break; }
                    varMap[varName] = value;
                }
                if (cancelled) break;
            }
        }
        if (cancelled) continue;
        if (varMap.Count > 0) ReviewRunVars(varMap, vars);

        selected = rawSelected.Select(item =>
            item.IsAlias
                ? new RunItem(item.Name, null, item.Alias, varMap.Count > 0 ? varMap : null)
                : new RunItem(item.Name, ApplyVars(item.Cmd!, varMap), null, null)
        ).ToList();
    }
    else // Create workflow
    {
        var cmdSelected = MultiSelect("Select commands", config.Commands);
        if (cmdSelected.Count == 0) continue;

        var workflowVars = new Dictionary<string, Dictionary<string, string>>();
        bool cancelled = false;
        var cmdNames = cmdSelected.Select(c => c.Name).ToList();
        var cmdNotes = new List<string?>(new string?[cmdSelected.Count]);
        for (int ci = 0; ci < cmdSelected.Count; ci++)
        {
            var cmd = cmdSelected[ci];
            var varNames = ExtractVarNames(cmd);
            if (varNames.Count == 0) continue;
            var cmdVars = new Dictionary<string, string>();
            foreach (var varName in varNames)
            {
                var entries = vars.TryGetValue(varName, out var ch) ? ch : new List<VarEntry>();
                var value = PromptVar(varName, entries, cmdNames, ci, cmdNotes);
                if (value == null) { cancelled = true; break; }
                cmdVars[varName] = value;
                cmdNotes[ci] = string.Join(" ", cmdVars.Select(kv => $"{{{kv.Key}={kv.Value}}}"));
            }
            if (cancelled) break;
            workflowVars[cmd.Name] = cmdVars;
        }
        if (cancelled) continue;
        if (workflowVars.Count > 0) ReviewWorkflowVars(cmdSelected, workflowVars, vars);

        Header("Create workflow");
        Console.WriteLine("  Esc: cancel\n");
        var name = ReadInput("  Workflow name > ");
        if (!string.IsNullOrEmpty(name))
        {
            var existing = workflows.FirstOrDefault(p => p.Name == name);
            if (existing != null)
            {
                Console.Write($"  \"{name}\" already exists. Overwrite? (y/n) > ");
                if (Console.ReadLine()?.Trim().ToLower() != "y") { Pause(); continue; }
                workflows.Remove(existing);
            }
            workflows.Add(new Workflow(name, cmdSelected.Select(c => c.Name).ToList(),
                workflowVars.Count > 0 ? workflowVars : null));
            SaveWorkflows(workflowsPath, workflows);
            Console.WriteLine("  Saved.");
            Pause();
        }
        continue;
    }

    // --- Confirm ---
    if (!ConfirmDialog(selected)) continue;

    // --- Reload config and validate ---
    Config? freshConfig;
    {
        var jsonPath = Path.Combine(projectDir, "config.json");
        var tsvPath  = Path.Combine(projectDir, "config.tsv");
        freshConfig = File.Exists(jsonPath)
            ? JsonSerializer.Deserialize<Config>(File.ReadAllText(jsonPath))
            : File.Exists(tsvPath) ? ParseTsv(tsvPath) : null;

        if (freshConfig == null)
        {
            Header("Error");
            Console.WriteLine("  Failed to reload config.");
            Pause(); continue;
        }

        var latestNames = freshConfig.Commands.Select(c => c.Name).ToHashSet();
        var missing = new List<string>();
        foreach (var item in selected)
        {
            if (item.IsAlias)
            {
                var badSteps = item.Alias!.Steps.Where(s => !latestNames.Contains(s)).ToList();
                foreach (var s in badSteps) missing.Add($"{item.Name} → {s}");
            }
            else if (!latestNames.Contains(item.Cmd!.Name))
            {
                missing.Add(item.Cmd.Name);
            }
        }
        if (missing.Count > 0)
        {
            Header("Error");
            Console.WriteLine("  The following commands no longer exist in config:\n");
            foreach (var m in missing) Console.WriteLine($"    {m}");
            Console.WriteLine("\n  Reload config and try again.");
            Pause(); continue;
        }
    }

    // --- Expand aliases and execute (using freshConfig) ---
    var toRun = new List<Command>();
    foreach (var item in selected)
    {
        if (item.IsAlias)
        {
            foreach (var stepName in item.Alias!.Steps)
            {
                var cmd = freshConfig.Commands.FirstOrDefault(c => c.Name == stepName);
                if (cmd != null)
                {
                    var stepVars = item.Alias.Vars?.TryGetValue(stepName, out var sv) == true
                        ? sv : (item.VarMap ?? new());
                    toRun.Add(ApplyVars(cmd, stepVars));
                }
            }
        }
        else
        {
            toRun.Add(item.Cmd!);
        }
    }

    Header("Running");
    var success = true;
    int completed = 0;
    for (int i = 0; i < toRun.Count; i++)
    {
        var cmd = toRun[i];
        Console.WriteLine($"  [{i + 1}/{toRun.Count}] {C.White}{cmd.Name}{C.Reset}");
        Console.WriteLine($"  {C.Gray}> {cmd.Cmd}{C.Reset}");
        if (!string.IsNullOrEmpty(cmd.Dir))
            Console.WriteLine($"  {C.Gray}  {cmd.Dir}{C.Reset}");
        Console.WriteLine();
        if (!RunCommand(cmd.Cmd, cmd.Dir, cmd.Shell))
        {
            Console.WriteLine($"  {C.Gray}Error: {cmd.Name} failed. Aborting.{C.Reset}");
            success = false;
            break;
        }
        completed++;
        Console.WriteLine(ProgressBar(completed, toRun.Count));
        Console.WriteLine();
    }

    Console.WriteLine(success ? $"  {C.Green}Done!{C.Reset}" : $"  {C.Gray}Aborted.{C.Reset}");
    if (!success) Pause();
    else
    {
        Thread.Sleep(1000);
        Console.ReadKey(true);
    }
}

} // end try
catch (Exception ex)
{
    Console.Clear();
    Console.WriteLine($"\n  {C.Gray}Unexpected error: {ex.Message}{C.Reset}");
    Console.WriteLine($"\n  {C.Gray}{ex.GetType().Name}{C.Reset}");
    Console.WriteLine("\n  Press Enter to exit...");
    Console.ReadLine();
}

// --- Load config ---

static (Config? config, List<Workflow> workflows, string? lastWorkflow, string workflowsPath, string lastPath,
        List<Alias> aliases, string aliasesPath, Dictionary<string, List<VarEntry>> vars, string varsDir)
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

static string? SelectProjectDir(List<string> dirs)
{
    var names = dirs.Select(Path.GetFileName).ToList();
    var selected = SingleSelectStr("Select project", names!);
    if (selected == null) return null;
    return dirs[names.IndexOf(selected)];
}

// --- TSV parser ---

static Config? ParseTsv(string path)
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

// --- Var helpers ---

static Dictionary<string, List<VarEntry>> LoadVarsFromDir(string varsDir)
{
    var result = new Dictionary<string, List<VarEntry>>();
    if (!Directory.Exists(varsDir)) return result;
    foreach (var file in Directory.GetFiles(varsDir, "*.tsv").OrderBy(f => f))
    {
        var varName = Path.GetFileNameWithoutExtension(file);
        var entries = File.ReadAllLines(file)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l =>
            {
                var parts = l.Split('\t', 2);
                var value = parts[0].Trim();
                var label = parts.Length > 1 ? parts[1].Trim() : null;
                return new VarEntry(value, string.IsNullOrEmpty(label) ? null : label);
            })
            .Where(e => !string.IsNullOrEmpty(e.Value))
            .ToList();
        if (entries.Count > 0) result[varName] = entries;
    }
    return result;
}

static void SaveVar(string varsDir, string varName, List<VarEntry> entries)
{
    try
    {
        Directory.CreateDirectory(varsDir);
        var lines = entries.Select(e => string.IsNullOrEmpty(e.Label) ? e.Value : $"{e.Value}\t{e.Label}");
        File.WriteAllLines(Path.Combine(varsDir, $"{varName}.tsv"), lines);
    }
    catch (Exception ex) { Console.WriteLine($"\n  Failed to save var: {ex.Message}"); Pause(); }
}

static List<string> ExtractVarNames(Command cmd)
{
    var names = new HashSet<string>();
    foreach (Match m in C.VarPattern.Matches(cmd.Cmd))       names.Add(m.Groups[1].Value);
    foreach (Match m in C.VarPattern.Matches(cmd.Dir ?? "")) names.Add(m.Groups[1].Value);
    return names.ToList();
}

static Command ApplyVars(Command cmd, Dictionary<string, string> varMap)
{
    if (varMap.Count == 0) return cmd;
    var resolvedCmd = cmd.Cmd;
    var resolvedDir = cmd.Dir;
    foreach (var (k, v) in varMap)
    {
        resolvedCmd = resolvedCmd.Replace($"{{{k}}}", v);
        resolvedDir = resolvedDir?.Replace($"{{{k}}}", v);
    }
    return cmd with { Cmd = resolvedCmd, Dir = resolvedDir };
}

// Show TUI selector for a variable; returns chosen value or null (Esc)
static string? PromptVar(string varName, List<VarEntry> entries,
    List<string>? cmdList = null, int cmdIndex = -1, List<string?>? cmdNotes = null)
{
    int cursor = 0;
    string search = "";

    void RenderCmdContext()
    {
        for (int i = 0; i < cmdList!.Count; i++)
        {
            var note = cmdNotes != null && i < cmdNotes.Count && cmdNotes[i] != null
                ? $"  {cmdNotes[i]}" : "";
            Console.WriteLine(i == cmdIndex
                ? $"  {C.CursorBg} {i + 1,2}. {cmdList[i]}{note}{C.Reset}"
                : $"  {C.Gray}  {i + 1,2}. {cmdList[i]}{note}{C.Reset}");
        }
        Console.WriteLine();
        Console.WriteLine(HLineLabel("Select value"));
        Console.WriteLine();
    }

    while (true)
    {
        var filtered = string.IsNullOrEmpty(search)
            ? entries
            : entries.Where(e =>
                e.Value.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (e.Label ?? "").Contains(search, StringComparison.OrdinalIgnoreCase))
              .ToList();

        if (cursor > filtered.Count) cursor = filtered.Count;

        int labelCol = filtered.Any(e => e.Label != null)
            ? filtered.Max(e => e.Value.Length) + 2 : 0;

        Header($"Variable: {varName}");

        if (cmdList != null)
        {
            RenderCmdContext();
        }

        // Search field
        Console.WriteLine($"  {C.Cyan}/{C.Reset} {search}{C.Dim}_\x1b[0m");
        Console.WriteLine();

        int contextLines = (cmdList != null ? cmdList.Count + 2 : 0) + 2;
        int viewHeight = Math.Max(1, Console.WindowHeight - 8 - contextLines);
        int viewStart  = Math.Max(0, Math.Min(cursor - viewHeight / 2, filtered.Count + 1 - viewHeight));
        var viewEnd    = Math.Min(viewStart + viewHeight, filtered.Count + 1);

        if (viewStart > 0) Console.WriteLine($"  {C.Gray}...{C.Reset}");
        for (int i = viewStart; i < viewEnd; i++)
        {
            string line;
            if (i == filtered.Count)
            {
                line = $"{C.Gray}[ Type... ]{C.Reset}";
            }
            else
            {
                var e = filtered[i];
                var labelPart = e.Label != null
                    ? $"{new string(' ', Math.Max(1, labelCol - e.Value.Length))}{C.Gray}{e.Label}{C.Reset}"
                    : "";
                line = $"{e.Value}{labelPart}";
            }
            Console.WriteLine(i == cursor
                ? $"  {C.Cyan}>{C.Reset} {line}"
                : $"    {line}");
        }
        if (viewEnd < filtered.Count + 1) Console.WriteLine($"  {C.Gray}...{C.Reset}");

        Console.WriteLine();
        PanelHint("↑↓ Enter Esc");

        var key = Console.ReadKey(true);
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                cursor = (cursor - 1 + filtered.Count + 1) % (filtered.Count + 1); break;
            case ConsoleKey.DownArrow:
            case ConsoleKey.Tab:
                cursor = (cursor + 1) % (filtered.Count + 1); break;
            case ConsoleKey.Backspace:
                if (search.Length > 0) { search = search[..^1]; cursor = 0; }
                break;
            case ConsoleKey.Enter:
                if (cursor == filtered.Count)
                {
                    Header($"Variable: {varName}");
                    if (cmdList != null) RenderCmdContext();
                    Console.WriteLine("  Esc: cancel\n");
                    return ReadInput($"  {varName} > ", search);
                }
                return filtered[cursor].Value;
            case ConsoleKey.Escape:
                return null;
            default:
                if (key.KeyChar >= 32 && key.KeyChar != 127)
                { search += key.KeyChar; cursor = 0; }
                break;
        }
    }
}

// TUI for editing var entries (value + optional label)
static List<VarEntry> EditVarEntries(string varName, List<VarEntry> existing)
{
    var entries = new List<VarEntry>(existing);
    int cursor = 0;

    while (true)
    {
        var total = entries.Count + 1; // +1 for "+ Add value"
        Header($"Var: {varName}");
        Console.WriteLine($"  {C.Gray}Del: remove   Esc: done{C.Reset}\n");

        int labelCol = entries.Any(e => e.Label != null)
            ? entries.Max(e => e.Value.Length) + 2
            : 0;

        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var labelPart = e.Label != null
                ? $"{new string(' ', Math.Max(1, labelCol - e.Value.Length))}{C.Gray}{e.Label}{C.Reset}"
                : "";
            var line = $"{e.Value}{labelPart}";
            Console.WriteLine(i == cursor
                ? $"  {C.Cyan}>{C.Reset} {line}"
                : $"    {line}");
        }

        Console.WriteLine(cursor == entries.Count
            ? $"  {C.Cyan}>{C.Reset} {C.Green}+ Add value{C.Reset}"
            : $"    {C.Gray}+ Add value{C.Reset}");

        Console.WriteLine();
        PanelHint("↑↓ Enter  Del: remove  Esc: done");

        var key = Console.ReadKey(true);
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                cursor = (cursor - 1 + total) % total; break;
            case ConsoleKey.DownArrow:
            case ConsoleKey.Tab:
                cursor = (cursor + 1) % total; break;
            case ConsoleKey.Delete:
                if (cursor < entries.Count)
                {
                    entries.RemoveAt(cursor);
                    if (cursor >= entries.Count && cursor > 0) cursor--;
                }
                break;
            case ConsoleKey.Enter:
                if (cursor == entries.Count)
                {
                    Header($"Add value: {varName}");
                    Console.WriteLine("  Esc: cancel\n");
                    var val = ReadInput("  Value > ");
                    if (string.IsNullOrEmpty(val)) break;
                    Console.WriteLine($"  {C.Gray}Label (Enter to skip){C.Reset}");
                    var lbl = ReadOptionalInput("  Label > ");
                    entries.Add(new VarEntry(val, string.IsNullOrEmpty(lbl) ? null : lbl));
                    cursor = entries.Count - 1;
                }
                break;
            case ConsoleKey.Escape:
                return entries;
        }
    }
}

// Review and optionally re-edit collected workflow vars before saving
static void ReviewWorkflowVars(
    List<Command> cmdSelected,
    Dictionary<string, Dictionary<string, string>> workflowVars,
    Dictionary<string, List<VarEntry>> vars)
{
    // Build flat list: (cmdIdx, cmdName, varName) for commands that have vars, in selection order
    var rows = new List<(int cmdIdx, string cmdName, string varName)>();
    for (int ci = 0; ci < cmdSelected.Count; ci++)
    {
        var cmd = cmdSelected[ci];
        if (!workflowVars.TryGetValue(cmd.Name, out var cmdVars)) continue;
        foreach (var varName in cmdVars.Keys)
            rows.Add((ci, cmd.Name, varName));
    }
    if (rows.Count == 0) return;

    var cmdNames = cmdSelected.Select(c => c.Name).ToList();
    int labelWidth = rows.Max(r => $"{r.cmdIdx + 1}. {r.cmdName}".Length);
    int cursor = 0;
    int btn = 0; // 0=Confirm, 1=Edit
    bool listMode = false;

    while (true)
    {
        Header("Confirm vars");
        Console.WriteLine($"  {C.Gray}Review the variables to be saved with this workflow.{C.Reset}\n");

        string? lastCmdName = null;
        for (int i = 0; i < rows.Count; i++)
        {
            var (cmdIdx, cmdName, varName) = rows[i];
            var value = workflowVars[cmdName][varName];
            var prefix = cmdName != lastCmdName ? $"{cmdIdx + 1}. {cmdName}" : "";
            lastCmdName = cmdName;
            var paddedPrefix = prefix.PadRight(labelWidth);
            var valueStr = $"{varName} = {value}";

            Console.WriteLine(listMode && i == cursor
                ? $"  {C.Cyan}>{C.Reset} {paddedPrefix}  {C.White}{valueStr}{C.Reset}"
                : $"    {C.Gray}{paddedPrefix}  {valueStr}{C.Reset}");
        }

        Console.WriteLine();
        Console.WriteLine(HLine());
        Console.WriteLine();

        var rc = !listMode && btn == 0 ? $"\x1b[92m{C.Bold}" : C.Gray;
        var ec = !listMode && btn == 1 ? $"{C.White}{C.Bold}" : C.Gray;
        Console.WriteLine($"  {rc}+-----------+{C.Reset}      {ec}+--------+{C.Reset}");
        Console.WriteLine($"  {rc}|  Confirm  |{C.Reset}      {ec}|  Edit  |{C.Reset}");
        Console.WriteLine($"  {rc}+-----------+{C.Reset}      {ec}+--------+{C.Reset}");

        if (listMode)
            Console.WriteLine($"\n  {C.Gray}↑↓ Space: edit   Enter: done{C.Reset}");
        else
            Console.WriteLine($"\n  {C.Gray}Tab: switch   Enter: confirm{C.Reset}");

        var key = Console.ReadKey(true);

        if (listMode)
        {
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    cursor = (cursor - 1 + rows.Count) % rows.Count; break;
                case ConsoleKey.DownArrow:
                    cursor = (cursor + 1) % rows.Count; break;
                case ConsoleKey.Spacebar:
                    var (selCmdIdx, selCmdName, selVarName) = rows[cursor];
                    var entries = vars.TryGetValue(selVarName, out var ch) ? ch : new List<VarEntry>();
                    var newValue = PromptVar(selVarName, entries, cmdNames, selCmdIdx);
                    if (newValue != null)
                        workflowVars[selCmdName][selVarName] = newValue;
                    break;
                case ConsoleKey.Enter:
                case ConsoleKey.Escape:
                    listMode = false; break;
            }
        }
        else
        {
            switch (key.Key)
            {
                case ConsoleKey.Tab:
                case ConsoleKey.LeftArrow:
                case ConsoleKey.RightArrow:
                    btn = 1 - btn; break;
                case ConsoleKey.Enter:
                    if (btn == 0) return;
                    listMode = true; cursor = 0; break;
                case ConsoleKey.Escape:
                    return;
            }
        }
    }
}

// Confirm vars screen for Run manually (flat varMap, no per-command grouping)
static void ReviewRunVars(Dictionary<string, string> varMap, Dictionary<string, List<VarEntry>> vars)
{
    var keys = varMap.Keys.ToList();
    int cursor = 0;
    int btn = 0; // 0=Confirm, 1=Edit
    bool listMode = false;

    while (true)
    {
        Header("Confirm vars");
        Console.WriteLine($"  {C.Gray}Review the variables for this run.{C.Reset}\n");

        for (int i = 0; i < keys.Count; i++)
        {
            var k = keys[i];
            var valueStr = $"{k} = {varMap[k]}";
            Console.WriteLine(listMode && i == cursor
                ? $"  {C.Cyan}>{C.Reset} {C.White}{valueStr}{C.Reset}"
                : $"  {C.Gray}  {valueStr}{C.Reset}");
        }

        Console.WriteLine();
        Console.WriteLine(HLine());
        Console.WriteLine();

        var rc = !listMode && btn == 0 ? $"\x1b[92m{C.Bold}" : C.Gray;
        var ec = !listMode && btn == 1 ? $"{C.White}{C.Bold}" : C.Gray;
        Console.WriteLine($"  {rc}+-----------+{C.Reset}      {ec}+--------+{C.Reset}");
        Console.WriteLine($"  {rc}|  Confirm  |{C.Reset}      {ec}|  Edit  |{C.Reset}");
        Console.WriteLine($"  {rc}+-----------+{C.Reset}      {ec}+--------+{C.Reset}");

        if (listMode)
            Console.WriteLine($"\n  {C.Gray}↑↓ Space: edit   Enter: done{C.Reset}");
        else
            Console.WriteLine($"\n  {C.Gray}Tab: switch   Enter: confirm{C.Reset}");

        var key = Console.ReadKey(true);

        if (listMode)
        {
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    cursor = (cursor - 1 + keys.Count) % keys.Count; break;
                case ConsoleKey.DownArrow:
                    cursor = (cursor + 1) % keys.Count; break;
                case ConsoleKey.Spacebar:
                    var selKey = keys[cursor];
                    var entries = vars.TryGetValue(selKey, out var ch) ? ch : new List<VarEntry>();
                    var newValue = PromptVar(selKey, entries);
                    if (newValue != null) varMap[selKey] = newValue;
                    break;
                case ConsoleKey.Enter:
                case ConsoleKey.Escape:
                    listMode = false; break;
            }
        }
        else
        {
            switch (key.Key)
            {
                case ConsoleKey.Tab:
                case ConsoleKey.LeftArrow:
                case ConsoleKey.RightArrow:
                    btn = 1 - btn; break;
                case ConsoleKey.Enter:
                    if (btn == 0) return;
                    listMode = true; cursor = 0; break;
                case ConsoleKey.Escape:
                    return;
            }
        }
    }
}

// --- Confirm dialog ---

static bool ConfirmDialog(List<RunItem> items)
{
    int btn = 0;
    while (true)
    {
        Header("Confirm");
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.IsAlias)
            {
                var steps = string.Join(" > ", item.Alias!.Steps);
                Console.WriteLine($"  {C.Gray}{i + 1,2}.{C.Reset}  {C.Cyan}@{C.Reset} {item.Name}  {C.Gray}({steps}){C.Reset}");
            }
            else
            {
                var c = item.Cmd!;
                var group = string.IsNullOrEmpty(c.Group) ? "" : $"  {C.GroupTag}[{c.Group}]{C.Reset}";
                Console.WriteLine($"  {C.Gray}{i + 1,2}.{C.Reset}  {c.Name}{group}");
                if (!string.IsNullOrEmpty(c.Dir))
                    Console.WriteLine($"       {C.Gray}{c.Dir}{C.Reset}");
            }
        }
        Console.WriteLine();
        Console.WriteLine(HLine());
        Console.WriteLine();

        var rc = btn == 0 ? $"\x1b[92m{C.Bold}" : C.Gray;
        var cc = btn == 1 ? $"{C.White}{C.Bold}" : C.Gray;
        Console.WriteLine($"  {rc}+---------+{C.Reset}      {cc}+------------+{C.Reset}");
        Console.WriteLine($"  {rc}|   Run   |{C.Reset}      {cc}|   Cancel   |{C.Reset}");
        Console.WriteLine($"  {rc}+---------+{C.Reset}      {cc}+------------+{C.Reset}");
        Console.WriteLine($"\n  {C.Gray}Tab: switch   Enter: confirm   Esc: back{C.Reset}");

        switch (Console.ReadKey(true).Key)
        {
            case ConsoleKey.Enter:                    return btn == 0;
            case ConsoleKey.Escape:                   return false;
            case ConsoleKey.LeftArrow:
            case ConsoleKey.RightArrow:
            case ConsoleKey.Tab:                      btn = 1 - btn; break;
        }
    }
}

// --- Progress bar ---

static string ProgressBar(int done, int total, int width = 24)
{
    int filled = total > 0 ? (int)((double)done / total * width) : 0;
    int pct    = total > 0 ? (int)((double)done / total * 100)   : 0;
    var bar    = $"{C.Green}{new string('#', filled)}{C.Gray}{new string('-', width - filled)}{C.Reset}";
    return $"  [{bar}]  {C.White}{done}/{total}{C.Reset}  {C.Gray}{pct}%{C.Reset}";
}

// --- Panel helpers ---

static int DetectBoxCharCols()
{
    var lang = CultureInfo.CurrentCulture.TwoLetterISOLanguageName;
    return (lang == "ja" || lang == "zh" || lang == "ko") ? 2 : 1;
}

static string HLine()
{
    var w = Math.Max(0, (Console.WindowWidth - 4) / C.BoxCharCols);
    return C.Gray + "  " + new string('─', w) + C.Reset;
}

static string HLineLabel(string label)
{
    var w = Math.Max(0, (Console.WindowWidth - 4 - label.Length - 4) / C.BoxCharCols);
    return $"{C.Gray}  ── {label} {new string('─', w)}{C.Reset}";
}

static void PanelHint(string hint)
{
    Console.WriteLine(HLine());
    Console.WriteLine($"  {C.Gray}{hint}{C.Reset}");
}

static void Header(string title)
{
    Console.Clear();
    Console.WriteLine($"  {C.Cyan}{C.Bold}[ {title} ]{C.Reset}");
    Console.WriteLine(HLine());
    Console.WriteLine();
}

// --- Command select with real-time search + scroll ---

static List<Command> MultiSelect(string prompt, List<Command> items, HashSet<string>? preSelected = null)
{
    var selectedIdx = new List<int>();
    if (preSelected != null)
        for (int i = 0; i < items.Count; i++)
            if (preSelected.Contains(items[i].Name)) selectedIdx.Add(i);

    var hasVarsMap = items.Select(cmd => ExtractVarNames(cmd).Count > 0).ToArray();

    int cursor = 0, viewStart = 0;
    string search = "", groupSearch = "";
    int activeField = 0;

    while (true)
    {
        var filtered = items
            .Select((item, idx) => (item, idx))
            .Where(x =>
                (string.IsNullOrEmpty(search) ||
                 x.item.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                 (x.item.Group ?? "").Contains(search, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrEmpty(groupSearch) ||
                 (x.item.Group ?? "").Contains(groupSearch, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (cursor >= filtered.Count) cursor = Math.Max(0, filtered.Count - 1);

        int viewHeight = Math.Max(1, Console.WindowHeight - 9);
        if (cursor < viewStart) viewStart = cursor;
        if (cursor >= viewStart + viewHeight) viewStart = cursor - viewHeight + 1;

        Header(prompt);

        var f0 = activeField == 0 ? C.Cyan : C.Gray;
        var f1 = activeField == 1 ? C.Cyan : C.Gray;
        var cur0 = activeField == 0 ? $"{C.Dim}_\u001b[0m" : "";
        var cur1 = activeField == 1 ? $"{C.Dim}_\u001b[0m" : "";
        Console.WriteLine($"  {f0}/{C.Reset} {search}{cur0}        {f1}Group /{C.Reset} {groupSearch}{cur1}");
        Console.WriteLine();

        if (filtered.Count == 0)
        {
            Console.WriteLine($"  {C.Gray}No results.{C.Reset}");
        }
        else
        {
            if (viewStart > 0) Console.WriteLine($"  {C.Gray}...{C.Reset}");
            var viewEnd = Math.Min(viewStart + viewHeight, filtered.Count);
            for (int i = viewStart; i < viewEnd; i++)
            {
                var (cmd, origIdx) = filtered[i];
                var selOrder = selectedIdx.IndexOf(origIdx);
                var check   = selOrder >= 0 ? $"{C.SelNum}[{selOrder + 1}]{C.Reset}" : $"{C.Gray}[ ]{C.Reset}";
                var group   = string.IsNullOrEmpty(cmd.Group) ? "" : $"  {C.GroupTag}[{cmd.Group}]{C.Reset}";
                var hasVars = hasVarsMap[origIdx] ? $"  {C.Gray}{{...}}{C.Reset}" : "";
                Console.WriteLine(i == cursor
                    ? $"  {C.Cyan}>{C.Reset} {check} {cmd.Name}{group}{hasVars}"
                    : $"    {check} {cmd.Name}{group}{hasVars}");
            }
            if (viewEnd < filtered.Count) Console.WriteLine($"  {C.Gray}...{C.Reset}");
        }

        var selectedNames = selectedIdx.Select(i => items[i].Name).ToList();
        Console.WriteLine($"\n  {C.Green}Selected({selectedIdx.Count}){C.Reset}: {string.Join(", ", selectedNames)}");
        PanelHint("↑↓ Space Enter Esc  |  Tab: switch field");

        var key = Console.ReadKey(true);
        switch (key.Key)
        {
            case ConsoleKey.Tab:
                activeField = 1 - activeField; break;
            case ConsoleKey.UpArrow:
                if (filtered.Count > 0) cursor = (cursor - 1 + filtered.Count) % filtered.Count; break;
            case ConsoleKey.DownArrow:
                if (filtered.Count > 0) cursor = (cursor + 1) % filtered.Count; break;
            case ConsoleKey.Spacebar:
                if (filtered.Count > 0)
                {
                    var origIdx = filtered[cursor].idx;
                    if (selectedIdx.Contains(origIdx)) selectedIdx.Remove(origIdx);
                    else selectedIdx.Add(origIdx);
                }
                break;
            case ConsoleKey.Backspace:
                if (activeField == 0 && search.Length > 0) { search = search[..^1]; cursor = 0; viewStart = 0; }
                else if (activeField == 1 && groupSearch.Length > 0) { groupSearch = groupSearch[..^1]; cursor = 0; viewStart = 0; }
                break;
            case ConsoleKey.Enter:
                return selectedIdx.Select(i => items[i]).ToList();
            case ConsoleKey.Escape:
                return new List<Command>();
            default:
                if (!char.IsControl(key.KeyChar))
                {
                    if (activeField == 0) search += key.KeyChar;
                    else groupSearch += key.KeyChar;
                    cursor = 0; viewStart = 0;
                }
                break;
        }
    }
}

// --- Command + Alias select (for Run manually) ---

static List<RunItem> MultiSelectWithAliases(string prompt, List<Command> commands, List<Alias> aliases)
{
    var allItems = commands.Select(c => new RunItem(c.Name, c, null, null))
        .Concat(aliases.Select(a => new RunItem(a.Name, null, a, null)))
        .ToList();

    var hasVarsMap = allItems.Select(i => !i.IsAlias && ExtractVarNames(i.Cmd!).Count > 0).ToArray();

    var selectedIdx = new List<int>();
    int cursor = 0, viewStart = 0;
    string search = "", groupSearch = "";
    int activeField = 0;

    while (true)
    {
        var filtered = allItems
            .Select((item, idx) => (item, idx))
            .Where(x =>
            {
                var grp = x.item.IsAlias ? "alias" : (x.item.Cmd?.Group ?? "");
                return (string.IsNullOrEmpty(search) ||
                        x.item.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        grp.Contains(search, StringComparison.OrdinalIgnoreCase)) &&
                       (string.IsNullOrEmpty(groupSearch) ||
                        grp.Contains(groupSearch, StringComparison.OrdinalIgnoreCase));
            })
            .ToList();

        if (cursor >= filtered.Count) cursor = Math.Max(0, filtered.Count - 1);

        int viewHeight = Math.Max(1, Console.WindowHeight - 9);
        if (cursor < viewStart) viewStart = cursor;
        if (cursor >= viewStart + viewHeight) viewStart = cursor - viewHeight + 1;

        Header(prompt);

        var f0 = activeField == 0 ? C.Cyan : C.Gray;
        var f1 = activeField == 1 ? C.Cyan : C.Gray;
        var cur0 = activeField == 0 ? $"{C.Dim}_\u001b[0m" : "";
        var cur1 = activeField == 1 ? $"{C.Dim}_\u001b[0m" : "";
        Console.WriteLine($"  {f0}/{C.Reset} {search}{cur0}        {f1}Group /{C.Reset} {groupSearch}{cur1}");
        Console.WriteLine();

        if (filtered.Count == 0)
        {
            Console.WriteLine($"  {C.Gray}No results.{C.Reset}");
        }
        else
        {
            if (viewStart > 0) Console.WriteLine($"  {C.Gray}...{C.Reset}");
            var viewEnd = Math.Min(viewStart + viewHeight, filtered.Count);
            for (int i = viewStart; i < viewEnd; i++)
            {
                var (item, origIdx) = filtered[i];
                var selOrder = selectedIdx.IndexOf(origIdx);
                var check = selOrder >= 0 ? $"{C.SelNum}[{selOrder + 1}]{C.Reset}" : $"{C.Gray}[ ]{C.Reset}";

                string label;
                if (item.IsAlias)
                {
                    var steps = string.Join($" {C.Gray}>{C.Reset} ", item.Alias!.Steps);
                    label = $"{C.Cyan}@{C.Reset} {item.Name}  {C.GroupTag}[alias]{C.Reset}  {C.Gray}{steps}{C.Reset}";
                }
                else
                {
                    var group   = string.IsNullOrEmpty(item.Cmd!.Group) ? "" : $"  {C.GroupTag}[{item.Cmd.Group}]{C.Reset}";
                    var hasVars = hasVarsMap[origIdx] ? $"  {C.Gray}{{...}}{C.Reset}" : "";
                    label = $"{item.Name}{group}{hasVars}";
                }

                Console.WriteLine(i == cursor
                    ? $"  {C.Cyan}>{C.Reset} {check} {label}"
                    : $"    {check} {label}");
            }
            if (viewEnd < filtered.Count) Console.WriteLine($"  {C.Gray}...{C.Reset}");
        }

        var selectedNames = selectedIdx.Select(i => allItems[i].Name).ToList();
        Console.WriteLine($"\n  {C.Green}Selected({selectedIdx.Count}){C.Reset}: {string.Join(", ", selectedNames)}");
        PanelHint("↑↓ Space Enter Esc  |  Tab: switch field");

        var key = Console.ReadKey(true);
        switch (key.Key)
        {
            case ConsoleKey.Tab:
                activeField = 1 - activeField; break;
            case ConsoleKey.UpArrow:
                if (filtered.Count > 0) cursor = (cursor - 1 + filtered.Count) % filtered.Count; break;
            case ConsoleKey.DownArrow:
                if (filtered.Count > 0) cursor = (cursor + 1) % filtered.Count; break;
            case ConsoleKey.Spacebar:
                if (filtered.Count > 0)
                {
                    var origIdx = filtered[cursor].idx;
                    if (selectedIdx.Contains(origIdx)) selectedIdx.Remove(origIdx);
                    else selectedIdx.Add(origIdx);
                }
                break;
            case ConsoleKey.Backspace:
                if (activeField == 0 && search.Length > 0) { search = search[..^1]; cursor = 0; viewStart = 0; }
                else if (activeField == 1 && groupSearch.Length > 0) { groupSearch = groupSearch[..^1]; cursor = 0; viewStart = 0; }
                break;
            case ConsoleKey.Enter:
                return selectedIdx.Select(i => allItems[i]).ToList();
            case ConsoleKey.Escape:
                return new List<RunItem>();
            default:
                if (!char.IsControl(key.KeyChar))
                {
                    if (activeField == 0) search += key.KeyChar;
                    else groupSearch += key.KeyChar;
                    cursor = 0; viewStart = 0;
                }
                break;
        }
    }
}

// --- Generic multi-select ---

static List<T> MultiSelectGeneric<T>(string prompt, List<T> items, Func<T, string> label)
{
    var selectedIdx = new HashSet<int>();
    int cursor = 0, viewStart = 0;

    while (true)
    {
        int viewHeight = Math.Max(1, Console.WindowHeight - 7);
        if (cursor < viewStart) viewStart = cursor;
        if (cursor >= viewStart + viewHeight) viewStart = cursor - viewHeight + 1;

        Header(prompt);
        if (viewStart > 0) Console.WriteLine($"  {C.Gray}...{C.Reset}");
        var viewEnd = Math.Min(viewStart + viewHeight, items.Count);
        for (int i = viewStart; i < viewEnd; i++)
        {
            var check = selectedIdx.Contains(i) ? $"{C.SelNum}[x]{C.Reset}" : $"{C.Gray}[ ]{C.Reset}";
            Console.WriteLine(i == cursor
                ? $"  {C.Cyan}>{C.Reset} {check} {label(items[i])}"
                : $"    {check} {label(items[i])}");
        }
        if (viewEnd < items.Count) Console.WriteLine($"  {C.Gray}...{C.Reset}");

        Console.WriteLine($"\n  {C.Green}Selected({selectedIdx.Count}){C.Reset}");
        PanelHint("↑↓ Space Enter Esc");

        var key = Console.ReadKey(true);
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                cursor = (cursor - 1 + items.Count) % items.Count; break;
            case ConsoleKey.DownArrow:
            case ConsoleKey.Tab:
                cursor = (cursor + 1) % items.Count; break;
            case ConsoleKey.Spacebar:
                if (selectedIdx.Contains(cursor)) selectedIdx.Remove(cursor);
                else selectedIdx.Add(cursor);
                break;
            case ConsoleKey.Enter:
                return items.Where((_, i) => selectedIdx.Contains(i)).ToList();
            case ConsoleKey.Escape:
                return new List<T>();
        }
    }
}

// --- Single select ---

static T? SingleSelect<T>(string prompt, List<T> items, Func<T, string> label, int initialCursor = 0)
{
    int cursor = Math.Clamp(initialCursor, 0, items.Count - 1);
    int viewStart = 0;
    string search = "";

    while (true)
    {
        var filtered = items
            .Select((item, idx) => (item, idx))
            .Where(x => string.IsNullOrEmpty(search) ||
                        label(x.item).Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (cursor >= filtered.Count) cursor = Math.Max(0, filtered.Count - 1);

        int viewHeight = Math.Max(1, Console.WindowHeight - 7);
        if (cursor < viewStart) viewStart = cursor;
        if (cursor >= viewStart + viewHeight) viewStart = cursor - viewHeight + 1;

        Header(prompt);
        Console.WriteLine($"  {C.Cyan}/{C.Reset} {search}{C.Dim}_\n{C.Reset}");

        if (filtered.Count == 0)
        {
            Console.WriteLine($"  {C.Gray}No results.{C.Reset}");
        }
        else
        {
            if (viewStart > 0) Console.WriteLine($"  {C.Gray}...{C.Reset}");
            var viewEnd = Math.Min(viewStart + viewHeight, filtered.Count);
            for (int i = viewStart; i < viewEnd; i++)
            {
                Console.WriteLine(i == cursor
                    ? $"  {C.Cyan}>{C.Reset} {label(filtered[i].item)}"
                    : $"    {label(filtered[i].item)}");
            }
            if (viewEnd < filtered.Count) Console.WriteLine($"  {C.Gray}...{C.Reset}");
        }

        Console.WriteLine();
        PanelHint("↑↓ Enter Esc");

        var key = Console.ReadKey(true);
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                if (filtered.Count > 0) cursor = (cursor - 1 + filtered.Count) % filtered.Count; break;
            case ConsoleKey.DownArrow:
            case ConsoleKey.Tab:
                if (filtered.Count > 0) cursor = (cursor + 1) % filtered.Count; break;
            case ConsoleKey.Enter:
                return filtered.Count > 0 ? filtered[cursor].item : default;
            case ConsoleKey.Escape: return default;
            case ConsoleKey.Backspace:
                if (search.Length > 0) search = search[..^1];
                cursor = 0; viewStart = 0; break;
            default:
                if (!char.IsControl(key.KeyChar)) { search += key.KeyChar; cursor = 0; viewStart = 0; }
                break;
        }
    }
}

static string? SingleSelectStr(string prompt, List<string> items, bool isMain = false, string? subtitle = null)
{
    int cursor = 0;
    bool firstShow = isMain;
    while (true)
    {
        Console.Clear();
        if (isMain)
        {
            Console.WriteLine($"  ┌───────────────┐");
            Console.WriteLine($"  │  {C.Bold}{C.White}S H R U N{C.Reset}    │");
            Console.WriteLine($"  └───────────────┘");
            if (subtitle != null) Console.WriteLine($"  {C.Gray}project: {subtitle}{C.Reset}");
        }
        else
        {
            Console.WriteLine($"  {C.Cyan}{C.Bold}[ {prompt} ]{C.Reset}");
        }
        Console.WriteLine();

        for (int i = 0; i < items.Count; i++)
        {
            Console.WriteLine(i == cursor
                ? $"  {C.Cyan}>{C.Reset} {items[i]}"
                : $"    {items[i]}");
        }

        Console.WriteLine();
        PanelHint("↑↓ Enter Esc");

        if (firstShow)
        {
            firstShow = false;
            Thread.Sleep(300);
            while (Console.KeyAvailable) Console.ReadKey(true);
        }

        var key = Console.ReadKey(true);
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                cursor = (cursor - 1 + items.Count) % items.Count; break;
            case ConsoleKey.DownArrow:
            case ConsoleKey.Tab:
                cursor = (cursor + 1) % items.Count; break;
            case ConsoleKey.Enter: return items[cursor];
            case ConsoleKey.Escape: return null;
        }
    }
}

// --- Helpers ---

static bool RunCommand(string cmd, string? workDir, string? shell = null)
{
    try
    {
        var usePs = shell == "ps" || shell == "powershell";
        var psi = new ProcessStartInfo
        {
            FileName        = usePs ? "powershell.exe" : "cmd.exe",
            Arguments       = usePs ? $"-NoProfile -Command {cmd}" : $"/c {cmd}",
            UseShellExecute = false,
        };
        if (!string.IsNullOrEmpty(workDir))
            psi.WorkingDirectory = workDir;

        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        return proc.ExitCode == 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  {C.Gray}Error: {ex.Message}{C.Reset}");
        return false;
    }
}

static void SaveWorkflows(string path, List<Workflow> workflows)
{
    try { File.WriteAllText(path, JsonSerializer.Serialize(workflows, new JsonSerializerOptions { WriteIndented = true })); }
    catch (Exception ex) { Console.WriteLine($"\n  Failed to save workflows: {ex.Message}"); Pause(); }
}

static void SaveAliases(string path, List<Alias> aliases)
{
    try { File.WriteAllText(path, JsonSerializer.Serialize(aliases, new JsonSerializerOptions { WriteIndented = true })); }
    catch (Exception ex) { Console.WriteLine($"\n  Failed to save aliases: {ex.Message}"); Pause(); }
}

static string? ReadInput(string prompt, string prefill = "")
{
    Console.Write(prompt);
    var input = new System.Text.StringBuilder(prefill);
    if (!string.IsNullOrEmpty(prefill)) Console.Write(prefill);
    while (true)
    {
        var key = Console.ReadKey(true);
        if (key.Key == ConsoleKey.Enter)
        {
            var result = input.ToString().Trim();
            if (string.IsNullOrEmpty(result)) continue;
            Console.WriteLine();
            return result;
        }
        if (key.Key == ConsoleKey.Escape) return null;
        if (key.Key == ConsoleKey.Backspace)
        {
            if (input.Length > 0) { input.Remove(input.Length - 1, 1); Console.Write("\b \b"); }
        }
        else if (!char.IsControl(key.KeyChar))
        {
            input.Append(key.KeyChar);
            Console.Write(key.KeyChar);
        }
    }
}

// Like ReadInput but allows empty Enter (returns "" instead of ignoring)
static string ReadOptionalInput(string prompt)
{
    Console.Write(prompt);
    var input = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(true);
        if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return input.ToString().Trim(); }
        if (key.Key == ConsoleKey.Escape) { Console.WriteLine(); return ""; }
        if (key.Key == ConsoleKey.Backspace)
        {
            if (input.Length > 0) { input.Remove(input.Length - 1, 1); Console.Write("\b \b"); }
        }
        else if (!char.IsControl(key.KeyChar))
        {
            input.Append(key.KeyChar);
            Console.Write(key.KeyChar);
        }
    }
}

static void Pause()
{
    Console.WriteLine("\n  Press Enter to continue...");
    Console.ReadLine();
}

// --- Models ---

record Command(
    [property: JsonPropertyName("name")]  string Name,
    [property: JsonPropertyName("group")] string? Group,
    [property: JsonPropertyName("dir")]   string? Dir,
    [property: JsonPropertyName("cmd")]   string Cmd,
    [property: JsonPropertyName("shell")] string? Shell
);

record Config(
    [property: JsonPropertyName("commands")] List<Command> Commands
);

record Workflow(
    [property: JsonPropertyName("name")]     string Name,
    [property: JsonPropertyName("commands")] List<string> Commands,
    [property: JsonPropertyName("vars")]     Dictionary<string, Dictionary<string, string>>? Vars = null
);

record Alias(
    [property: JsonPropertyName("name")]  string Name,
    [property: JsonPropertyName("steps")] List<string> Steps,
    [property: JsonPropertyName("vars")]  Dictionary<string, Dictionary<string, string>>? Vars = null
);

// A single entry in a vars/*.tsv file: value + optional label
record VarEntry(string Value, string? Label = null);

// Represents a selectable item: either a Command or an Alias
record RunItem(string Name, Command? Cmd, Alias? Alias, Dictionary<string, string>? VarMap)
{
    public bool IsAlias => Alias != null;
}

static class C
{
    public const string Reset    = "\x1b[0m";
    public const string Bold     = "\x1b[1m";
    public const string Dim      = "\x1b[2m";
    public const string Green    = "\x1b[32m";
    public const string Cyan     = "\x1b[36m";
    public const string Gray     = "\x1b[90m";
    public const string White    = "\x1b[97m";
    public const string CursorBg = "\x1b[48;5;238m\x1b[97m";
    public const string SelNum   = "\x1b[32m";
    public const string GroupTag = "\x1b[90m";

    // Display columns used by box-drawing chars (1 = normal, 2 = East-Asian wide)
    public static int BoxCharCols = 1;

    public static readonly Regex VarPattern = new Regex(@"\{(\w+)\}", RegexOptions.Compiled);
}
