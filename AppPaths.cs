namespace AGLPTZ;

internal static class AppPaths
{
    private static readonly (string Root, string? Notice) Paths = Resolve();
    internal static string DataRoot => Paths.Root;
    internal static string? Notice => Paths.Notice;

    private static (string, string?) Resolve()
    {
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AGL PTZ Controller");
        var exe = AppContext.BaseDirectory;
        if (!File.Exists(Path.Combine(exe, "portable.flag"))) return (local, null);

        var data = Path.Combine(exe, "Data");
        try
        {
            Directory.CreateDirectory(data);
            using var probe = new FileStream(Path.Combine(data, Guid.NewGuid() + ".tmp"), FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            return (data, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return (local, "Нет доступа к папке Data. Данные хранятся в LocalAppData.");
        }
    }
}
