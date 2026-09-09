namespace _3DS.Core;

public static class AppPaths
{
    public static string ApplicationDirectory => Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    public static string GetFilePath(string fileName) => Path.Combine(ApplicationDirectory, fileName);
}
