namespace AGLPTZ;

internal static class UiAssets
{
    private static readonly Dictionary<string, string> Types = new(StringComparer.Ordinal)
    {
        ["index.html"] = "text/html; charset=utf-8",
        ["phone.html"] = "text/html; charset=utf-8",
        ["phone.js"] = "text/javascript; charset=utf-8",
        ["phone.css"] = "text/css; charset=utf-8",
        ["app.js"] = "text/javascript; charset=utf-8",
        ["app.css"] = "text/css; charset=utf-8",
        ["surface.css"] = "text/css; charset=utf-8",
        ["app-icon.png"] = "image/png",
    };

    internal static Stream? Open(string name, out string? contentType) =>
        Types.TryGetValue(name, out contentType)
            ? typeof(UiAssets).Assembly.GetManifestResourceStream("AGLPTZ.UI." + name)
            : null;
}
