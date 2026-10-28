using Trackdub.Contracts.Transcripts;
using Trackdub.Domain.Speakers;

namespace Trackdub.TestDoubles;

public sealed class FakeSpeakerRepository : ISpeakerRepository
{
    private readonly List<ProjectSpeaker> speakers = [];
    private readonly List<SpeakerTurn> turns = [];
    private readonly HashSet<Guid> segmentSpeakerIds = [];
    private readonly HashSet<Guid> voiceAssignmentSpeakerIds = [];
    private readonly Dictionary<Guid, Guid> mergedTargets = [];

    public IReadOnlyList<ProjectSpeaker> Speakers => speakers;
    public IReadOnlyList<SpeakerTurn> Turns => turns;

    /// <summary>Seed speakers and turns without going through <see cref="ReplaceDiarizationAsync"/>.</summary>
    public void Seed(IEnumerable<ProjectSpeaker> seedSpeakers, IEnumerable<SpeakerTurn>? seedTurns = null)
    {
        speakers.AddRange(seedSpeakers);
        if (seedTurns is not null)
        {
            turns.AddRange(seedTurns);
        }
    }

    /// <summary>
    /// Mirrors the real repository's delete guards: speakers referenced by transcript segments
    /// or voice assignments, archived as a merge source, or named as another speaker's merge
    /// target cannot be removed.
    /// </summary>
    public void SeedReferences(
        IEnumerable<Guid>? segmentSpeakerIds = null,
        IEnumerable<Guid>? voiceAssignmentSpeakerIds = null,
        IReadOnlyDictionary<Guid, Guid>? mergedSpeakerTargets = null)
    {
        if (segmentSpeakerIds is not null)
        {
            this.segmentSpeakerIds.UnionWith(segmentSpeakerIds);
        }

        if (voiceAssignmentSpeakerIds is not null)
        {
            this.voiceAssignmentSpeakerIds.UnionWith(voiceAssignmentSpeakerIds);
        }

        if (mergedSpeakerTargets is not null)
        {
            foreach ((Guid source, Guid target) in mergedSpeakerTargets)
            {
                mergedTargets[source] = target;
            }
        }
    }

    public Task<IReadOnlyList<ProjectSpeaker>> ListSpeakersAsync(Guid projectId, CancellationToken cancellationToken)
    {
        IReadOnlyList<ProjectSpeaker> result = speakers
            .Where(s => s.ProjectId == projectId && !mergedTargets.ContainsKey(s.Id))
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<MergedSpeakerEntry>> ListMergedSpeakersAsync(Guid projectId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MergedSpeakerEntry>>(speakers
            .Where(s => s.ProjectId == projectId && mergedTargets.ContainsKey(s.Id))
            .Select(s => new MergedSpeakerEntry(s, ResolveActiveTarget(mergedTargets[s.Id])))
            .ToList());

    private Guid ResolveActiveTarget(Guid speakerId)
    {
        Guid resolved = speakerId;
        for (int hop = 0; mergedTargets.TryGetValue(resolved, out Guid target) && hop < mergedTargets.Count; hop++)
        {
            resolved = target;
        }

        return resolved;
    }

    public Task DeleteEmptySpeakerAsync(Guid projectId, Guid speakerId, CancellationToken cancellationToken)
    {
        if (mergedTargets.ContainsKey(speakerId))
        {
            throw new InvalidOperationException("Merged speakers are retained as history and cannot be deleted.");
        }

        if (turns.Exists(t => t.ProjectId == projectId && t.SpeakerId == speakerId) ||
            segmentSpeakerIds.Contains(speakerId) ||
            voiceAssignmentSpeakerIds.Contains(speakerId) ||
            mergedTargets.ContainsValue(speakerId))
        {
            throw new InvalidOperationException("Only speakers with no turns, segments, voice assignments, or archived merge history can be removed.");
        }

        if (speakers.RemoveAll(s => s.ProjectId == projectId && s.Id == speakerId) == 0)
        {
            throw new InvalidOperationException("The selected speaker was not found.");
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SpeakerTurn>> ListTurnsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        IReadOnlyList<SpeakerTurn> result = turns
            .Where(t => t.ProjectId == projectId)
            .OrderBy(t => t.StartSeconds)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<ProjectSpeaker> EnsureDefaultSpeakerAsync(Guid projectId, CancellationToken cancellationToken)
    {
        ProjectSpeaker? existing = speakers.FirstOrDefault(s => s.ProjectId == projectId);
        if (existing is not null)
        {
            return Task.FromResult(existing);
        }

        ProjectSpeaker speaker = ProjectSpeaker.Create(projectId, "Speaker 1", DateTimeOffset.UtcNow);
        speakers.Add(speaker);
        return Task.FromResult(speaker);
    }

    public Task<ProjectSpeaker> CreateSpeakerAsync(Guid projectId, CancellationToken cancellationToken)
    {
        ProjectSpeaker speaker = ProjectSpeaker.Create(
            projectId,
            BuildNextSpeakerDisplayName(projectId),
            DateTimeOffset.UtcNow.AddMilliseconds(speakers.Count(s => s.ProjectId == projectId)));
        speakers.Add(speaker);
        return Task.FromResult(speaker);
    }

    public Task ReplaceDiarizationAsync(
        Guid projectId,
        IReadOnlyList<ProjectSpeaker> newSpeakers,
        IReadOnlyList<SpeakerTurn> newTurns,
        CancellationToken cancellationToken)
    {
        bool preserveExistingSpeakers = speakers.Any(s => s.ProjectId == projectId);
        if (!preserveExistingSpeakers)
        {
            speakers.RemoveAll(s => s.ProjectId == projectId);
        }

        turns.RemoveAll(t => t.ProjectId == projectId);
        speakers.AddRange(newSpeakers);
        turns.AddRange(newTurns);
        return Task.CompletedTask;
    }

    public Task RenameSpeakerAsync(
        Guid projectId,
        Guid speakerId,
        string displayName,
        CancellationToken cancellationToken)
    {
        int index = speakers.FindIndex(s => s.ProjectId == projectId && s.Id == speakerId);
        if (index >= 0)
        {
            speakers[index] = speakers[index].Rename(displayName);
        }

        return Task.CompletedTask;
    }

    public Task SplitTurnAsync(
        Guid projectId,
        Guid speakerTurnId,
        double splitSeconds,
        CancellationToken cancellationToken)
    {
        int index = turns.FindIndex(t => t.ProjectId == projectId && t.Id == speakerTurnId);
        if (index < 0)
        {
            return Task.CompletedTask;
        }

        SpeakerTurn original = turns[index];
        if (splitSeconds <= original.StartSeconds || splitSeconds >= original.EndSeconds)
        {
            return Task.CompletedTask;
        }

        SpeakerTurn first = SpeakerTurn.Create(
            projectId, original.SpeakerId,
            original.StartSeconds, splitSeconds,
            original.Confidence, original.HasOverlap, original.StageRunId);

        SpeakerTurn second = SpeakerTurn.Create(
            projectId, original.SpeakerId,
            splitSeconds, original.EndSeconds,
            original.Confidence, original.HasOverlap, original.StageRunId);

        turns.RemoveAt(index);
        turns.Insert(index, second);
        turns.Insert(index, first);
        return Task.CompletedTask;
    }

    private string BuildNextSpeakerDisplayName(Guid projectId)
    {
        const string prefix = "Speaker ";
        HashSet<string> names = speakers
            .Where(s => s.ProjectId == projectId)
            .Select(s => s.DisplayName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        int nextNumber = names
            .Select(name => name.Trim())
            .Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(name => int.TryParse(name[prefix.Length..], out int number) ? number : 0)
            .DefaultIfEmpty(0)
            .Max() + 1;

        string candidate;
        do
        {
            candidate = $"{prefix}{nextNumber++}";
        }
        while (names.Contains(candidate));

        return candidate;
    }
}
