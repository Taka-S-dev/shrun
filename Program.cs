using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

var baseDir     = AppContext.BaseDirectory;
var configPath  = Path.Combine(baseDir, "projects", "config.json");
var workflowsPath = Path.Combine(baseDir, "projects", "workflows.json");
var lastPath    = Path.Combine(baseDir, "projects", ".last");

if (!File.Exists(configPath))
{
    Console.WriteLine("projects/config.json not found");
    Pause(); return;
}

var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(configPath));
if (config == null || config.Commands.Count == 0)
{
    Console.WriteLine("Failed to load config.json");
    Pause(); return;
}

var workflows = File.Exists(workflowsPath)
    ? JsonSerializer.Deserialize<List<Workflow>>(File.ReadAllText(workflowsPath)) ?? new()
    : new List<Workflow>();

var lastWorkflow = File.Exists(lastPath) ? File.ReadAllText(lastPath).Trim() : null;

// Startup check: warn if any workflow references a command not found in config.json
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
    Console.WriteLine("  Some workflows reference commands not found in config.json:\n");
    foreach (var w in warnings) Console.WriteLine(w);
    Console.WriteLine("\n  Check config.json or edit the affected workflows.");
    Pause();
}

var modes = new List<string> { "Run workflow", "Run manually", "Create workflow", "Edit workflow", "Delete workflow", "Exit" };

while (true)
{
    var mode = SingleSelectStr("Mode", modes, isMain: true);
    if (mode == null || mode == "Exit") return;

    // --- Delete workflow ---
    if (mode == "Delete workflow")
    {
        if (workflows.Count == 0)
        {
            Header("Delete workflow"); Console.WriteLine("  No workflows found."); Pause(); continue;
        }
        var toDelete = MultiSelectGeneric("Delete workflow", workflows,
            p => $"{p.Name}  ({string.Join(" -> ", p.Commands)})");
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
        var toEdit = SingleSelect("Edit workflow", workflows,
            p => $"{p.Name}  ({string.Join(" -> ", p.Commands)})");
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
            workflows[workflows.IndexOf(toEdit)] = toEdit with { Commands = newCommands.Select(c => c.Name).ToList() };
        }

        SaveWorkflows(workflowsPath, workflows);
        Header("Edit workflow"); Console.WriteLine("  Saved."); Pause(); continue;
    }

    // --- Select commands ---
    List<Command> selected;

    if (mode == "Run workflow")
    {
        if (workflows.Count == 0)
        {
            Header("Run workflow");
            Console.WriteLine("  No workflows found.");
            Pause(); continue;
        }

        var initialCursor = lastWorkflow != null ? Math.Max(0, workflows.FindIndex(p => p.Name == lastWorkflow)) : 0;
        var workflow = SingleSelect("Run workflow", workflows,
            p => $"{p.Name}  ({string.Join(" -> ", p.Commands)})", initialCursor);
        if (workflow == null) continue;
        selected = config.Commands.Where(c => workflow.Commands.Contains(c.Name)).ToList();
        lastWorkflow = workflow.Name;
        File.WriteAllText(lastPath, lastWorkflow);
    }
    else if (mode == "Run manually")
    {
        selected = MultiSelect("Select commands", config.Commands);
        if (selected.Count == 0) continue;
    }
    else // Create workflow
    {
        selected = MultiSelect("Select commands", config.Commands);
        if (selected.Count == 0) continue;

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
            workflows.Add(new Workflow(name, selected.Select(c => c.Name).ToList()));
            SaveWorkflows(workflowsPath, workflows);
            Console.WriteLine("  Saved.");
            Pause();
        }
        continue;
    }

    // --- Confirm ---
    Header("Confirm");
    var sep = "  " + new string('-', 36);
    Console.WriteLine(sep);
    foreach (var c in selected)
    {
        var group = string.IsNullOrEmpty(c.Group) ? "" : $"   [{c.Group}]";
        Console.WriteLine($"    {c.Name}{group}");
    }
    Console.WriteLine(sep);
    Console.WriteLine("\n  Enter: run   Esc: back");

    if (Console.ReadKey(true).Key == ConsoleKey.Escape) continue;

    // --- Execute ---
    Header("Running");
    var success = true;
    for (int i = 0; i < selected.Count; i++)
    {
        var cmd = selected[i];
        Console.WriteLine($"[{i + 1}/{selected.Count}] {cmd.Name}");
        Console.WriteLine($"  > {cmd.Cmd}");
        if (!RunCommand(cmd.Cmd, cmd.Dir, cmd.Shell))
        {
            Console.WriteLine($"\nError: {cmd.Name} failed. Aborting.");
            success = false;
            break;
        }
        Console.WriteLine();
    }

    Console.WriteLine(success ? "Done!" : "Aborted.");
    Pause();
}

// --- Header ---

static void Header(string title)
{
    Console.Clear();
    Console.WriteLine($"  [ {title} ]");
    Console.WriteLine();
}

// --- Command select with real-time search + scroll ---

static List<Command> MultiSelect(string prompt, List<Command> items, HashSet<string>? preSelected = null)
{
    var selectedIdx = new HashSet<int>();
    if (preSelected != null)
        for (int i = 0; i < items.Count; i++)
            if (preSelected.Contains(items[i].Name)) selectedIdx.Add(i);

    int cursor = 0, viewStart = 0;
    string search = "";

    while (true)
    {
        var isGroupSearch = search.StartsWith("g:", StringComparison.OrdinalIgnoreCase);
        var searchTerm = isGroupSearch ? search[2..] : search;

        var filtered = items
            .Select((item, idx) => (item, idx))
            .Where(x => string.IsNullOrEmpty(searchTerm) ||
                        (isGroupSearch
                            ? x.item.Group.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)
                            : x.item.Name .Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                              x.item.Group.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (cursor >= filtered.Count) cursor = Math.Max(0, filtered.Count - 1);

        int viewHeight = Math.Max(1, Console.WindowHeight - 8);
        if (cursor < viewStart) viewStart = cursor;
        if (cursor >= viewStart + viewHeight) viewStart = cursor - viewHeight + 1;

        Header(prompt);
        Console.WriteLine($"  / {search}_\n");

        if (filtered.Count == 0)
        {
            Console.WriteLine("  No results.");
        }
        else
        {
            if (viewStart > 0) Console.WriteLine("  ...");
            var viewEnd = Math.Min(viewStart + viewHeight, filtered.Count);
            for (int i = viewStart; i < viewEnd; i++)
            {
                var (cmd, origIdx) = filtered[i];
                var check = selectedIdx.Contains(origIdx) ? "[x]" : "[ ]";
                var arrow = i == cursor ? ">" : " ";
                var group = string.IsNullOrEmpty(cmd.Group) ? "" : $"   [{cmd.Group}]";
                Console.WriteLine($"  {arrow} {check} {cmd.Name}{group}");
            }
            if (viewEnd < filtered.Count) Console.WriteLine("  ...");
        }

        var selectedNames = selectedIdx.Select(i => items[i].Name).ToList();
        Console.WriteLine($"\n  Selected({selectedIdx.Count}): {string.Join(", ", selectedNames)}");
        Console.WriteLine("  ↑↓ Space Enter Esc  |  g: filter by group");

        var key = Console.ReadKey(true);
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                if (filtered.Count > 0) cursor = (cursor - 1 + filtered.Count) % filtered.Count; break;
            case ConsoleKey.DownArrow:
            case ConsoleKey.Tab:
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
                if (search.Length > 0) search = search[..^1];
                cursor = 0; viewStart = 0; break;
            case ConsoleKey.Enter:
                return items.Select((item, idx) => (item, idx))
                    .Where(x => selectedIdx.Contains(x.idx)).Select(x => x.item).ToList();
            case ConsoleKey.Escape:
                return new List<Command>();
            default:
                if (!char.IsControl(key.KeyChar)) { search += key.KeyChar; cursor = 0; viewStart = 0; }
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
        if (viewStart > 0) Console.WriteLine("  ...");
        var viewEnd = Math.Min(viewStart + viewHeight, items.Count);
        for (int i = viewStart; i < viewEnd; i++)
        {
            var check = selectedIdx.Contains(i) ? "[x]" : "[ ]";
            var arrow = i == cursor ? ">" : " ";
            Console.WriteLine($"  {arrow} {check} {label(items[i])}");
        }
        if (viewEnd < items.Count) Console.WriteLine("  ...");

        Console.WriteLine($"\n  Selected({selectedIdx.Count})");
        Console.WriteLine("  ↑↓ Space Enter Esc");

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
        Console.WriteLine($"  / {search}_\n");

        if (filtered.Count == 0)
        {
            Console.WriteLine("  No results.");
        }
        else
        {
            if (viewStart > 0) Console.WriteLine("  ...");
            var viewEnd = Math.Min(viewStart + viewHeight, filtered.Count);
            for (int i = viewStart; i < viewEnd; i++)
                Console.WriteLine($"  {(i == cursor ? ">" : " ")} {label(filtered[i].item)}");
            if (viewEnd < filtered.Count) Console.WriteLine("  ...");
        }

        Console.WriteLine("\n  ↑↓ Enter Esc");

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

static string? SingleSelectStr(string prompt, List<string> items, bool isMain = false)
{
    int cursor = 0;
    while (true)
    {
        Console.Clear();
        if (isMain)
        {
            Console.WriteLine("  +---------------------------------+");
            Console.WriteLine("  |            SHRUN               |");
            Console.WriteLine("  +---------------------------------+");
        }
        else
        {
            Console.WriteLine($"  [ {prompt} ]");
        }
        Console.WriteLine();

        for (int i = 0; i < items.Count; i++)
            Console.WriteLine($"  {(i == cursor ? ">" : " ")} {items[i]}");

        Console.WriteLine("\n  ↑↓ Enter Esc");

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
    var usePs = shell == "ps" || shell == "powershell";
    var psi = new ProcessStartInfo
    {
        FileName    = usePs ? "powershell.exe" : "cmd.exe",
        Arguments   = usePs ? $"-NoProfile -Command {cmd}" : $"/c {cmd}",
        UseShellExecute = false,
    };
    if (!string.IsNullOrEmpty(workDir))
        psi.WorkingDirectory = workDir;

    using var proc = Process.Start(psi)!;
    proc.WaitForExit();
    return proc.ExitCode == 0;
}

static void SaveWorkflows(string path, List<Workflow> workflows) =>
    File.WriteAllText(path, JsonSerializer.Serialize(workflows, new JsonSerializerOptions { WriteIndented = true }));

static string? ReadInput(string prompt)
{
    Console.Write(prompt);
    var input = new System.Text.StringBuilder();
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
            if (input.Length > 0)
            {
                input.Remove(input.Length - 1, 1);
                Console.Write("\b \b");
            }
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
    [property: JsonPropertyName("group")] string Group,
    [property: JsonPropertyName("dir")]   string? Dir,
    [property: JsonPropertyName("cmd")]   string Cmd,
    [property: JsonPropertyName("shell")] string? Shell
);

record Config(
    [property: JsonPropertyName("commands")] List<Command> Commands
);

record Workflow(
    [property: JsonPropertyName("name")]     string Name,
    [property: JsonPropertyName("commands")] List<string> Commands
);
