namespace Jane.Core.Storage.Migrations;

/// <summary>
/// Every migration Jane has ever shipped, in order.
/// </summary>
/// <remarks>
/// <para>
/// Append only. A migration that has shipped is never edited -- a user's database has already run
/// it, so a change here would only ever apply to fresh installs and the two would silently
/// diverge. Fix a mistake with a new migration.
/// </para>
/// <para>
/// The schema is deliberately plain: no blobs, no shadow tables, no compression, no encryption.
/// The plan promises that history is plaintext and permanent until deleted, and that the UI says
/// so; a schema a person can read with <c>sqlite3 jane.db ".schema"</c> and understand is what
/// makes that promise checkable rather than merely stated.
/// </para>
/// </remarks>
internal static class JaneMigrations
{
    /// <summary>
    /// Version 1 -- settings, dictionary, per-app instructions and history.
    /// </summary>
    /// <remarks>
    /// Timestamps are ISO-8601 in UTC, so lexicographic ordering is chronological ordering and
    /// SQLite needs no date functions to sort. Durations are whole milliseconds: history is read
    /// by a person and aggregated by the eval harness, and neither wants ticks.
    /// </remarks>
    private const string InitialSchema = """
        -- One row per leaf of JaneSettings, keyed by its dotted JSON path, e.g.
        --   speech.numThreads | 4
        --   llm.keepAlive     | "180s"
        -- The value is the JSON representation of that leaf, so a string keeps its quotes and the
        -- round trip back into JaneSettings is exact.
        CREATE TABLE settings (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        -- The custom dictionary. Terms and pronunciation hints become sherpa-onnx contextual
        -- biasing hotwords and prompt-side hints; a non-null replacement additionally disables
        -- the Phase 7 bypass whenever it applies to a transcript.
        -- There is no entry cap. Aqua's 800 is a product limit, not a technical one.
        CREATE TABLE dictionary (
            id                 INTEGER PRIMARY KEY,
            term               TEXT NOT NULL,
            pronunciation_hint TEXT,
            replacement        TEXT,
            enabled            INTEGER NOT NULL DEFAULT 1,
            created_at         TEXT NOT NULL,
            updated_at         TEXT NOT NULL
        );

        CREATE UNIQUE INDEX ux_dictionary_term ON dictionary (term COLLATE NOCASE);

        -- Custom Instructions. process_name NULL is the global rule set that applies everywhere;
        -- any other value is a per-app override keyed on the process name Windows reports,
        -- lower-case and without the extension.
        CREATE TABLE instructions (
            id           INTEGER PRIMARY KEY,
            process_name TEXT,
            text         TEXT NOT NULL,
            enabled      INTEGER NOT NULL DEFAULT 1,
            created_at   TEXT NOT NULL,
            updated_at   TEXT NOT NULL
        );

        -- IFNULL because SQLite lets a unique index hold any number of NULLs, which would allow
        -- several competing global rule sets.
        CREATE UNIQUE INDEX ux_instructions_scope
            ON instructions (IFNULL(process_name, '*') COLLATE NOCASE);

        -- Every dictation, in plaintext, kept until the user deletes it.
        --
        -- Audio is never stored. There is no BLOB column here and there never will be: the
        -- buffer is dropped on every path out of the pipeline, including the failing ones, and
        -- HistoryTests asserts both the schema and the stored values to keep it that way.
        CREATE TABLE history (
            id                  INTEGER PRIMARY KEY,
            created_at          TEXT NOT NULL,
            mode                TEXT NOT NULL,

            raw_transcript      TEXT NOT NULL,
            final_text          TEXT NOT NULL,
            deep_context        TEXT,

            -- The identity re-inject targets. The HWND alone is not identity: Windows recycles
            -- handles, so the process id and name are what make a stale match detectable.
            target_handle       INTEGER NOT NULL,
            target_process_id   INTEGER NOT NULL,
            target_process_name TEXT NOT NULL,
            target_window_class TEXT NOT NULL,
            target_window_title TEXT NOT NULL,

            -- Why the LLM did or did not run. Phase 12 measures the false-bypass rate from this.
            bypassed            INTEGER NOT NULL,
            bypass_reason       TEXT NOT NULL,

            engine_id           TEXT NOT NULL,
            llm_model           TEXT,

            injected            INTEGER NOT NULL,
            injection_failure   TEXT,

            capture_ms          INTEGER NOT NULL,
            vad_ms              INTEGER NOT NULL,
            recognition_ms      INTEGER NOT NULL,
            context_ms          INTEGER NOT NULL,
            formatting_ms       INTEGER NOT NULL,
            injection_ms        INTEGER NOT NULL,
            total_ms            INTEGER NOT NULL
        );

        CREATE INDEX ix_history_created_at ON history (created_at DESC, id DESC);
        CREATE INDEX ix_history_process    ON history (target_process_name COLLATE NOCASE);
        CREATE INDEX ix_history_bypassed   ON history (bypassed);
        """;

    /// <summary>In ascending version order. The runner sorts anyway; this is for readers.</summary>
    public static IReadOnlyList<Migration> All { get; } =
    [
        Migration.Sql(1, "initial-schema", InitialSchema),
    ];

    /// <summary>The version a freshly opened database ends up at.</summary>
    public static int CurrentVersion => All.Max(m => m.Version);
}
