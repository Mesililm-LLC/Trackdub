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
        if (!string.IsNullOrWhiteSpace(inputPath) &&
            FilePathComparison.AreSame(outputPath, Path.GetFullPath(inputPath)))
        {
            throw new InvalidOperationException(errorMessage);
        }
    }
}
