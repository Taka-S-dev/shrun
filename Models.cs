using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Shrun;

record Command(
    [property: JsonPropertyName("name")]  string Name,
    [property: JsonPropertyName("group")] string? Group,
    [property: JsonPropertyName("dir")]   string? Dir,
    [property: JsonPropertyName("cmd")]   string Cmd,
    [property: JsonPropertyName("shell")] string? Shell,
    [property: JsonPropertyName("vars")]  Dictionary<string, string>? Vars = null
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

// A single entry in a lists/*.tsv file: value + optional label
record ListEntry(string Value, string? Label = null);

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

    public static readonly Regex SlotPattern = new Regex(@"\{(\w+)\}", RegexOptions.Compiled);
}
