using System.Text.RegularExpressions;
using static Shrun.Tui;

namespace Shrun;

static class ListSystem
{
    public static Dictionary<string, List<ListEntry>> LoadListsFromDir(string listsDir)
    {
        var result = new Dictionary<string, List<ListEntry>>();
        if (!Directory.Exists(listsDir)) return result;
        foreach (var file in Directory.GetFiles(listsDir, "*.tsv").OrderBy(f => f))
        {
            var listName = Path.GetFileNameWithoutExtension(file);
            string rawText;
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs))
                rawText = sr.ReadToEnd();
            var entries = rawText.Split(["\r\n", "\n"], StringSplitOptions.None)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l =>
                {
                    var parts = l.Split('\t', 2);
                    var value = parts[0].Trim();
                    var label = parts.Length > 1 ? parts[1].Trim() : null;
                    return new ListEntry(value, string.IsNullOrEmpty(label) ? null : label);
                })
                .Where(e => !string.IsNullOrEmpty(e.Value))
                .ToList();
            if (entries.Count > 0) result[listName] = entries;
        }
        return result;
    }

    public static void SaveList(string listsDir, string listName, List<ListEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(listsDir);
            var lines = entries.Select(e => string.IsNullOrEmpty(e.Label) ? e.Value : $"{e.Value}\t{e.Label}");
            File.WriteAllLines(Path.Combine(listsDir, $"{listName}.tsv"), lines);
        }
        catch (Exception ex) { Console.WriteLine($"\n  Failed to save list: {ex.Message}"); Pause(); }
    }

    public static bool HasPlaceholders(Command cmd) =>
        C.SlotPattern.IsMatch(cmd.Cmd) || C.SlotPattern.IsMatch(cmd.Dir ?? "");

    // Returns unique slot names (for alias/backward-compat use)
    public static List<string> ExtractAllSlotNames(Command cmd)
    {
        var names = new HashSet<string>();
        foreach (Match m in C.SlotPattern.Matches(cmd.Cmd))       names.Add(m.Groups[1].Value);
        foreach (Match m in C.SlotPattern.Matches(cmd.Dir ?? "")) names.Add(m.Groups[1].Value);
        return names.ToList();
    }

    // Apply a varName→value map to command text (used for workflow stored vars and aliases)
    public static Command ApplyVarValues(Command cmd, Dictionary<string, string> varValues)
    {
        if (varValues.Count == 0) return cmd;
        var resolvedCmd = cmd.Cmd;
        var resolvedDir = cmd.Dir;
        foreach (var (k, v) in varValues)
        {
            resolvedCmd = resolvedCmd.Replace($"{{{k}}}", v);
            resolvedDir = resolvedDir?.Replace($"{{{k}}}", v);
        }
        return cmd with { Cmd = resolvedCmd, Dir = resolvedDir };
    }

    // Prompt for variable values defined in cmd.Vars; returns null if cancelled
    public static Dictionary<string, string>? PromptVarValues(
        Command cmd, Dictionary<string, List<ListEntry>> lists,
        List<string>? contextNames = null, int contextIndex = -1, List<string?>? contextNotes = null)
    {
        var result = new Dictionary<string, string>();
        foreach (var (varName, listName) in cmd.Vars ?? new())
        {
            var entries = lists.TryGetValue(listName, out var e) ? e : new List<ListEntry>();
            var value = PromptList(varName, entries, contextNames, contextIndex, contextNotes, cmd, result);
            if (value == null) return null;
            result[varName] = value;
        }
        return result;
    }

    // Prompt for each remaining {slotName} — once per unique name, applied to all occurrences
    public static Command? ResolveListSelections(
        Command cmd, Dictionary<string, List<ListEntry>> lists,
        List<string>? contextNames = null, int contextIndex = -1, List<string?>? contextNotes = null)
    {
        var allSlotNames = C.SlotPattern.Matches(cmd.Cmd).Select(m => m.Groups[1].Value)
            .Concat(C.SlotPattern.Matches(cmd.Dir ?? "").Select(m => m.Groups[1].Value))
            .Distinct().ToList();

        if (allSlotNames.Count == 0) return cmd;

        var selections = new Dictionary<string, string>();
        foreach (var slotName in allSlotNames)
        {
            var entries = lists.TryGetValue(slotName, out var e) ? e : new List<ListEntry>();
            var value = PromptList(slotName, entries, contextNames, contextIndex, contextNotes, cmd, selections);
            if (value == null) return null;
            selections[slotName] = value;
        }

        var resolvedCmd = C.SlotPattern.Replace(cmd.Cmd, m => selections[m.Groups[1].Value]);
        var resolvedDir = cmd.Dir != null ? C.SlotPattern.Replace(cmd.Dir, m => selections[m.Groups[1].Value]) : null;
        return cmd with { Cmd = resolvedCmd, Dir = resolvedDir };
    }

    // Show TUI selector for a slot (variable or list); returns chosen value or null (Esc)
    public static string? PromptList(string slotName, List<ListEntry> entries,
        List<string>? contextNames = null, int contextIndex = -1, List<string?>? contextNotes = null,
        Command? currentCmd = null, Dictionary<string, string>? resolvedValues = null)
    {
        int cursor = 0;
        string search = "";

        // Apply already-resolved values, then highlight the current slot
        string Highlight(string text)
        {
            if (resolvedValues != null)
                foreach (var (k, v) in resolvedValues)
                    text = text.Replace($"{{{k}}}", v);
            return C.SlotPattern.Replace(text, m =>
                m.Groups[1].Value == slotName
                    ? $"{C.Cyan}{m.Value}{C.Reset}"
                    : m.Value);
        }

        // Only show the line(s) that contain the slot currently being selected
        bool showCmd = currentCmd != null && !string.IsNullOrEmpty(currentCmd.Cmd) &&
            C.SlotPattern.Matches(currentCmd.Cmd).Any(m => m.Groups[1].Value == slotName);
        bool showDir = currentCmd != null && !string.IsNullOrEmpty(currentCmd.Dir) &&
            C.SlotPattern.Matches(currentCmd.Dir ?? "").Any(m => m.Groups[1].Value == slotName);
        // Fallback: show cmd if slot not found in either line
        if (currentCmd != null && !showCmd && !showDir && !string.IsNullOrEmpty(currentCmd.Cmd))
            showCmd = true;

        int NoteLines(int i) =>
            contextNotes != null && i < contextNotes.Count && !string.IsNullOrEmpty(contextNotes[i])
                ? contextNotes[i]!.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length : 0;
        int extraLines = (showCmd ? 1 : 0) + (showDir ? 1 : 0) +
            (contextNames == null ? 0 : Enumerable.Range(0, contextNames.Count)
                .Where(i => i != contextIndex).Sum(NoteLines));

        void RenderContext()
        {
            for (int i = 0; i < contextNames!.Count; i++)
            {
                Console.WriteLine(i == contextIndex
                    ? $"  {C.CursorBg} {i + 1,2}. {contextNames[i]}{C.Reset}"
                    : $"  {C.Gray}  {i + 1,2}. {contextNames[i]}{C.Reset}");

                if (i == contextIndex && currentCmd != null)
                {
                    // Current: partially-resolved cmd/dir with current slot highlighted
                    if (showCmd)
                        Console.WriteLine($"       {C.Gray}$ {Highlight(currentCmd!.Cmd)}{C.Reset}");
                    if (showDir)
                        Console.WriteLine($"         {C.Gray}dir: {Highlight(currentCmd!.Dir!)}{C.Reset}");
                }
                else
                {
                    // Completed/pending: show resolved preview from note
                    var noteText = contextNotes != null && i < contextNotes.Count ? contextNotes[i] : null;
                    if (!string.IsNullOrEmpty(noteText))
                        foreach (var line in noteText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                            Console.WriteLine($"       {C.Gray}{line}{C.Reset}");
                }
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

            Header($"Select: {slotName}");
            if (contextNames != null) RenderContext();

            Console.WriteLine($"  {C.Cyan}/{C.Reset} {search}{C.Dim}_\x1b[0m");
            Console.WriteLine();

            int contextLines = (contextNames != null ? contextNames.Count + 2 + extraLines : 0) + 2;
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
                        Header($"Select: {slotName}");
                        if (contextNames != null) RenderContext();
                        Console.WriteLine("  Esc: back\n");
                        var typed = ReadInput($"  {slotName} > ", search);
                        if (typed == null) break; // Esc → back to list
                        return typed;
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

    // TUI for editing list entries (value + optional label)
    public static List<ListEntry> EditListEntries(string listName, List<ListEntry> existing)
    {
        var entries = new List<ListEntry>(existing);
        int cursor = 0;

        while (true)
        {
            var total = entries.Count + 1;
            Header($"List: {listName}");
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
                        Header($"Add value: {listName}");
                        Console.WriteLine("  Esc: cancel\n");
                        var val = ReadInput("  Value > ");
                        if (string.IsNullOrEmpty(val)) break;
                        Console.WriteLine($"  {C.Gray}Label (Enter to skip){C.Reset}");
                        var lbl = ReadOptionalInput("  Label > ");
                        entries.Add(new ListEntry(val, string.IsNullOrEmpty(lbl) ? null : lbl));
                        cursor = entries.Count - 1;
                    }
                    break;
                case ConsoleKey.Escape:
                    return entries;
            }
        }
    }

    // Review and optionally re-edit variable values before saving a workflow or alias
    public static void ReviewWorkflowValues(
        List<Command> cmdSelected,
        Dictionary<string, Dictionary<string, string>> workflowVars,
        Dictionary<string, List<ListEntry>> lists)
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
        int btn = 0;
        bool listMode = false;

        while (true)
        {
            Header("Confirm variables");
            Console.WriteLine($"  {C.Gray}Review the variable values to be saved.{C.Reset}\n");

            if (listMode)
            {
                string? lastCmdName = null;
                for (int i = 0; i < rows.Count; i++)
                {
                    var (cmdIdx, cmdName, varName) = rows[i];
                    var value = workflowVars[cmdName][varName];
                    var prefix = cmdName != lastCmdName ? $"{cmdIdx + 1}. {cmdName}" : "";
                    lastCmdName = cmdName;
                    var paddedPrefix = prefix.PadRight(labelWidth);
                    var valueStr = $"{varName} = {value}";
                    Console.WriteLine(i == cursor
                        ? $"  {C.Cyan}>{C.Reset} {paddedPrefix}  {C.White}{valueStr}{C.Reset}"
                        : $"    {C.Gray}{paddedPrefix}  {valueStr}{C.Reset}");
                }
            }
            else
            {
                for (int ci = 0; ci < cmdSelected.Count; ci++)
                {
                    var cmd = cmdSelected[ci];
                    if (!workflowVars.TryGetValue(cmd.Name, out var cmdVars)) continue;
                    var resolved = ApplyVarValues(cmd, cmdVars);
                    Console.WriteLine($"  {C.Gray}  {ci + 1}. {cmd.Name}{C.Reset}");
                    Console.WriteLine($"       {C.Gray}$ {resolved.Cmd}{C.Reset}");
                    if (!string.IsNullOrEmpty(resolved.Dir))
                        Console.WriteLine($"         {C.Dim}dir: {resolved.Dir}{C.Reset}");
                }
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
                        var selCmd = cmdSelected.FirstOrDefault(c => c.Name == selCmdName);
                        var listName = selCmd?.Vars?.TryGetValue(selVarName, out var ln) == true ? ln : selVarName;
                        var entries = lists.TryGetValue(listName, out var ch) ? ch : new List<ListEntry>();
                        var newValue = PromptList(selVarName, entries, cmdNames, selCmdIdx, currentCmd: selCmd);
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
}
