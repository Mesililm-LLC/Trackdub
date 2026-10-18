using Avalonia;
using DubBench;
using DubBench.Services;
using Trackdub.Infrastructure.Persistence.Repositories;
using Trackdub.Infrastructure.Persistence.Sqlite;
using Trackdub.Infrastructure.Settings;

namespace DubBench.DevHost;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
    {
        var database = new SqliteUserBenchmarkDatabase(new TrackdubStoragePaths().UserDataRoot);
        var history = new BenchmarkEvidenceRepository(database);
        var onnxHistory = new UserBenchmarkRepository(database);
        var runner = new BenchmarkRunnerService(history, onnxHistory: onnxHistory);
        var builder = AppBuilder.Configure(() => new App(runner, history))
            .UsePlatformDetect()
            .LogToTrace();
#if DEBUG
        builder = builder.WithDeveloperTools();
#endif
        return builder;
    }
}
