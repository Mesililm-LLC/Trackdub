using Trackdub.Domain.Transcript;

namespace Trackdub.Application.Transcripts;

public sealed class SegmentEditingService(
    ITranscriptRepository transcriptRepository,
    ITtsTakeRepository ttsTakeRepository,
    TranscriptArtifactWriter artifactWriter)
{
    private readonly ITranscriptRepository transcriptRepository = transcriptRepository ?? throw new ArgumentNullException(nameof(transcriptRepository));
    private readonly ITtsTakeRepository ttsTakeRepository = ttsTakeRepository ?? throw new ArgumentNullException(nameof(ttsTakeRepository));
    private readonly TranscriptArtifactWriter artifactWriter = artifactWriter ?? throw new ArgumentNullException(nameof(artifactWriter));

    public Task SaveEditsAsync(
        TranscriptProjectState currentState,
        SaveTranscriptEditsRequest request,
        CancellationToken cancellationToken)
    {
        TranscriptRevision currentRevision = TranscriptWorkflowUtilities.GetRequiredTranscriptRevision(currentState);
        TranscriptWorkflowUtilities.EnsureRevisionMatches(
            currentRevision,
            request.TranscriptRevisionId,
            "Transcript edits were based on an out-of-date revision.");

        Dictionary<Guid, EditedTranscriptSegment> replacements = request.Segments.ToDictionary(
            segment => segment.SegmentId,
            segment => segment,
            comparer: EqualityComparer<Guid>.Default);

        TranscriptSegment[] editedSegments = currentState.TranscriptSegments
            .OrderBy(segment => segment.SegmentIndex)
            .Select((segment, index) =>
            {
                replacements.TryGetValue(segment.Id, out EditedTranscriptSegment? replacement);
                string text = replacement?.Text ?? segment.Text;
                Guid? speakerId = replacement is null ? segment.SpeakerId : replacement.SpeakerId;
                return TranscriptSegment.Create(
                    currentRevision.Id,
                    index,
                    segment.StartSeconds,
                    segment.EndSeconds,
                    text,
                    speakerId,
                    segment.DetectedLanguage,
                    TranscriptWorkflowUtilities.ShouldPreserveWords(segment, text)
                        ? TranscriptWorkflowUtilities.CloneWords(segment.Words)
                        : [],
                    speakerId != segment.SpeakerId
                        ? SpeakerAssignmentOrigin.Manual
                        : segment.SpeakerAssignmentOrigin);
            })
            .ToArray();

        return SaveTranscriptRevisionAsync(currentState, editedSegments, "manual-edit", cancellationToken);
    }

    public async Task SplitSegmentAsync(
        TranscriptProjectState currentState,
        SplitTranscriptSegmentRequest request,
        CancellationToken cancellationToken)
    {
        TranscriptRevision currentRevision = TranscriptWorkflowUtilities.GetRequiredTranscriptRevision(currentState);
        TranscriptWorkflowUtilities.EnsureRevisionMatches(
            currentRevision,
            request.TranscriptRevisionId,
            "Segment split was based on an out-of-date transcript revision.");

        TranscriptSegment[] existingSegments = currentState.TranscriptSegments
            .OrderBy(segment => segment.SegmentIndex)
            .ToArray();
        int targetIndex = Array.FindIndex(existingSegments, segment => segment.Id == request.SegmentId);
        if (targetIndex < 0)
        {
            throw new InvalidOperationException("The selected segment was not found in the current transcript revision.");
        }

        TranscriptSegment targetSegment = existingSegments[targetIndex];
        if (!double.IsFinite(request.SplitSeconds) ||
            request.SplitSeconds <= targetSegment.StartSeconds ||
            request.SplitSeconds >= targetSegment.EndSeconds)
        {
            throw new InvalidOperationException("Split time must fall inside the selected segment.");
        }

        (string leftText, string rightText) = TranscriptWorkflowUtilities.SplitSegmentText(targetSegment.Text);
        var revisedSegments = new List<TranscriptSegment>(existingSegments.Length + 1);
        int revisedIndex = 0;
        foreach (TranscriptSegment segment in existingSegments)
        {
            if (segment.Id != targetSegment.Id)
            {
                revisedSegments.Add(TranscriptSegment.Create(
                    currentRevision.Id,
                    revisedIndex++,
                    segment.StartSeconds,
                    segment.EndSeconds,
                    segment.Text,
                    segment.SpeakerId,
                    segment.DetectedLanguage,
                    TranscriptWorkflowUtilities.CloneWords(segment.Words),
                    segment.SpeakerAssignmentOrigin));
                continue;
            }

            revisedSegments.Add(TranscriptSegment.Create(
                currentRevision.Id,
                revisedIndex++,
                segment.StartSeconds,
                request.SplitSeconds,
                leftText,
                segment.SpeakerId,
                segment.DetectedLanguage,
                TranscriptWorkflowUtilities.CloneWordsInRange(segment.Words, segment.StartSeconds, request.SplitSeconds),
                segment.SpeakerAssignmentOrigin));
            revisedSegments.Add(TranscriptSegment.Create(
                currentRevision.Id,
                revisedIndex++,
                request.SplitSeconds,
                segment.EndSeconds,
                rightText,
                segment.SpeakerId,
                segment.DetectedLanguage,
                TranscriptWorkflowUtilities.CloneWordsInRange(segment.Words, request.SplitSeconds, segment.EndSeconds),
                segment.SpeakerAssignmentOrigin));
        }

        await SaveTranscriptRevisionAsync(currentState, revisedSegments, "segment-split", cancellationToken).ConfigureAwait(false);
        await MarkTtsTakesFromSegmentIndexStaleAsync(
            currentState.ProjectState.Project.Id,
            targetSegment.SegmentIndex,
            cancellationToken).ConfigureAwait(false);
    }

    public Task MergeSegmentsAsync(
        TranscriptProjectState currentState,
        MergeTranscriptSegmentsRequest request,
        CancellationToken cancellationToken) =>
        MergeSegmentRunAsync(
            currentState,
            new MergeTranscriptSegmentRunRequest(
                request.TranscriptRevisionId,
                [request.FirstSegmentId, request.SecondSegmentId]),
            cancellationToken);

    public async Task MergeSegmentRunAsync(
        TranscriptProjectState currentState,
        MergeTranscriptSegmentRunRequest request,
        CancellationToken cancellationToken)
    {
        TranscriptRevision currentRevision = TranscriptWorkflowUtilities.GetRequiredTranscriptRevision(currentState);
        TranscriptWorkflowUtilities.EnsureRevisionMatches(
            currentRevision,
            request.TranscriptRevisionId,
            "Segment merge was based on an out-of-date transcript revision.");

        TranscriptSegment[] existingSegments = currentState.TranscriptSegments
            .OrderBy(segment => segment.SegmentIndex)
            .ToArray();

        Guid[] requestedSegmentIds = request.SegmentIds.Distinct().ToArray();
        if (requestedSegmentIds.Length < 2)
        {
            throw new InvalidOperationException("Select at least two adjacent segments to merge.");
        }

        HashSet<Guid> requestedIds = requestedSegmentIds.ToHashSet();
        TranscriptSegment[] selectedSegments = existingSegments
            .Where(segment => requestedIds.Contains(segment.Id))
            .OrderBy(segment => segment.SegmentIndex)
            .ToArray();
        if (selectedSegments.Length != requestedSegmentIds.Length)
        {
            throw new InvalidOperationException("One or more selected segments were not found in the current transcript revision.");
        }

        for (int index = 1; index < selectedSegments.Length; index++)
        {
            if (selectedSegments[index].SegmentIndex - selectedSegments[index - 1].SegmentIndex != 1)
            {
                throw new InvalidOperationException("Only adjacent transcript segments can be merged.");
            }
        }

        Guid? mergedSpeakerId = selectedSegments[0].SpeakerId;
        if (mergedSpeakerId is null || selectedSegments.Any(segment => segment.SpeakerId != mergedSpeakerId))
        {
            throw new InvalidOperationException("Only adjacent segments from the same assigned speaker can be merged.");
        }

        TranscriptSegment firstSelected = selectedSegments[0];
        TranscriptSegment lastSelected = selectedSegments[^1];
        string mergedText = selectedSegments
            .Select(segment => segment.Text)
            .Aggregate(TranscriptWorkflowUtilities.MergeSegmentText);
        string? mergedDetectedLanguage = selectedSegments
            .Select(segment => segment.DetectedLanguage)
            .Aggregate(TranscriptWorkflowUtilities.MergeDetectedLanguage);
        SpeakerAssignmentOrigin mergedOrigin = selectedSegments.Any(static segment =>
            segment.SpeakerAssignmentOrigin == SpeakerAssignmentOrigin.Manual)
            ? SpeakerAssignmentOrigin.Manual
            : selectedSegments.Any(static segment => segment.SpeakerAssignmentOrigin == SpeakerAssignmentOrigin.Unknown)
                ? SpeakerAssignmentOrigin.Unknown
                : SpeakerAssignmentOrigin.Automatic;
        var revisedSegments = new List<TranscriptSegment>(existingSegments.Length - selectedSegments.Length + 1);
        int revisedIndex = 0;
        foreach (TranscriptSegment segment in existingSegments)
        {
            if (segment.Id == firstSelected.Id)
            {
                revisedSegments.Add(TranscriptSegment.Create(
                    currentRevision.Id,
                    revisedIndex++,
                    firstSelected.StartSeconds,
                    lastSelected.EndSeconds,
                    mergedText,
                    mergedSpeakerId,
                    mergedDetectedLanguage,
                    TranscriptWorkflowUtilities.CloneMergedWords(selectedSegments),
                    mergedOrigin));
                continue;
            }

            if (requestedIds.Contains(segment.Id))
            {
                continue;
            }

            revisedSegments.Add(TranscriptSegment.Create(
                currentRevision.Id,
                revisedIndex++,
                segment.StartSeconds,
                segment.EndSeconds,
                segment.Text,
                segment.SpeakerId,
                segment.DetectedLanguage,
                TranscriptWorkflowUtilities.CloneWords(segment.Words),
                segment.SpeakerAssignmentOrigin));
        }

        await SaveTranscriptRevisionAsync(currentState, revisedSegments, "segment-merge", cancellationToken).ConfigureAwait(false);
        await MarkTtsTakesFromSegmentIndexStaleAsync(
            currentState.ProjectState.Project.Id,
            firstSelected.SegmentIndex,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task TrimSegmentAsync(
        TranscriptProjectState currentState,
        TrimTranscriptSegmentRequest request,
        CancellationToken cancellationToken)
    {
        TranscriptRevision currentRevision = TranscriptWorkflowUtilities.GetRequiredTranscriptRevision(currentState);
        TranscriptWorkflowUtilities.EnsureRevisionMatches(
            currentRevision,
            request.TranscriptRevisionId,
            "Segment trim was based on an out-of-date transcript revision.");

        if (!double.IsFinite(request.StartSeconds) ||
            !double.IsFinite(request.EndSeconds) ||
            request.StartSeconds < 0 ||
            request.EndSeconds < request.StartSeconds)
        {
            throw new InvalidOperationException("Trim start and end times must be finite, non-negative, and ordered.");
        }

        TranscriptSegment[] existingSegments = currentState.TranscriptSegments
            .OrderBy(segment => segment.SegmentIndex)
            .ToArray();
        int targetIndex = Array.FindIndex(existingSegments, segment => segment.Id == request.SegmentId);
        if (targetIndex < 0)
        {
            throw new InvalidOperationException("The selected segment was not found in the current transcript revision.");
        }

        TranscriptSegment targetSegment = existingSegments[targetIndex];
        double previousEnd = targetIndex == 0 ? 0d : existingSegments[targetIndex - 1].EndSeconds;
        double nextStart = targetIndex == existingSegments.Length - 1 ? double.PositiveInfinity : existingSegments[targetIndex + 1].StartSeconds;
        if (request.StartSeconds < previousEnd || request.EndSeconds > nextStart)
        {
            throw new InvalidOperationException("Trimmed segment timing would overlap an adjacent segment.");
        }

        // Replacement segments are re-indexed below when inserted into the full revised transcript.
        TranscriptSegment[] replacementSegments = BuildRetimedSegments(currentRevision.Id, targetSegment, request.StartSeconds, request.EndSeconds);
        var revisedSegments = new List<TranscriptSegment>(existingSegments.Length + replacementSegments.Length - 1);
        int revisedIndex = 0;
        foreach (TranscriptSegment segment in existingSegments)
        {
            bool isTarget = segment.Id == targetSegment.Id;
            if (isTarget)
            {
                foreach (TranscriptSegment replacementSegment in replacementSegments)
                {
                    revisedSegments.Add(TranscriptSegment.Create(
                        currentRevision.Id,
                        revisedIndex++,
                        replacementSegment.StartSeconds,
                        replacementSegment.EndSeconds,
                        replacementSegment.Text,
                        replacementSegment.SpeakerId,
                        replacementSegment.DetectedLanguage,
                        TranscriptWorkflowUtilities.CloneWords(replacementSegment.Words),
                        replacementSegment.SpeakerAssignmentOrigin));
                }

                continue;
            }

            revisedSegments.Add(TranscriptSegment.Create(
                currentRevision.Id,
                revisedIndex++,
                segment.StartSeconds,
                segment.EndSeconds,
                segment.Text,
                segment.SpeakerId,
                segment.DetectedLanguage,
                TranscriptWorkflowUtilities.CloneWords(segment.Words),
                segment.SpeakerAssignmentOrigin));
        }

        await SaveTranscriptRevisionAsync(currentState, revisedSegments, "segment-trim", cancellationToken).ConfigureAwait(false);
        if (replacementSegments.Length == 1)
        {
            await MarkTtsTakesAtSegmentIndexStaleAsync(
                currentState.ProjectState.Project.Id,
                targetSegment.SegmentIndex,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await MarkTtsTakesFromSegmentIndexStaleAsync(
                currentState.ProjectState.Project.Id,
                targetSegment.SegmentIndex,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DeleteSegmentAsync(
        TranscriptProjectState currentState,
        DeleteTranscriptSegmentRequest request,
        CancellationToken cancellationToken)
    {
        TranscriptRevision currentRevision = TranscriptWorkflowUtilities.GetRequiredTranscriptRevision(currentState);
        TranscriptWorkflowUtilities.EnsureRevisionMatches(
            currentRevision,
            request.TranscriptRevisionId,
            "Segment delete was based on an out-of-date transcript revision.");

        TranscriptSegment[] existingSegments = currentState.TranscriptSegments
            .OrderBy(segment => segment.SegmentIndex)
            .ToArray();
        int targetIndex = Array.FindIndex(existingSegments, segment => segment.Id == request.SegmentId);
        if (targetIndex < 0)
        {
            throw new InvalidOperationException("The selected segment was not found in the current transcript revision.");
        }

        if (existingSegments.Length == 1)
        {
            throw new InvalidOperationException("Cannot delete the only remaining segment.");
        }

        TranscriptSegment targetSegment = existingSegments[targetIndex];
        var revisedSegments = new List<TranscriptSegment>(existingSegments.Length - 1);
        int revisedIndex = 0;
        foreach (TranscriptSegment segment in existingSegments)
        {
            if (segment.Id == request.SegmentId)
            {
                continue;
            }

            revisedSegments.Add(TranscriptSegment.Create(
                currentRevision.Id,
                revisedIndex++,
                segment.StartSeconds,
                segment.EndSeconds,
                segment.Text,
                segment.SpeakerId,
                segment.DetectedLanguage,
                TranscriptWorkflowUtilities.CloneWords(segment.Words),
                segment.SpeakerAssignmentOrigin));
        }

        await SaveTranscriptRevisionAsync(currentState, revisedSegments, "segment-delete", cancellationToken).ConfigureAwait(false);
        await MarkTtsTakesFromSegmentIndexStaleAsync(
            currentState.ProjectState.Project.Id,
            targetSegment.SegmentIndex,
            cancellationToken).ConfigureAwait(false);
    }

    private static TranscriptSegment[] BuildRetimedSegments(
        Guid transcriptRevisionId,
        TranscriptSegment targetSegment,
        double requestedStartSeconds,
        double requestedEndSeconds)
    {
        const double Epsilon = 0.0001d;
        var parts = new List<SegmentTimingPart>(3);
        if (requestedStartSeconds > targetSegment.StartSeconds + Epsilon)
        {
            parts.Add(new SegmentTimingPart(targetSegment.StartSeconds, requestedStartSeconds));
        }

        parts.Add(new SegmentTimingPart(requestedStartSeconds, requestedEndSeconds));

        if (requestedEndSeconds < targetSegment.EndSeconds - Epsilon)
        {
            parts.Add(new SegmentTimingPart(requestedEndSeconds, targetSegment.EndSeconds));
        }

        string[] partTexts = BuildRetimedSegmentTexts(targetSegment, parts);
        return parts
            .Select((part, index) => TranscriptSegment.Create(
                transcriptRevisionId,
                index,
                part.StartSeconds,
                part.EndSeconds,
                partTexts[index],
                targetSegment.SpeakerId,
                targetSegment.DetectedLanguage,
                TranscriptWorkflowUtilities.CloneWordsInRange(
                    targetSegment.Words,
                    part.StartSeconds,
                    part.EndSeconds),
                targetSegment.SpeakerAssignmentOrigin))
            .ToArray();
    }

    private static string[] BuildRetimedSegmentTexts(
        TranscriptSegment targetSegment,
        IReadOnlyList<SegmentTimingPart> parts)
    {
        string trimmedText = targetSegment.Text.Trim();
        if (parts.Count == 1)
        {
            return [trimmedText];
        }

        IReadOnlyList<TranscriptWord>[] wordGroups = parts
            .Select(part => TranscriptWorkflowUtilities.CloneWordsInRange(
                targetSegment.Words,
                part.StartSeconds,
                part.EndSeconds))
            .ToArray();
        if (targetSegment.Words.Count > 0 && wordGroups.All(group => group.Count > 0))
        {
            return wordGroups
                .Select(group => string.Join(' ', group.Select(word => word.Text)).Trim())
                .Select(text => string.IsNullOrWhiteSpace(text) ? trimmedText : text)
                .ToArray();
        }

        return SplitTextByPartDurations(
            trimmedText,
            parts,
            targetSegment.StartSeconds,
            targetSegment.EndSeconds);
    }

    private static string[] SplitTextByPartDurations(
        string text,
        IReadOnlyList<SegmentTimingPart> parts,
        double originalStartSeconds,
        double originalEndSeconds)
    {
        string trimmed = text.Trim();
        if (parts.Count == 1)
        {
            return [trimmed];
        }

        string[] words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length >= parts.Count)
        {
            return SplitWordsByPartDurations(words, parts);
        }

        if (trimmed.Length >= parts.Count)
        {
            return SplitCharactersByPartDurations(trimmed, parts, originalStartSeconds, originalEndSeconds);
        }

        return Enumerable.Repeat(trimmed, parts.Count).ToArray();
    }

    private static string[] SplitWordsByPartDurations(
        IReadOnlyList<string> words,
        IReadOnlyList<SegmentTimingPart> parts)
    {
        double totalDuration = parts.Sum(static part => Math.Max(0d, part.EndSeconds - part.StartSeconds));
        if (totalDuration <= 0d)
        {
            totalDuration = parts.Count;
        }

        var results = new string[parts.Count];
        int wordIndex = 0;
        double accumulatedDuration = 0d;
        for (int index = 0; index < parts.Count; index++)
        {
            int remainingParts = parts.Count - index;
            int remainingWords = words.Count - wordIndex;
            int count;
            if (index == parts.Count - 1)
            {
                count = remainingWords;
            }
            else
            {
                accumulatedDuration += Math.Max(0d, parts[index].EndSeconds - parts[index].StartSeconds);
                int desiredEnd = (int)Math.Round(accumulatedDuration / totalDuration * words.Count);
                count = Math.Clamp(
                    desiredEnd - wordIndex,
                    1,
                    remainingWords - (remainingParts - 1));
            }

            results[index] = string.Join(' ', words.Skip(wordIndex).Take(count));
            wordIndex += count;
        }

        return results;
    }

    private static string[] SplitCharactersByPartDurations(
        string text,
        IReadOnlyList<SegmentTimingPart> parts,
        double originalStartSeconds,
        double originalEndSeconds)
    {
        double totalDuration = Math.Max(0d, originalEndSeconds - originalStartSeconds);
        if (totalDuration <= 0d)
        {
            totalDuration = parts.Count;
        }

        var results = new string[parts.Count];
        int charIndex = 0;
        for (int index = 0; index < parts.Count; index++)
        {
            int remainingParts = parts.Count - index;
            int remainingChars = text.Length - charIndex;
            int count;
            if (index == parts.Count - 1)
            {
                count = remainingChars;
            }
            else
            {
                double partEndOffset = Math.Max(0d, parts[index].EndSeconds - originalStartSeconds);
                int desiredEnd = (int)Math.Round(partEndOffset / totalDuration * text.Length);
                count = Math.Clamp(
                    desiredEnd - charIndex,
                    1,
                    remainingChars - (remainingParts - 1));
            }

            results[index] = text.Substring(charIndex, count).Trim();
            if (results[index].Length == 0)
            {
                results[index] = FindNearestCharacterSlice(text, charIndex, count);
            }

            charIndex += count;
        }

        return results;
    }

    private static string FindNearestCharacterSlice(string text, int startIndex, int count)
    {
        int endIndex = Math.Min(text.Length, startIndex + count);
        for (int index = startIndex; index < endIndex; index++)
        {
            if (!char.IsWhiteSpace(text[index]))
            {
                return text[index].ToString();
            }
        }

        for (int index = endIndex; index < text.Length; index++)
        {
            if (!char.IsWhiteSpace(text[index]))
            {
                return text[index].ToString();
            }
        }

        for (int index = startIndex - 1; index >= 0; index--)
        {
            if (!char.IsWhiteSpace(text[index]))
            {
                return text[index].ToString();
            }
        }

        return ".";
    }

    private async Task MarkTtsTakesFromSegmentIndexStaleAsync(
        Guid projectId,
        int firstSegmentIndex,
        CancellationToken cancellationToken) =>
        await MarkTtsTakesStaleAsync(
            projectId,
            segmentIndex => segmentIndex >= firstSegmentIndex,
            cancellationToken).ConfigureAwait(false);

    private async Task MarkTtsTakesAtSegmentIndexStaleAsync(
        Guid projectId,
        int segmentIndex,
        CancellationToken cancellationToken) =>
        await MarkTtsTakesStaleAsync(
            projectId,
            takeSegmentIndex => takeSegmentIndex == segmentIndex,
            cancellationToken).ConfigureAwait(false);

    private async Task MarkTtsTakesStaleAsync(
        Guid projectId,
        Func<int, bool> shouldMarkSegmentIndex,
        CancellationToken cancellationToken)
    {
        var takes = await ttsTakeRepository
            .GetByProjectAsync(projectId, cancellationToken)
            .ConfigureAwait(false);
        foreach (var take in takes.Where(take => shouldMarkSegmentIndex(take.SegmentIndex) && !take.IsStale))
        {
            await ttsTakeRepository.SaveAsync(take.MarkStale(), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SaveTranscriptRevisionAsync(
        TranscriptProjectState currentState,
        IReadOnlyList<TranscriptSegment> segments,
        string provenance,
        CancellationToken cancellationToken)
    {
        int nextRevisionNumber = await transcriptRepository.GetNextRevisionNumberAsync(
            currentState.ProjectState.Project.Id,
            cancellationToken).ConfigureAwait(false);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        TranscriptRevision editedRevision = TranscriptRevision.Create(
            currentState.ProjectState.Project.Id,
            stageRunId: null,
            nextRevisionNumber,
            now);

        TranscriptSegment[] revisedSegments = segments
            .OrderBy(segment => segment.SegmentIndex)
            .Select((segment, index) => TranscriptSegment.Create(
                editedRevision.Id,
                index,
                segment.StartSeconds,
                segment.EndSeconds,
                segment.Text,
                segment.SpeakerId,
                segment.DetectedLanguage,
                TranscriptWorkflowUtilities.CloneWords(segment.Words),
                segment.SpeakerAssignmentOrigin))
            .ToArray();

        await transcriptRepository.SaveRevisionAsync(editedRevision, revisedSegments, cancellationToken).ConfigureAwait(false);
        await artifactWriter.WriteTranscriptArtifactAsync(
            currentState.ProjectState.Project.Id,
            TranscriptWorkflowUtilities.GetRequiredMediaAsset(currentState),
            editedRevision,
            revisedSegments,
            stageRunId: null,
            provenance,
            cancellationToken).ConfigureAwait(false);
    }

    private sealed record SegmentTimingPart(double StartSeconds, double EndSeconds);
}
