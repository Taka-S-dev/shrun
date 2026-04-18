using System.Text.Json;
using Shrun;
using static Shrun.Tui;
using static Shrun.Selectors;
using static Shrun.VarSystem;
using static Shrun.ConfigStore;
using static Shrun.Runner;

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
