namespace Tasklabe.App.Services;

/// <summary>障害調査用のログ。%LOCALAPPDATA%\Tasklabe\logs\tasklabe.log に追記する。</summary>
public static class AppLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly Lock Gate = new();

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tasklabe", "logs", "tasklabe.log");

    public static void Error(string context, Exception exception) => Write("ERROR", $"{context}: {exception}");

    public static void Info(string message) => Write("INFO", message);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (File.Exists(Path) && new FileInfo(Path).Length > MaxBytes)
                {
                    File.Move(Path, Path + ".1", overwrite: true);
                }

                File.AppendAllText(Path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // ログの失敗でアプリを止めない
        }
    }
}
