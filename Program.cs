using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

Console.OutputEncoding = System.Text.Encoding.UTF8;
C.BoxCharCols = DetectBoxCharCols();

var baseDir     = AppContext.BaseDirectory;
var projectsDir = Path.Combine(baseDir, "projects");

if (!Directory.Exists(projectsDir))
{
    Console.WriteLine("projects/ folder not found");
    Pause(); return;
}

// Find project subdirectories that contain config.json
var projectDirs = Directory.GetDirectories(projectsDir)
    .Where(d => File.Exists(Path.Combine(d, "config.json")))
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

var (config, workflows, lastWorkflow, workflowsPath, lastPath) = LoadConfig(projectDir);
if (config == null) return;

var modes = new List<string> { "Run workflow", "Run manually", "Create workflow", "Edit workflow", "Delete workflow", "Switch config", "Exit" };

while (true)
{
    var mode = SingleSelectStr("Mode", modes, isMain: true, subtitle: Path.GetFileName(projectDir));
    if (mode == null || mode == "Exit") return;

    // --- Switch config ---
    if (mode == "Switch config")
    {
        var newDir = SelectProjectDir(projectDirs);
        if (newDir == null) continue;
        projectDir = newDir;
        (config, workflows, lastWorkflow, workflowsPath, lastPath) = LoadConfig(projectDir);
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
        selected = workflow.Commands
            .Select(name => config.Commands.FirstOrDefault(c => c.Name == name))
            .Where(c => c != null).Select(c => c!).ToList();
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
    if (!ConfirmDialog(selected)) continue;

    // --- Pre-run validation ---
    {
        var latestConfig = JsonSerializer.Deserialize<Config>(File.ReadAllText(Path.Combine(projectDir, "config.json")));
        var latestNames = latestConfig?.Commands.Select(c => c.Name).ToHashSet() ?? new();
        var missing = selected.Where(c => !latestNames.Contains(c.Name)).Select(c => c.Name).ToList();
        if (missing.Count > 0)
        {
            Header("Error");
            Console.WriteLine("  The following commands no longer exist in config.json:\n");
            foreach (var m in missing) Console.WriteLine($"    {m}");
            Console.WriteLine("\n  Reload config and try again.");
            Pause(); continue;
        }
    }

    // --- Execute ---
    Header("Running");
    var success = true;
    int completed = 0;
    for (int i = 0; i < selected.Count; i++)
    {
        var cmd = selected[i];
        Console.WriteLine($"  [{i + 1}/{selected.Count}] {C.White}{cmd.Name}{C.Reset}");
        Console.WriteLine($"  {C.Gray}> {cmd.Cmd}{C.Reset}");
        Console.WriteLine();
        if (!RunCommand(cmd.Cmd, cmd.Dir, cmd.Shell))
        {
            Console.WriteLine($"  {C.Gray}Error: {cmd.Name} failed. Aborting.{C.Reset}");
            success = false;
            break;
        }
        completed++;
        Console.WriteLine(ProgressBar(completed, selected.Count));
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

// --- Load config ---

static (Config? config, List<Workflow> workflows, string? lastWorkflow, string workflowsPath, string lastPath)
    LoadConfig(string projectDir)
{
    var jsonPath      = Path.Combine(projectDir, "config.json");
    var tsvPath       = Path.Combine(projectDir, "config.tsv");
    var workflowsPath = Path.Combine(projectDir, "workflows.json");
    var lastPath      = Path.Combine(projectDir, ".last");

    Config? config = null;
    if (File.Exists(jsonPath))
        config = JsonSerializer.Deserialize<Config>(File.ReadAllText(jsonPath));
    else if (File.Exists(tsvPath))
        config = ParseTsv(tsvPath);

    if (config == null || config.Commands.Count == 0)
    {
        Console.WriteLine("Failed to load config. Add config.json or config.tsv.");
        Pause();
        return (null, new(), null, "", "");
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
        Console.WriteLine("  Some workflows reference commands not found in config:\n");
        foreach (var w in warnings) Console.WriteLine(w);
        Console.WriteLine("\n  Check config or edit the affected workflows.");
        Pause();
    }

    return (config, workflows, lastWorkflow, workflowsPath, lastPath);
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

// --- Confirm dialog ---

static bool ConfirmDialog(List<Command> selected)
{
    int btn = 0; // 0 = Run, 1 = Cancel
    while (true)
    {
        Header("Confirm");
        for (int i = 0; i < selected.Count; i++)
        {
            var c = selected[i];
            var group = string.IsNullOrEmpty(c.Group) ? "" : $"  {C.GroupTag}[{c.Group}]{C.Reset}";
            Console.WriteLine($"  {C.Gray}{i + 1,2}.{C.Reset}  {c.Name}{group}");
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

// East-Asian terminals render box-drawing chars as double-width (2 columns each)
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

static void PanelHint(string hint)
{
    Console.WriteLine(HLine());
    Console.WriteLine($"  {C.Gray}{hint}{C.Reset}");
}

// --- Header ---

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
                var (cmd, origIdx) = filtered[i];
                var selOrder = selectedIdx.IndexOf(origIdx);
                var check = selOrder >= 0 ? $"{C.SelNum}[{selOrder + 1}]{C.Reset}" : $"{C.Gray}[ ]{C.Reset}";
                var group = string.IsNullOrEmpty(cmd.Group) ? "" : $"  {C.GroupTag}[{cmd.Group}]{C.Reset}";
                if (i == cursor)
                    Console.WriteLine($"  {C.CursorBg}  {check} {cmd.Name}{group}  {C.Reset}");
                else
                    Console.WriteLine($"    {check} {cmd.Name}{group}");
            }
            if (viewEnd < filtered.Count) Console.WriteLine($"  {C.Gray}...{C.Reset}");
        }

        var selectedNames = selectedIdx.Select(i => items[i].Name).ToList();

        Console.WriteLine($"\n  {C.Green}Selected({selectedIdx.Count}){C.Reset}: {string.Join(", ", selectedNames)}");
        PanelHint("↑↓ Space Enter Esc  |  g: group filter");

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
                return selectedIdx.Select(i => items[i]).ToList();
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
        if (viewStart > 0) Console.WriteLine($"  {C.Gray}...{C.Reset}");
        var viewEnd = Math.Min(viewStart + viewHeight, items.Count);
        for (int i = viewStart; i < viewEnd; i++)
        {
            var check = selectedIdx.Contains(i) ? $"{C.SelNum}[x]{C.Reset}" : $"{C.Gray}[ ]{C.Reset}";
            if (i == cursor)
                Console.WriteLine($"  {C.CursorBg}  {check} {label(items[i])}  {C.Reset}");
            else
                Console.WriteLine($"    {check} {label(items[i])}");
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
                if (i == cursor)
                    Console.WriteLine($"  {C.CursorBg}  {label(filtered[i].item)}  {C.Reset}");
                else
                    Console.WriteLine($"    {label(filtered[i].item)}");
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
            if (i == cursor)
                Console.WriteLine($"  {C.CursorBg}  {items[i]}  {C.Reset}");
            else
                Console.WriteLine($"    {items[i]}");
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
}
