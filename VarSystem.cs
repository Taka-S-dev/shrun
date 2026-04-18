using System.Text.RegularExpressions;
using static Shrun.Tui;

namespace Shrun;

static class VarSystem
{
    public static Dictionary<string, List<VarEntry>> LoadVarsFromDir(string varsDir)
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

    public static void SaveVar(string varsDir, string varName, List<VarEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(varsDir);
            var lines = entries.Select(e => string.IsNullOrEmpty(e.Label) ? e.Value : $"{e.Value}\t{e.Label}");
            File.WriteAllLines(Path.Combine(varsDir, $"{varName}.tsv"), lines);
        }
        catch (Exception ex) { Console.WriteLine($"\n  Failed to save var: {ex.Message}"); Pause(); }
    }

    public static List<string> ExtractVarNames(Command cmd)
    {
        var names = new HashSet<string>();
        foreach (Match m in C.VarPattern.Matches(cmd.Cmd))       names.Add(m.Groups[1].Value);
        foreach (Match m in C.VarPattern.Matches(cmd.Dir ?? "")) names.Add(m.Groups[1].Value);
        return names.ToList();
    }

    public static Command ApplyVars(Command cmd, Dictionary<string, string> varMap)
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
    public static string? PromptVar(string varName, List<VarEntry> entries,
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

            if (cmdList != null) RenderCmdContext();

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
    public static List<VarEntry> EditVarEntries(string varName, List<VarEntry> existing)
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
    public static void ReviewWorkflowVars(
        List<Command> cmdSelected,
        Dictionary<string, Dictionary<string, string>> workflowVars,
        Dictionary<string, List<VarEntry>> vars)
    {
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
    public static void ReviewRunVars(Dictionary<string, string> varMap, Dictionary<string, List<VarEntry>> vars)
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
}
