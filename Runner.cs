using System.Diagnostics;

namespace Shrun;

static class Runner
{
    private static volatile Process? _current;

    static Runner()
    {
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // prevent host process from being killed immediately
            _current?.Kill(entireProcessTree: true);
        };
    }

    public static bool RunCommand(string cmd, string? workDir, string? shell = null)
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
            _current = proc;
            proc.WaitForExit();
            _current = null;
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  {C.Gray}Error: {ex.Message}{C.Reset}");
            return false;
        }
    }
}
