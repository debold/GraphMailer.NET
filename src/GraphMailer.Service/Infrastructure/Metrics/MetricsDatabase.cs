using Microsoft.Data.Sqlite;

namespace GraphMailer.Service.Infrastructure.Metrics;

/// <summary>
/// Connection strings for <c>metrics.db</c>, shared by the service and the ConfigTool.
/// </summary>
/// <remarks>
/// Built with <see cref="SqliteConnectionStringBuilder"/> rather than string interpolation:
/// a semicolon is legal in a Windows path, and an interpolated <c>Data Source=…</c> would cut the
/// path there and fail to parse the rest as a keyword. The data directory can be moved with
/// <c>GRAPHMAILER_DATA_DIR</c> or <c>Metrics:BasePath</c>, so the path is not under our control.
/// </remarks>
internal static class MetricsDatabase
{
    /// <summary>Connection string for <paramref name="dbPath"/>; created on first write.</summary>
    /// <param name="readOnly">
    /// Opens read-only (statistics pages, reports) — never creates or modifies the file.
    /// </param>
    internal static string ConnectionString(string dbPath, bool readOnly = false) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
        }.ToString();
}
