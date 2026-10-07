namespace Tasklabe.App.Services;

/// <summary>既定のブラウザーで URL を開く（Windows の起動の仕組み Launcher を使う）。</summary>
public static class Browser
{
    public static void Open(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            Open(uri);
        }
    }

    public static void Open(Uri uri) => _ = Windows.System.Launcher.LaunchUriAsync(uri);
}
