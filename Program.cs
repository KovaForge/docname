using Microsoft.Data.Sqlite;
using System.CommandLine;
using SQLitePCL;

const string Level1 = "KF";
var openClawDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openclaw");
var dbPath = Path.Combine(openClawDirectory, "docname.db");
var codesPath = ResolveCodesPath(openClawDirectory);
Batteries_V2.Init();

var root = new RootCommand("docname, controlled document filename allocator");

var initCommand = new Command("init", "Creates or refreshes the SQLite database and seeds Level2 codes.");
initCommand.SetAction(_ =>
{
    try
    {
        InitializeDatabase(dbPath);
        Console.WriteLine($"Initialized {dbPath}");
        return 0;
    }
    catch (Exception ex)
    {
        return Fail(ex.Message);
    }
});

var listCommand = new Command("list", "Lists all known Level2 codes and their descriptions.");
listCommand.SetAction(_ =>
{
    try
    {
        InitializeDatabase(dbPath);
        using var connection = OpenConnection(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT level2, description FROM codes ORDER BY level2";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            Console.WriteLine($"{reader.GetString(0)}\t{reader.GetString(1)}");
        }

        return 0;
    }
    catch (Exception ex)
    {
        return Fail(ex.Message);
    }
});

var allocL2 = new Argument<string>("L2") { Description = "Level2 code, for example POL." };
var allocL3 = new Argument<string>("L3") { Description = "Level3 code, for example MD." };
var allocText = new Argument<string>("FreeText") { Description = "Document title text." };
var previewOption = new Option<bool>("--preview", "-p") { Description = "Show the filename without allocating or committing to the database." };
var allocCommand = new Command("alloc", "Allocates the next filename for the given Level2 and Level3 codes.")
{
    allocL2,
    allocL3,
    allocText,
    previewOption
};
allocCommand.SetAction(parseResult =>
{
    try
    {
        InitializeDatabase(dbPath);
        var l2 = NormalizeCode(parseResult.GetValue(allocL2), "L2");
        var l3 = NormalizeCode(parseResult.GetValue(allocL3), "L3");
        var freeText = parseResult.GetValue(allocText)?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(freeText))
        {
            return Fail("FreeText cannot be empty.");
        }

        using var connection = OpenConnection(dbPath);
        EnsureLevel2Exists(connection, transaction: null, l2);
        var nextNumber = GetNextNumber(connection, transaction: null, l2, l3, allocate: false);
        var filename = BuildFilename(l2, l3, nextNumber, freeText);

        if (parseResult.GetValue(previewOption))
        {
            Console.WriteLine($"[preview] {filename}");
            return 0;
        }

        using var transaction = connection.BeginTransaction();
        GetNextNumber(connection, transaction, l2, l3, allocate: true);
        transaction.Commit();

        Console.WriteLine(filename);
        return 0;
    }
    catch (Exception ex)
    {
        return Fail(ex.Message);
    }
});

var nextL2 = new Argument<string>("L2") { Description = "Level2 code, for example POL." };
var nextL3 = new Argument<string>("L3") { Description = "Level3 code, for example MD." };
var nextCommand = new Command("next", "Shows the next number for the given Level2 and Level3 codes without allocating it.")
{
    nextL2,
    nextL3
};
nextCommand.SetAction(parseResult =>
{
    try
    {
        InitializeDatabase(dbPath);
        var l2 = NormalizeCode(parseResult.GetValue(nextL2), "L2");
        var l3 = NormalizeCode(parseResult.GetValue(nextL3), "L3");

        using var connection = OpenConnection(dbPath);
        EnsureLevel2Exists(connection, transaction: null, l2);
        var nextNumber = GetNextNumber(connection, transaction: null, l2, l3, allocate: false);

        Console.WriteLine($"Next number for {Level1}-{l2}-{l3}: {nextNumber:000}");
        return 0;
    }
    catch (Exception ex)
    {
        return Fail(ex.Message);
    }
});

root.Subcommands.Add(initCommand);
root.Subcommands.Add(listCommand);
root.Subcommands.Add(allocCommand);
root.Subcommands.Add(nextCommand);

return await root.Parse(args).InvokeAsync();

static int Fail(string message)
{
    Console.Error.WriteLine($"Error: {message}");
    return 1;
}

static SqliteConnection OpenConnection(string dbPath)
{
    var directory = Path.GetDirectoryName(dbPath);
    if (!string.IsNullOrWhiteSpace(directory))
    {
        Directory.CreateDirectory(directory);
    }

    var connection = new SqliteConnection($"Data Source={dbPath}");
    connection.Open();
    return connection;
}

void InitializeDatabase(string dbPath)
{
    using var connection = OpenConnection(dbPath);
    using var command = connection.CreateCommand();
    command.CommandText = @"
CREATE TABLE IF NOT EXISTS counters (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    level1 TEXT NOT NULL DEFAULT 'KF',
    level2 TEXT NOT NULL,
    level3 TEXT NOT NULL,
    last_number INTEGER NOT NULL DEFAULT 0,
    UNIQUE(level1, level2, level3)
);

CREATE TABLE IF NOT EXISTS codes (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    level2 TEXT NOT NULL UNIQUE,
    description TEXT NOT NULL
);
";
    command.ExecuteNonQuery();

    foreach (var code in LoadCodes(codesPath))
    {
        using var seedCommand = connection.CreateCommand();
        seedCommand.CommandText = "INSERT INTO codes (level2, description) VALUES ($level2, $description) ON CONFLICT(level2) DO UPDATE SET description = excluded.description";
        seedCommand.Parameters.AddWithValue("$level2", code.Code);
        seedCommand.Parameters.AddWithValue("$description", code.Description);
        seedCommand.ExecuteNonQuery();
    }
}

static string ResolveCodesPath(string openClawDirectory)
{
    var overridePath = Environment.GetEnvironmentVariable("DOCNAME_CODES_FILE");
    if (!string.IsNullOrWhiteSpace(overridePath))
    {
        return ExpandHome(overridePath);
    }

    var userPath = Path.Combine(openClawDirectory, "docname-codes.tsv");
    if (File.Exists(userPath))
    {
        return userPath;
    }

    var appPath = Path.Combine(AppContext.BaseDirectory, "codes.tsv");
    if (File.Exists(appPath))
    {
        return appPath;
    }

    var workingDirectoryPath = Path.Combine(Environment.CurrentDirectory, "codes.tsv");
    if (File.Exists(workingDirectoryPath))
    {
        return workingDirectoryPath;
    }

    throw new FileNotFoundException(
        "No docname codes file found. Create ~/.openclaw/docname-codes.tsv, set DOCNAME_CODES_FILE, or place codes.tsv beside the docname binary.");
}

static string ExpandHome(string path)
{
    if (path == "~")
    {
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    if (path.StartsWith("~/", StringComparison.Ordinal))
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
    }

    return path;
}

static IReadOnlyList<(string Code, string Description)> LoadCodes(string path)
{
    var codes = new List<(string Code, string Description)>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var rawLine in File.ReadLines(path))
    {
        var line = rawLine.Trim();
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
        {
            continue;
        }

        var parts = line.Split('\t', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
        {
            throw new InvalidOperationException($"Invalid codes file line in {path}: '{rawLine}'. Expected CODE<TAB>Description.");
        }

        var code = NormalizeCode(parts[0], "Level2 code");
        if (!seen.Add(code))
        {
            throw new InvalidOperationException($"Duplicate Level2 code in {path}: {code}");
        }

        codes.Add((code, parts[1].Trim()));
    }

    if (codes.Count == 0)
    {
        throw new InvalidOperationException($"Codes file contains no Level2 codes: {path}");
    }

    return codes;
}

static string NormalizeCode(string? input, string name)
{
    var value = (input ?? string.Empty).Trim().ToUpperInvariant();
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException($"{name} cannot be empty.");
    }

    return value;
}

static void EnsureLevel2Exists(SqliteConnection connection, SqliteTransaction? transaction, string level2)
{
    using var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = "SELECT COUNT(1) FROM codes WHERE level2 = $level2";
    command.Parameters.AddWithValue("$level2", level2);

    var exists = Convert.ToInt32(command.ExecuteScalar()) > 0;
    if (!exists)
    {
        throw new InvalidOperationException($"Unknown Level2 code: {level2}. Run 'docname list' or 'docname init'.");
    }
}

static int GetNextNumber(SqliteConnection connection, SqliteTransaction? transaction, string level2, string level3, bool allocate)
{
    if (allocate)
    {
        using var insertCommand = connection.CreateCommand();
        insertCommand.Transaction = transaction;
        insertCommand.CommandText = @"
INSERT INTO counters (level1, level2, level3, last_number)
VALUES ($level1, $level2, $level3, 0)
ON CONFLICT(level1, level2, level3) DO NOTHING";
        insertCommand.Parameters.AddWithValue("$level1", Level1);
        insertCommand.Parameters.AddWithValue("$level2", level2);
        insertCommand.Parameters.AddWithValue("$level3", level3);
        insertCommand.ExecuteNonQuery();

        using var updateCommand = connection.CreateCommand();
        updateCommand.Transaction = transaction;
        updateCommand.CommandText = @"
UPDATE counters
SET last_number = last_number + 1
WHERE level1 = $level1 AND level2 = $level2 AND level3 = $level3
RETURNING last_number";
        updateCommand.Parameters.AddWithValue("$level1", Level1);
        updateCommand.Parameters.AddWithValue("$level2", level2);
        updateCommand.Parameters.AddWithValue("$level3", level3);

        var result = updateCommand.ExecuteScalar();
        return Convert.ToInt32(result);
    }

    using var selectCommand = connection.CreateCommand();
    selectCommand.Transaction = transaction;
    selectCommand.CommandText = @"
SELECT COALESCE(last_number, 0) + 1
FROM counters
WHERE level1 = $level1 AND level2 = $level2 AND level3 = $level3";
    selectCommand.Parameters.AddWithValue("$level1", Level1);
    selectCommand.Parameters.AddWithValue("$level2", level2);
    selectCommand.Parameters.AddWithValue("$level3", level3);

    var resultObj = selectCommand.ExecuteScalar();
    return resultObj is null || resultObj == DBNull.Value ? 1 : Convert.ToInt32(resultObj);
}

static string BuildFilename(string level2, string level3, int number, string freeText)
{
    var cleaned = SanitizeFreeText(freeText);
    return $"{Level1}-{level2}-{level3}-{number:000}_{cleaned}.md";
}

static string SanitizeFreeText(string freeText)
{
    var chars = freeText.Trim()
        .Select(ch => char.IsWhiteSpace(ch) ? '_' : ch)
        .Where(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-')
        .ToArray();

    var cleaned = new string(chars);
    while (cleaned.Contains("__", StringComparison.Ordinal))
    {
        cleaned = cleaned.Replace("__", "_", StringComparison.Ordinal);
    }

    cleaned = cleaned.Trim('_');
    if (string.IsNullOrWhiteSpace(cleaned))
    {
        throw new InvalidOperationException("FreeText must contain at least one letter or digit after sanitization.");
    }

    return cleaned;
}
