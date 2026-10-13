using System.IO;

namespace NSW.WPF.Services;

public static class ProcessExtensions
{
    public static bool SuppressOpening { get; set; }

    public static void OpenFolder(this string folder)
    {
        if (SuppressOpening)
            return;
        if (Directory.Exists(folder))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true
            });
        }
    }
}
