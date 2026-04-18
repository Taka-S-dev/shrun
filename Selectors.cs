using static Shrun.Tui;
using static Shrun.VarSystem;

namespace Shrun;

static class Selectors
{
    // --- Command select with real-time search + scroll ---

    public static List<Command> MultiSelect(string prompt, List<Command> items, HashSet<string>? preSelected = null)
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

    public static List<RunItem> MultiSelectWithAliases(string prompt, List<Command> commands, List<Alias> aliases)
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

    public static List<T> MultiSelectGeneric<T>(string prompt, List<T> items, Func<T, string> label)
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

    public static T? SingleSelect<T>(string prompt, List<T> items, Func<T, string> label, int initialCursor = 0)
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

    public static string? SingleSelectStr(string prompt, List<string> items, bool isMain = false, string? subtitle = null)
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

    // --- Confirm dialog ---

    public static bool ConfirmDialog(List<RunItem> items)
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
}
