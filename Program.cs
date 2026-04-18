using System.Text.Json;
using Shrun;
using static Shrun.Tui;
using static Shrun.Selectors;
using static Shrun.ListSystem;
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

var (config, workflows, lastWorkflow, workflowsPath, lastPath, aliases, aliasesPath, lists, listsDir) = LoadConfig(projectDir);
if (config == null) return;

var modes = new List<string> { "Run workflow", "Run manually", "Create workflow", "Edit workflow", "Delete workflow", "Manage aliases", "Manage lists", "Switch config", "Exit" };

try
{

while (true)
{
    // Reload config and lists on each iteration so selection screens always show latest
    try
    {
        var reloadJson = Path.Combine(projectDir, "config.json");
        var reloadTsv  = Path.Combine(projectDir, "config.tsv");
        var reloaded = File.Exists(reloadJson)
            ? JsonSerializer.Deserialize<Config>(File.ReadAllText(reloadJson))
            : File.Exists(reloadTsv) ? ParseTsv(reloadTsv) : null;
        if (reloaded != null && reloaded.Commands.Count > 0) config = reloaded;

        lists = LoadListsFromDir(listsDir);
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
        (config, workflows, lastWorkflow, workflowsPath, lastPath, aliases, aliasesPath, lists, listsDir) = LoadConfig(projectDir);
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
                var slotNames = ExtractAllSlotNames(cmd);
                if (slotNames.Count == 0) continue;
                var cmdVars = new Dictionary<string, string>();
                foreach (var slotName in slotNames)
                {
                    var listName = cmd.Vars?.TryGetValue(slotName, out var ln) == true ? ln : slotName;
                    var entries = lists.TryGetValue(listName, out var ch) ? ch : [];
                    var value = PromptList(slotName, entries, stepNames, si, aliasCmdNotes, cmd, cmdVars);
                    if (value == null) { aliasCancelled = true; break; }
                    cmdVars[slotName] = value;
                    var partialAlias = ApplyVarValues(cmd, cmdVars);
                    aliasCmdNotes[si] = !string.IsNullOrEmpty(partialAlias.Dir)
                        ? $"{partialAlias.Cmd}  (dir: {partialAlias.Dir})"
                        : partialAlias.Cmd;
                }
                if (aliasCancelled) break;
                aliasVarMap[cmd.Name] = cmdVars;
            }
            if (aliasCancelled) continue;
            if (aliasVarMap.Count > 0) ReviewWorkflowValues(steps, aliasVarMap, lists);

            string? aliasName = null;
            while (true)
            {
                Header("Create alias");
                Console.WriteLine("  Esc: cancel\n");
                aliasName = ReadInput("  Alias name > ");
                if (string.IsNullOrEmpty(aliasName)) { aliasName = null; break; }
                var existing = aliases.FirstOrDefault(a => a.Name == aliasName);
                if (existing == null) break;
                Console.Write($"  \"{aliasName}\" already exists. Overwrite? (y/n) > ");
                if (Console.ReadLine()?.Trim().ToLower() == "y") { aliases.Remove(existing); break; }
                // n or Enter → ask for a different name
            }
            if (aliasName != null)
            {
                aliases.Add(new Alias(aliasName, steps.Select(c => c.Name).ToList(),
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

    // --- Manage lists ---
    if (mode == "Manage lists")
    {
        var listModes = new List<string> { "Create list", "Edit list", "Delete list" };
        var listMode = SingleSelectStr("Manage lists", listModes);
        if (listMode == null) continue;

        if (listMode == "Create list")
        {
            Header("Create list");
            Console.WriteLine("  Esc: cancel\n");
            var listName = ReadInput("  List name > ");
            if (string.IsNullOrEmpty(listName)) continue;

            if (lists.ContainsKey(listName))
            {
                Console.Write($"  \"{listName}\" already exists. Overwrite? (y/n) > ");
                if (Console.ReadLine()?.Trim().ToLower() != "y") { Pause(); continue; }
            }

            var entries = EditListEntries(listName, []);
            if (entries.Count > 0)
            {
                Directory.CreateDirectory(listsDir);
                SaveList(listsDir, listName, entries);
                lists[listName] = entries;
                Header("Create list"); Console.WriteLine("  Saved."); Pause();
            }
            continue;
        }

        if (listMode == "Edit list")
        {
            if (lists.Count == 0)
            {
                Header("Edit list"); Console.WriteLine("  No lists found."); Pause(); continue;
            }
            var listNames = lists.Keys.ToList();
            var selectedListName = SingleSelectStr("Edit list", listNames);
            if (selectedListName == null) continue;

            var editListModes = new List<string> { "Rename", "Edit values" };
            var editListMode = SingleSelectStr($"Edit: {selectedListName}", editListModes);
            if (editListMode == null) continue;

            if (editListMode == "Rename")
            {
                Header($"Rename: {selectedListName}");
                Console.WriteLine("  Esc: cancel\n");
                var newName = ReadInput("  New name > ");
                if (string.IsNullOrEmpty(newName)) continue;
                if (lists.ContainsKey(newName))
                {
                    Console.Write($"  \"{newName}\" already exists. Overwrite? (y/n) > ");
                    if (Console.ReadLine()?.Trim().ToLower() != "y") { Pause(); continue; }
                    var oldFile = Path.Combine(listsDir, $"{newName}.tsv");
                    File.Delete(oldFile);
                    lists.Remove(newName);
                }
                var entries = lists[selectedListName];
                var srcFile = Path.Combine(listsDir, $"{selectedListName}.tsv");
                var dstFile = Path.Combine(listsDir, $"{newName}.tsv");
                try { File.Move(srcFile, dstFile); } catch (FileNotFoundException) { }
                lists.Remove(selectedListName);
                lists[newName] = entries;
            }
            else
            {
                var newEntries = EditListEntries(selectedListName, lists[selectedListName]);
                SaveList(listsDir, selectedListName, newEntries);
                lists[selectedListName] = newEntries;
            }

            Header("Edit list"); Console.WriteLine("  Saved."); Pause(); continue;
        }

        if (listMode == "Delete list")
        {
            if (lists.Count == 0)
            {
                Header("Delete list"); Console.WriteLine("  No lists found."); Pause(); continue;
            }
            var listNames = lists.Keys.ToList();
            var toDeleteLists = MultiSelectGeneric("Delete list", listNames, v => v);
            if (toDeleteLists.Count == 0) continue;
            foreach (var v in toDeleteLists)
            {
                lists.Remove(v);
                var f = Path.Combine(listsDir, $"{v}.tsv");
                File.Delete(f);
            }
            Header("Delete list");
            Console.WriteLine($"  Deleted: {string.Join(", ", toDeleteLists)}");
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

        // Apply all stored values — no prompts needed at run time
        selected = workflow.Commands
            .Select(name => config.Commands.FirstOrDefault(c => c.Name == name))
            .Where(c => c != null)
            .Select(c =>
            {
                var storedVars = workflow.Vars?.TryGetValue(c!.Name, out var v) == true ? v : null;
                var resolved = storedVars != null ? ApplyVarValues(c!, storedVars) : c!;
                return new RunItem(resolved.Name, resolved, null, null);
            }).ToList();
        lastWorkflow = workflow.Name;
        File.WriteAllText(lastPath, lastWorkflow);
    }
    else if (mode == "Run manually")
    {
        var rawSelected = MultiSelectWithAliases("Select commands", config.Commands, aliases);
        if (rawSelected.Count == 0) continue;

        bool cancelled = false;
        var itemNames = rawSelected.Select(i => i.Name).ToList();
        var itemNotes = new List<string?>(new string?[rawSelected.Count]);
        var resolvedItems = new List<RunItem>();

        for (int ri = 0; ri < rawSelected.Count && !cancelled; ri++)
        {
            var item = rawSelected[ri];

            if (item.IsAlias)
            {
                if (item.Alias!.Vars != null)
                {
                    // Stored vars — no prompting needed
                    resolvedItems.Add(item);
                }
                else
                {
                    // Prompt for all slots in each alias step
                    var aliasVarMap = new Dictionary<string, string>();
                    var stepCmds = item.Alias.Steps
                        .Select(s => config.Commands.FirstOrDefault(c => c.Name == s))
                        .Where(c => c != null).Select(c => c!).ToList();

                    foreach (var stepCmd in stepCmds)
                    {
                        foreach (var slotName in ExtractAllSlotNames(stepCmd))
                        {
                            if (aliasVarMap.ContainsKey(slotName)) continue;
                            var listName = stepCmd.Vars?.TryGetValue(slotName, out var ln) == true ? ln : slotName;
                            var entries = lists.TryGetValue(listName, out var ch) ? ch : [];
                            var value = PromptList(slotName, entries, itemNames, ri, itemNotes, stepCmd);
                            if (value == null) { cancelled = true; break; }
                            aliasVarMap[slotName] = value;
                        }
                        if (cancelled) break;
                    }
                    if (!cancelled)
                        resolvedItems.Add(new RunItem(item.Name, null, item.Alias, aliasVarMap.Count > 0 ? aliasVarMap : null));
                }
            }
            else
            {
                var cmd = item.Cmd!;

                // Step 1: Variables defined in cmd.Vars — prompted once, reused everywhere
                var varValues = PromptVarValues(cmd, lists, itemNames, ri, itemNotes);
                if (varValues == null) { cancelled = true; break; }

                if (varValues.Count > 0)
                    itemNotes[ri] = string.Join(" ", varValues.Select(kv => $"{{{kv.Key}={kv.Value}}}"));

                var cmdWithVars = ApplyVarValues(cmd, varValues);

                // Step 2: List selections — each {listName} occurrence prompted independently
                var resolved = ResolveListSelections(cmdWithVars, lists, itemNames, ri, itemNotes);
                if (resolved == null) { cancelled = true; break; }

                resolvedItems.Add(new RunItem(item.Name, resolved, null, null));
            }
        }

        if (cancelled) continue;
        selected = resolvedItems;
    }
    else // Create workflow
    {
        var cmdSelected = MultiSelect("Select commands", config.Commands);
        if (cmdSelected.Count == 0) continue;

        // Prompt for all unique placeholders; use cmd.Vars to determine which list to use
        var workflowVars = new Dictionary<string, Dictionary<string, string>>();
        bool cancelled = false;
        var cmdNames = cmdSelected.Select(c => c.Name).ToList();
        var cmdNotes = new List<string?>(new string?[cmdSelected.Count]);
        for (int ci = 0; ci < cmdSelected.Count; ci++)
        {
            var cmd = cmdSelected[ci];
            var slotNames = ExtractAllSlotNames(cmd);
            if (slotNames.Count == 0) continue;

            var cmdVars = new Dictionary<string, string>();
            foreach (var slotName in slotNames)
            {
                var listName = cmd.Vars?.TryGetValue(slotName, out var ln) == true ? ln : slotName;
                var entries = lists.TryGetValue(listName, out var ch) ? ch : [];
                var value = PromptList(slotName, entries, cmdNames, ci, cmdNotes, cmd, cmdVars);
                if (value == null) { cancelled = true; break; }
                cmdVars[slotName] = value;
                var partial = ApplyVarValues(cmd, cmdVars);
                cmdNotes[ci] = !string.IsNullOrEmpty(partial.Dir)
                    ? $"{partial.Cmd}  (dir: {partial.Dir})"
                    : partial.Cmd;
            }
            if (cancelled) break;
            workflowVars[cmd.Name] = cmdVars;
        }
        if (cancelled) continue;
        if (workflowVars.Count > 0) ReviewWorkflowValues(cmdSelected, workflowVars, lists);

        string? name = null;
        while (true)
        {
            Header("Create workflow");
            Console.WriteLine("  Esc: cancel\n");
            name = ReadInput("  Workflow name > ");
            if (string.IsNullOrEmpty(name)) { name = null; break; }
            var existing = workflows.FirstOrDefault(p => p.Name == name);
            if (existing == null) break;
            Console.Write($"  \"{name}\" already exists. Overwrite? (y/n) > ");
            if (Console.ReadLine()?.Trim().ToLower() == "y") { workflows.Remove(existing); break; }
            // n or Enter → ask for a different name
        }
        if (name != null)
        {
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
                    toRun.Add(ApplyVarValues(cmd, stepVars));
                }
            }
        }
        else
        {
            toRun.Add(item.Cmd!);
        }
    }

    // --- Validate: no unresolved placeholders ---
    var unresolved = toRun
        .Where(c => C.SlotPattern.IsMatch(c.Cmd) || C.SlotPattern.IsMatch(c.Dir ?? ""))
        .Select(c =>
        {
            var slots = C.SlotPattern.Matches(c.Cmd).Concat(C.SlotPattern.Matches(c.Dir ?? ""))
                .Select(m => $"{{{m.Groups[1].Value}}}").Distinct();
            return $"{c.Name}: {string.Join(", ", slots)}";
        }).ToList();
    if (unresolved.Count > 0)
    {
        Header("Error");
        Console.WriteLine("  Unresolved placeholders found:\n");
        foreach (var u in unresolved) Console.WriteLine($"    {u}");
        Console.WriteLine("\n  All placeholders must be resolved before running.");
        Pause(); continue;
    }

    int startFrom = 0;
    int retryCount = 0;
    bool success = false;
    while (true)
    {
        Header(retryCount == 0 ? "Running" : $"Running  {C.Gray}(retry {retryCount}){C.Reset}");
        success = true;
        int completed = 0;
        int failedAt = -1;
        for (int i = startFrom; i < toRun.Count; i++)
        {
            var cmd = toRun[i];
            Console.WriteLine($"  [{i + 1}/{toRun.Count}] {C.White}{cmd.Name}{C.Reset}");
            Console.WriteLine($"       {C.Gray}$ {cmd.Cmd}{C.Reset}");
            if (!string.IsNullOrEmpty(cmd.Dir))
                Console.WriteLine($"         {C.Dim}dir: {cmd.Dir}{C.Reset}");
            Console.WriteLine();
            if (!RunCommand(cmd.Cmd, cmd.Dir, cmd.Shell))
            {
                Console.WriteLine($"  {C.Gray}Error: {cmd.Name} failed.{C.Reset}");
                success = false;
                failedAt = i;
                break;
            }
            completed++;
            Console.WriteLine(ProgressBar(startFrom + completed, toRun.Count));
            Console.WriteLine();
        }

        if (success) break;

        Console.WriteLine();
        var remaining = string.Join(" → ", toRun.Skip(failedAt).Select(c => c.Name));
        var retryOptions = new List<string>
        {
            $"Retry from step {failedAt + 1}  ({remaining})",
            "Retry all",
            "Abort"
        };

        // Inline selector — no Console.Clear() so error output stays visible
        int retryCursor = 0;
        foreach (var opt in retryOptions)
            Console.WriteLine($"    {opt}");
        int optTop = Console.CursorTop - retryOptions.Count;
        Console.WriteLine();
        PanelHint("↑↓ Enter Esc");

        while (true)
        {
            for (int r = 0; r < retryOptions.Count; r++)
            {
                Console.SetCursorPosition(0, optTop + r);
                Console.Write(r == retryCursor
                    ? $"  {C.Cyan}>{C.Reset} {retryOptions[r]}          "
                    : $"    {retryOptions[r]}          ");
            }
            var rk = Console.ReadKey(true);
            if (rk.Key == ConsoleKey.UpArrow)        retryCursor = (retryCursor - 1 + retryOptions.Count) % retryOptions.Count;
            else if (rk.Key == ConsoleKey.DownArrow) retryCursor = (retryCursor + 1) % retryOptions.Count;
            else if (rk.Key == ConsoleKey.Enter)     break;
            else if (rk.Key == ConsoleKey.Escape)    { retryCursor = 2; break; }
        }
        Console.WriteLine();

        if (retryCursor == 0) { startFrom = failedAt; retryCount++; }
        else if (retryCursor == 1) { startFrom = 0; retryCount++; }
        else break;
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
