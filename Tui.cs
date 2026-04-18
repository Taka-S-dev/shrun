using System.Globalization;

namespace Shrun;

static class Tui
{
    public static int DetectBoxCharCols()
    {
        var lang = CultureInfo.CurrentCulture.TwoLetterISOLanguageName;
        return (lang == "ja" || lang == "zh" || lang == "ko") ? 2 : 1;
    }

    public static string HLine()
    {
        var w = Math.Max(0, (Console.WindowWidth - 4) / C.BoxCharCols);
        return C.Gray + "  " + new string('─', w) + C.Reset;
    }

    public static string HLineLabel(string label)
    {
        var w = Math.Max(0, (Console.WindowWidth - 4 - label.Length - 4) / C.BoxCharCols);
        return $"{C.Gray}  ── {label} {new string('─', w)}{C.Reset}";
    }

    public static void PanelHint(string hint)
    {
        Console.WriteLine(HLine());
        Console.WriteLine($"  {C.Gray}{hint}{C.Reset}");
    }

    public static void Header(string title)
    {
        Console.Clear();
        Console.WriteLine($"  {C.Cyan}{C.Bold}[ {title} ]{C.Reset}");
        Console.WriteLine(HLine());
        Console.WriteLine();
    }

    public static string ProgressBar(int done, int total, int width = 24)
    {
        int filled = total > 0 ? (int)((double)done / total * width) : 0;
        int pct    = total > 0 ? (int)((double)done / total * 100)   : 0;
        var bar    = $"{C.Green}{new string('#', filled)}{C.Gray}{new string('-', width - filled)}{C.Reset}";
        return $"  [{bar}]  {C.White}{done}/{total}{C.Reset}  {C.Gray}{pct}%{C.Reset}";
    }

    public static string? ReadInput(string prompt, string prefill = "")
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
    public static string ReadOptionalInput(string prompt)
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

    public static void Pause()
    {
        Console.WriteLine("\n  Press Enter to continue...");
        Console.ReadLine();
    }
}
