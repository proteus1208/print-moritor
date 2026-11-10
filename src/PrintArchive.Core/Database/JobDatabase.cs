using Microsoft.Data.Sqlite;

namespace PrintArchive.Core;

public sealed class JobDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _sync = new();

    public JobDatabase(string? path = null)
    {
        ArchivePaths.EnsureCreated();
        string dbPath = path ?? ArchivePaths.Database;
        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        Execute("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS Jobs (
                Id TEXT PRIMARY KEY,
                JobId INTEGER,
                EventRecordId INTEGER UNIQUE,
                DocumentName TEXT NOT NULL,
                Username TEXT NOT NULL,
                PrinterName TEXT NOT NULL,
                Timestamp TEXT NOT NULL,
                PageCount INTEGER,
                DataSize INTEGER,
                OriginalFilePath TEXT,
                ArchiveFilePath TEXT,
                ScreenshotPath TEXT,
                CaptureStatus TEXT NOT NULL,
                ClientComputer TEXT,
                UserSid TEXT
            );
            CREATE INDEX IF NOT EXISTS IX_Jobs_Timestamp ON Jobs(Timestamp);
            CREATE INDEX IF NOT EXISTS IX_Jobs_Printer ON Jobs(PrinterName);
            CREATE INDEX IF NOT EXISTS IX_Jobs_Document ON Jobs(DocumentName);
            """);
    }

    public bool ExistsByEventRecordId(long eventRecordId)
    {
        lock (_sync)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM Jobs WHERE EventRecordId = $id LIMIT 1";
            cmd.Parameters.AddWithValue("$id", eventRecordId);
            return cmd.ExecuteScalar() is not null;
        }
    }

    public bool ExistsById(string id)
    {
        lock (_sync)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM Jobs WHERE Id = $id LIMIT 1";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteScalar() is not null;
        }
    }

    public void Upsert(PrintJobRecord record)
    {
        lock (_sync)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Jobs (
                    Id, JobId, EventRecordId, DocumentName, Username, PrinterName, Timestamp,
                    PageCount, DataSize, OriginalFilePath, ArchiveFilePath, ScreenshotPath,
                    CaptureStatus, ClientComputer, UserSid)
                VALUES (
                    $Id, $JobId, $EventRecordId, $DocumentName, $Username, $PrinterName, $Timestamp,
                    $PageCount, $DataSize, $OriginalFilePath, $ArchiveFilePath, $ScreenshotPath,
                    $CaptureStatus, $ClientComputer, $UserSid)
                ON CONFLICT(Id) DO UPDATE SET
                    JobId=excluded.JobId,
                    EventRecordId=excluded.EventRecordId,
                    DocumentName=excluded.DocumentName,
                    Username=excluded.Username,
                    PrinterName=excluded.PrinterName,
                    Timestamp=excluded.Timestamp,
                    PageCount=excluded.PageCount,
                    DataSize=excluded.DataSize,
                    OriginalFilePath=excluded.OriginalFilePath,
                    ArchiveFilePath=excluded.ArchiveFilePath,
                    ScreenshotPath=excluded.ScreenshotPath,
                    CaptureStatus=excluded.CaptureStatus,
                    ClientComputer=excluded.ClientComputer,
                    UserSid=excluded.UserSid;
                """;
            Bind(cmd, record);
            try
            {
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19 && record.EventRecordId is not null)
            {
                // Unique EventRecordId — duplicate event, ignore.
            }
        }
    }

    public List<PrintJobRecord> Query(
        string? search = null,
        string? printer = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null)
    {
        lock (_sync)
        {
            using var cmd = _connection.CreateCommand();
            var where = new List<string>();
            if (!string.IsNullOrWhiteSpace(search))
            {
                where.Add("(DocumentName LIKE $search OR Username LIKE $search)");
                cmd.Parameters.AddWithValue("$search", "%" + search.Trim() + "%");
            }
            if (!string.IsNullOrWhiteSpace(printer) && printer != "(All printers)")
            {
                where.Add("PrinterName = $printer");
                cmd.Parameters.AddWithValue("$printer", printer);
            }
            if (from is not null)
            {
                where.Add("Timestamp >= $from");
                cmd.Parameters.AddWithValue("$from", from.Value.ToString("O"));
            }
            if (to is not null)
            {
                where.Add("Timestamp <= $to");
                cmd.Parameters.AddWithValue("$to", to.Value.ToString("O"));
            }

            cmd.CommandText = "SELECT * FROM Jobs"
                + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
                + " ORDER BY Timestamp DESC";

            var list = new List<PrintJobRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                list.Add(Read(reader));
            return list;
        }
    }

    public PrintJobRecord? GetById(string id)
    {
        lock (_sync)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM Jobs WHERE Id = $id LIMIT 1";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        }
    }

    public List<string> GetPrinters()
    {
        lock (_sync)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT PrinterName FROM Jobs ORDER BY PrinterName";
            var list = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                list.Add(reader.GetString(0));
            return list;
        }
    }

    public PrintJobRecord? GetOldest()
    {
        lock (_sync)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM Jobs ORDER BY Timestamp ASC LIMIT 1";
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        }
    }

    /// <summary>
    /// Jobs that still need a screenshot and/or original-file enrichment from the Agent.
    /// </summary>
    public List<PrintJobRecord> QueryNeedingEnrichment(int limit = 25)
    {
        lock (_sync)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT * FROM Jobs
                WHERE ScreenshotPath IS NULL
                   OR ArchiveFilePath IS NULL
                ORDER BY Timestamp DESC
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$limit", limit);
            var list = new List<PrintJobRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                list.Add(Read(reader));
            return list;
        }
    }


    public void Delete(string id)
    {
        lock (_sync)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Jobs WHERE Id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public int Count()
    {
        lock (_sync)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Jobs";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    private void Execute(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void Bind(SqliteCommand cmd, PrintJobRecord r)
    {
        cmd.Parameters.AddWithValue("$Id", r.Id);
        cmd.Parameters.AddWithValue("$JobId", (object?)r.JobId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$EventRecordId", (object?)r.EventRecordId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$DocumentName", r.DocumentName);
        cmd.Parameters.AddWithValue("$Username", r.Username);
        cmd.Parameters.AddWithValue("$PrinterName", r.PrinterName);
        cmd.Parameters.AddWithValue("$Timestamp", r.Timestamp.ToString("O"));
        cmd.Parameters.AddWithValue("$PageCount", (object?)r.PageCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$DataSize", (object?)r.DataSize ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$OriginalFilePath", (object?)r.OriginalFilePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ArchiveFilePath", (object?)r.ArchiveFilePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ScreenshotPath", (object?)r.ScreenshotPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$CaptureStatus", r.CaptureStatus);
        cmd.Parameters.AddWithValue("$ClientComputer", (object?)r.ClientComputer ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$UserSid", (object?)r.UserSid ?? DBNull.Value);
    }

    private static PrintJobRecord Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("Id")),
        JobId = reader.IsDBNull(reader.GetOrdinal("JobId")) ? null : reader.GetInt32(reader.GetOrdinal("JobId")),
        EventRecordId = reader.IsDBNull(reader.GetOrdinal("EventRecordId")) ? null : reader.GetInt64(reader.GetOrdinal("EventRecordId")),
        DocumentName = reader.GetString(reader.GetOrdinal("DocumentName")),
        Username = reader.GetString(reader.GetOrdinal("Username")),
        PrinterName = reader.GetString(reader.GetOrdinal("PrinterName")),
        Timestamp = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("Timestamp"))),
        PageCount = reader.IsDBNull(reader.GetOrdinal("PageCount")) ? null : reader.GetInt32(reader.GetOrdinal("PageCount")),
        DataSize = reader.IsDBNull(reader.GetOrdinal("DataSize")) ? null : reader.GetInt64(reader.GetOrdinal("DataSize")),
        OriginalFilePath = reader.IsDBNull(reader.GetOrdinal("OriginalFilePath")) ? null : reader.GetString(reader.GetOrdinal("OriginalFilePath")),
        ArchiveFilePath = reader.IsDBNull(reader.GetOrdinal("ArchiveFilePath")) ? null : reader.GetString(reader.GetOrdinal("ArchiveFilePath")),
        ScreenshotPath = reader.IsDBNull(reader.GetOrdinal("ScreenshotPath")) ? null : reader.GetString(reader.GetOrdinal("ScreenshotPath")),
        CaptureStatus = reader.GetString(reader.GetOrdinal("CaptureStatus")),
        ClientComputer = reader.IsDBNull(reader.GetOrdinal("ClientComputer")) ? null : reader.GetString(reader.GetOrdinal("ClientComputer")),
        UserSid = reader.IsDBNull(reader.GetOrdinal("UserSid")) ? null : reader.GetString(reader.GetOrdinal("UserSid")),
    };

    public void Dispose() => _connection.Dispose();
}
