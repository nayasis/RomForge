using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace RomForge.Cli;

internal static class CliConsole
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int handle);

    public static void Initialize()
    {
        // WinExe inherits redirected handles, but needs to attach for an interactive terminal.
        if (GetStdHandle(-11) == IntPtr.Zero || GetStdHandle(-11) == new IntPtr(-1))
            AttachConsole(uint.MaxValue);

        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
    }
}
