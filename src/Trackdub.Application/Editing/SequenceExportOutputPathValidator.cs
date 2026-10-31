using Trackdub.Contracts;
using Trackdub.Contracts.Editing;

namespace Trackdub.Application.Editing;

internal static class SequenceExportOutputPathValidator
{
    public static void Validate(SequenceExportPlan plan, string outputPath)
    {
        ValidateContainerExtension(plan.Container, outputPath);
        ValidatePlacementInputs(plan, outputPath);
        ValidateAuxiliaryInputs(plan, outputPath);
    }

    private static void ValidateContainerExtension(ExportOutputContainer container, string outputPath)
    {
        string expectedExtension = container switch
        {
            ExportOutputContainer.Mp4 => ".mp4",
            ExportOutputContainer.Mkv => ".mkv",
            _ => throw new ArgumentOutOfRangeException(nameof(container))
        };
        if (!string.Equals(Path.GetExtension(outputPath), expectedExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The output path extension must match the selected {container} container.",
                nameof(outputPath));
        }
    }

    private static void ValidatePlacementInputs(SequenceExportPlan plan, string outputPath)
    {
        foreach (SequenceRenderPlacementAssets assets in plan.PlacementAssets.Values)
        {
            EnsureDifferentPath(outputPath, assets.SourceMediaPath,
                "Sequence output path must be different from every source input.");
            if (plan.AudioMode is SequenceExportAudioMode.Dubbed)
            {
                EnsureDifferentPath(outputPath, assets.AudioPath,
                    "Sequence output path must be different from every audio input.");
            }
        }
    }

    private static void ValidateAuxiliaryInputs(SequenceExportPlan plan, string outputPath)
    {
        EnsureDifferentPath(outputPath, plan.BurnInSubtitlePath,
            "Sequence output path must be different from the subtitle input.");
        EnsureDifferentPath(outputPath, plan.WatermarkFontPath,
            "Sequence output path must be different from the watermark font.");
    }

    private static void EnsureDifferentPath(string outputPath, string? inputPath, string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            return;
        }

        // Compare resolved filesystem identity, not just normalized strings: an input that is
        // a symlink (or lives under a symlinked directory) can resolve to the requested output,
        // and publishing over it would destroy the source media.
        if (FilePathComparison.AreSame(ResolveRealPath(outputPath), ResolveRealPath(inputPath)))
        {
            throw new InvalidOperationException(errorMessage);
        }
    }

    private static string ResolveRealPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath) ?? string.Empty;
        string[] segments = fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        string current = root;
        foreach (string segment in segments)
        {
            current = ResolveLink(Path.Join(current, segment));
        }

        return current;
    }

    private static string ResolveLink(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (info.Exists && info.LinkTarget is not null &&
                info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                return Path.GetFullPath(target.FullName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unresolvable link falls back to its normalized path; the string comparison
            // still rejects the obvious same-path case.
        }

        return path;
    }
}
