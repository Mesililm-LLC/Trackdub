namespace Trackdub.Infrastructure.Persistence.Migrations;

internal static class SqliteMigrations
{
    public static IReadOnlyList<SqliteMigration> All { get; } =
    [
        new(
            1,
            "create-project-spine",
            """
            CREATE TABLE IF NOT EXISTS Projects (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                RootPath TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS MediaAssets (
                Id TEXT PRIMARY KEY,
                ProjectId TEXT NOT NULL,
                OriginalPath TEXT NOT NULL,
                ContentHash TEXT NOT NULL,
                Kind TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                FOREIGN KEY (ProjectId) REFERENCES Projects(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS StageRuns (
                Id TEXT PRIMARY KEY,
                ProjectId TEXT NOT NULL,
                StageName TEXT NOT NULL,
                Status TEXT NOT NULL,
                StartedAtUtc TEXT NOT NULL,
                CompletedAtUtc TEXT NULL,
                FailureReason TEXT NULL,
                FOREIGN KEY (ProjectId) REFERENCES Projects(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS Artifacts (
                Id TEXT PRIMARY KEY,
                ProjectId TEXT NOT NULL,
                StageRunId TEXT NULL,
                Kind TEXT NOT NULL,
                RelativePath TEXT NOT NULL,
                ContentHash TEXT NOT NULL,
                Provenance TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                FOREIGN KEY (ProjectId) REFERENCES Projects(Id) ON DELETE CASCADE,
                FOREIGN KEY (StageRunId) REFERENCES StageRuns(Id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS ModelCache (
                ModelId TEXT PRIMARY KEY,
                RootPath TEXT NOT NULL,
                Revision TEXT NOT NULL,
                Sha256 TEXT NOT NULL,
                CachedAtUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS BenchmarkRuns (
                Id TEXT PRIMARY KEY,
                ModelId TEXT NOT NULL,
                ModelPath TEXT NOT NULL,
                ReportPath TEXT NOT NULL,
                Status TEXT NOT NULL,
                RequestedProvider TEXT NOT NULL,
                SelectedProvider TEXT NOT NULL,
                RunCount INTEGER NOT NULL,
                SupportsExecution INTEGER NOT NULL,
                ModelSizeBytes INTEGER NOT NULL,
                ColdLoadMilliseconds REAL NULL,
                WarmLatencyAverageMilliseconds REAL NULL,
                WarmLatencyMinimumMilliseconds REAL NULL,
                WarmLatencyMaximumMilliseconds REAL NULL,
                FailureReason TEXT NULL,
                GeneratedAtUtc TEXT NOT NULL
            );
            """),
        new(
            2,
            "create-transcript-and-export-spine",
            """
            CREATE TABLE IF NOT EXISTS Speakers (
                Id TEXT PRIMARY KEY,
                ProjectId TEXT NOT NULL,
                DisplayName TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                FOREIGN KEY (ProjectId) REFERENCES Projects(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS SpeakerTurns (
                Id TEXT PRIMARY KEY,
                ProjectId TEXT NOT NULL,
                SpeakerId TEXT NOT NULL,
                StageRunId TEXT NULL,
                StartSeconds REAL NOT NULL,
                EndSeconds REAL NOT NULL,
                FOREIGN KEY (ProjectId) REFERENCES Projects(Id) ON DELETE CASCADE,
                FOREIGN KEY (SpeakerId) REFERENCES Speakers(Id) ON DELETE CASCADE,
                FOREIGN KEY (StageRunId) REFERENCES StageRuns(Id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS TranscriptRevisions (
                Id TEXT PRIMARY KEY,
                ProjectId TEXT NOT NULL,
                StageRunId TEXT NULL,
                RevisionNumber INTEGER NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                FOREIGN KEY (ProjectId) REFERENCES Projects(Id) ON DELETE CASCADE,
                FOREIGN KEY (StageRunId) REFERENCES StageRuns(Id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS TranscriptSegments (
                Id TEXT PRIMARY KEY,
                TranscriptRevisionId TEXT NOT NULL,
                SpeakerId TEXT NULL,
                SegmentIndex INTEGER NOT NULL,
                StartSeconds REAL NOT NULL,
                EndSeconds REAL NOT NULL,
                Text TEXT NOT NULL,
                FOREIGN KEY (TranscriptRevisionId) REFERENCES TranscriptRevisions(Id) ON DELETE CASCADE,
                FOREIGN KEY (SpeakerId) REFERENCES Speakers(Id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS Words (
                Id TEXT PRIMARY KEY,
                TranscriptSegmentId TEXT NOT NULL,
                WordIndex INTEGER NOT NULL,
                StartSeconds REAL NOT NULL,
                EndSeconds REAL NOT NULL,
                Text TEXT NOT NULL,
                FOREIGN KEY (TranscriptSegmentId) REFERENCES TranscriptSegments(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS TranslationRevisions (
                Id TEXT PRIMARY KEY,
                ProjectId TEXT NOT NULL,
                StageRunId TEXT NULL,
                SourceTranscriptRevisionId TEXT NULL,
                TargetLanguage TEXT NOT NULL,
                RevisionNumber INTEGER NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                FOREIGN KEY (ProjectId) REFERENCES Projects(Id) ON DELETE CASCADE,
                FOREIGN KEY (StageRunId) REFERENCES StageRuns(Id) ON DELETE SET NULL,
                FOREIGN KEY (SourceTranscriptRevisionId) REFERENCES TranscriptRevisions(Id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS TranslatedSegments (
                Id TEXT PRIMARY KEY,
                TranslationRevisionId TEXT NOT NULL,
                SourceSegmentId TEXT NULL,
                SegmentIndex INTEGER NOT NULL,
                Text TEXT NOT NULL,
                FOREIGN KEY (TranslationRevisionId) REFERENCES TranslationRevisions(Id) ON DELETE CASCADE,
                FOREIGN KEY (SourceSegmentId) REFERENCES TranscriptSegments(Id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS VoiceAssignments (
                Id TEXT PRIMARY KEY,
                ProjectId TEXT NOT NULL,
                SpeakerId TEXT NOT NULL,
                VoiceModelId TEXT NOT NULL,
                VoiceVariant TEXT NULL,
                RequiresConsent INTEGER NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                FOREIGN KEY (ProjectId) REFERENCES Projects(Id) ON DELETE CASCADE,
                FOREIGN KEY (SpeakerId) REFERENCES Speakers(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS TtsTakes (
                Id TEXT PRIMARY KEY,
                ProjectId TEXT NOT NULL,
                VoiceAssignmentId TEXT NOT NULL,
                ArtifactId TEXT NULL,
                StageRunId TEXT NULL,
                Status TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                FOREIGN KEY (ProjectId) REFERENCES Projects(Id) ON DELETE CASCADE,
                FOREIGN KEY (VoiceAssignmentId) REFERENCES VoiceAssignments(Id) ON DELETE CASCADE,
                FOREIGN KEY (ArtifactId) REFERENCES Artifacts(Id) ON DELETE SET NULL,
                FOREIGN KEY (StageRunId) REFERENCES StageRuns(Id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS MixPlans (
                Id TEXT PRIMARY KEY,
                ProjectId TEXT NOT NULL,
                StageRunId TEXT NULL,
                CreatedAtUtc TEXT NOT NULL,
                FOREIGN KEY (ProjectId) REFERENCES Projects(Id) ON DELETE CASCADE,
                FOREIGN KEY (StageRunId) REFERENCES StageRuns(Id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS Exports (
                Id TEXT PRIMARY KEY,
                ProjectId TEXT NOT NULL,
                MixPlanId TEXT NULL,
                ArtifactId TEXT NULL,
                ExportKind TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                FOREIGN KEY (ProjectId) REFERENCES Projects(Id) ON DELETE CASCADE,
                FOREIGN KEY (MixPlanId) REFERENCES MixPlans(Id) ON DELETE SET NULL,
                FOREIGN KEY (ArtifactId) REFERENCES Artifacts(Id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS ConsentRecords (
                Id TEXT PRIMARY KEY,
                ProjectId TEXT NOT NULL,
                SubjectId TEXT NOT NULL,
                ConsentKind TEXT NOT NULL,
                GrantedAtUtc TEXT NOT NULL,
                Notes TEXT NULL,
                FOREIGN KEY (ProjectId) REFERENCES Projects(Id) ON DELETE CASCADE
            );
            """),
        new(
            3,
            "create-core-indexes",
            """
            CREATE INDEX IF NOT EXISTS IX_Artifacts_ProjectId ON Artifacts(ProjectId);
            CREATE INDEX IF NOT EXISTS IX_Artifacts_StageRunId ON Artifacts(StageRunId);
            CREATE INDEX IF NOT EXISTS IX_StageRuns_ProjectId ON StageRuns(ProjectId);
            CREATE INDEX IF NOT EXISTS IX_MediaAssets_ProjectId ON MediaAssets(ProjectId);
            CREATE INDEX IF NOT EXISTS IX_BenchmarkRuns_ModelId ON BenchmarkRuns(ModelId);
            CREATE INDEX IF NOT EXISTS IX_TranscriptRevisions_ProjectId ON TranscriptRevisions(ProjectId);
            CREATE INDEX IF NOT EXISTS IX_TranslationRevisions_ProjectId ON TranslationRevisions(ProjectId);
            CREATE INDEX IF NOT EXISTS IX_ConsentRecords_ProjectId ON ConsentRecords(ProjectId);
            """),
        new(
            4,
            "extend-stage-runs-with-runtime-context",
            """
            ALTER TABLE StageRuns ADD COLUMN RequestedProvider TEXT NULL;
            ALTER TABLE StageRuns ADD COLUMN SelectedProvider TEXT NULL;
            ALTER TABLE StageRuns ADD COLUMN RuntimeModelId TEXT NULL;
            ALTER TABLE StageRuns ADD COLUMN RuntimeModelAlias TEXT NULL;
            ALTER TABLE StageRuns ADD COLUMN RuntimeModelVariant TEXT NULL;
            ALTER TABLE StageRuns ADD COLUMN BootstrapDetail TEXT NULL;
            """),
        new(
            5,
            "tts-columns",
            """
            ALTER TABLE TtsTakes ADD COLUMN TranslatedSegmentId TEXT NULL REFERENCES TranslatedSegments(Id) ON DELETE SET NULL;
            ALTER TABLE TtsTakes ADD COLUMN IsStale INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE TtsTakes ADD COLUMN DurationSamples INTEGER NULL;
            ALTER TABLE TtsTakes ADD COLUMN SampleRate INTEGER NULL;
            ALTER TABLE TtsTakes ADD COLUMN Provider TEXT NULL;
            """),
        new(
            6,
            "tts-m11-metadata",
            """
            ALTER TABLE TtsTakes ADD COLUMN SegmentIndex INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE TtsTakes ADD COLUMN TranslatedTextHash TEXT NULL;
            ALTER TABLE TtsTakes ADD COLUMN ModelId TEXT NULL;
            ALTER TABLE TtsTakes ADD COLUMN VoiceId TEXT NULL;
            ALTER TABLE TtsTakes ADD COLUMN DurationOverrunRatio REAL NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS IX_VoiceAssignments_ProjectId_SpeakerId ON VoiceAssignments(ProjectId, SpeakerId);
            CREATE INDEX IF NOT EXISTS IX_TtsTakes_ProjectId_SegmentIndex ON TtsTakes(ProjectId, SegmentIndex);
            CREATE INDEX IF NOT EXISTS IX_TtsTakes_VoiceAssignmentId_IsStale ON TtsTakes(VoiceAssignmentId, IsStale);
            """),
        new(
            7,
            "transcript-segment-language",
            """
            ALTER TABLE TranscriptSegments ADD COLUMN DetectedLanguage TEXT NULL;
            """),
        new(
            8,
            "voice-assignment-fallback-flag",
            """
            ALTER TABLE VoiceAssignments ADD COLUMN IsFallback INTEGER NOT NULL DEFAULT 0;
            DROP INDEX IF EXISTS IX_VoiceAssignments_ProjectId_SpeakerId;
            CREATE UNIQUE INDEX IF NOT EXISTS IX_VoiceAssignments_ProjectId_SpeakerId_User
                ON VoiceAssignments(ProjectId, SpeakerId)
                WHERE IsFallback = 0;
            """),
        new(
            9,
            "tts-stretch-metadata",
            """
            ALTER TABLE TtsTakes ADD COLUMN PreStretchDurationSeconds REAL NULL;
            ALTER TABLE TtsTakes ADD COLUMN StretchRatioApplied REAL NULL;
            ALTER TABLE TtsTakes ADD COLUMN StretchMode TEXT NOT NULL DEFAULT 'None';
            ALTER TABLE TtsTakes ADD COLUMN StretchEngine TEXT NOT NULL DEFAULT 'None';
            """),
        new(
            10,
            "word-confidence",
            """
            ALTER TABLE Words ADD COLUMN Confidence REAL NULL;
            """),
        new(
            11,
            "voice-cloning-tts-metadata",
            """
            ALTER TABLE VoiceAssignments ADD COLUMN ReferenceClipArtifactId TEXT NULL;
            ALTER TABLE TtsTakes ADD COLUMN Kind TEXT NOT NULL DEFAULT 'Stock';
            ALTER TABLE TtsTakes ADD COLUMN ReferenceClipArtifactId TEXT NULL;
            """),
        new(
            12,
            "add-stage-run-extended-runtime-info",
            """
            ALTER TABLE StageRuns ADD COLUMN DeviceTarget TEXT NULL;
            ALTER TABLE StageRuns ADD COLUMN FallbackReason TEXT NULL;
            ALTER TABLE StageRuns ADD COLUMN SmokeEvidenceId TEXT NULL;
            ALTER TABLE StageRuns ADD COLUMN BenchmarkEvidenceId TEXT NULL;
            """),
        new(
            13,
            "benchmark-measurement-metadata",
            """
            ALTER TABLE BenchmarkRuns ADD COLUMN FirstRunMilliseconds REAL NULL;
            ALTER TABLE BenchmarkRuns ADD COLUMN WarmupMilliseconds REAL NULL;
            ALTER TABLE BenchmarkRuns ADD COLUMN WarmupRunCount INTEGER NULL;
            ALTER TABLE BenchmarkRuns ADD COLUMN SampleCount INTEGER NULL;
            ALTER TABLE BenchmarkRuns ADD COLUMN WarmLatencyP50Milliseconds REAL NULL;
            ALTER TABLE BenchmarkRuns ADD COLUMN WarmLatencyP90Milliseconds REAL NULL;
            ALTER TABLE BenchmarkRuns ADD COLUMN WarmLatencyP95Milliseconds REAL NULL;
            ALTER TABLE BenchmarkRuns ADD COLUMN WarmLatencyP99Milliseconds REAL NULL;
            ALTER TABLE BenchmarkRuns ADD COLUMN MeasuredWallTimeMilliseconds REAL NULL;
            ALTER TABLE BenchmarkRuns ADD COLUMN PassesPerSecond REAL NULL;
            ALTER TABLE BenchmarkRuns ADD COLUMN ThroughputUnitsPerSecond REAL NULL;
            ALTER TABLE BenchmarkRuns ADD COLUMN AudioDurationSeconds REAL NULL;
            ALTER TABLE BenchmarkRuns ADD COLUMN RealTimeFactorAverage REAL NULL;
            ALTER TABLE BenchmarkRuns ADD COLUMN ThroughputUnit TEXT NULL;
            ALTER TABLE BenchmarkRuns ADD COLUMN TimingMode TEXT NULL;
            ALTER TABLE BenchmarkRuns ADD COLUMN InputProfileId TEXT NULL;
            ALTER TABLE BenchmarkRuns ADD COLUMN ComparisonKey TEXT NULL;
            ALTER TABLE BenchmarkRuns ADD COLUMN MetadataJson TEXT NULL;
            CREATE INDEX IF NOT EXISTS IX_BenchmarkRuns_ComparisonKey_GeneratedAtUtc
                ON BenchmarkRuns(ComparisonKey, GeneratedAtUtc);
            """)
    ];
}
