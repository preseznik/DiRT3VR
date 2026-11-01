using System.Data;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using EgoEngineLibrary.Data;
using EgoEngineLibrary.Language;

// Read-only: nothing in the game folder is changed.
//   dump    <game> <schema.xml> <output.json> <table> [table ...]   all columns of the named tables
//   catalog <game> <schema.xml> <output.json>                       the direct-launch event catalog
// xml <binary xml file> <output.xml>: converts one of the game's binary XML files (flow, states) for reading.
if (args.Length == 3 && args[0] == "xml")
{
    using var input = File.OpenRead(args[1]);
    var file = new EgoEngineLibrary.Xml.XmlFile(input);
    file.Document.Save(args[2]);
    Console.WriteLine($"wrote {args[2]}");
    return 0;
}
// lng <game> <regex>: English strings whose key or text matches, for research.
if (args.Length == 3 && args[0] == "lng")
{
    using var stream = File.OpenRead(Path.Combine(args[1], "language", "language_eng.lng"));
    var lng = new EgoEngineLibrary.Language.LngFile(stream);
    var pattern = new System.Text.RegularExpressions.Regex(args[2], System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    foreach (var entry in lng.hsht.Entries.Where(e => pattern.IsMatch(e.Key) || pattern.IsMatch(e.Value)).OrderBy(e => e.Key, StringComparer.Ordinal))
        Console.WriteLine($"{entry.Key} = {entry.Value}");
    return 0;
}
if (args.Length < 4 || (args[0] != "dump" && args[0] != "catalog"))
{
    Console.Error.WriteLine("usage: EventCatalog dump|catalog <DiRT 3 folder> <schemaDirt3.xml> <output.json> [table ...]");
    return 2;
}
var game = args[1];
var databasePath = Path.Combine(game, "database", "database.bin");
var database = new DatabaseFile(databasePath, args[2]);
var json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

if (args[0] == "dump")
{
    var dump = new SortedDictionary<string, object>(StringComparer.Ordinal);
    foreach (var name in args.Skip(4))
    {
        var table = database.Tables[name] ?? throw new InvalidDataException("Unknown table: " + name);
        var columns = table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray();
        dump[name] = new { columns, rows = table.Rows.Cast<DataRow>().Select(r => columns.ToDictionary(c => c, c => r[c] is DBNull ? null : r[c])).ToArray() };
    }
    File.WriteAllText(args[3], JsonSerializer.Serialize(dump, json));
    Console.WriteLine($"wrote {dump.Count} tables to {args[3]}");
    return 0;
}

var languagePath = Path.Combine(game, "language", "language_eng.lng");
LngFile language;
using (var stream = File.OpenRead(languagePath)) language = new LngFile(stream);
var strings = language.hsht.Entries.GroupBy(e => e.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
string? Name(object? id) => id is string key && key.Length > 0 && strings.TryGetValue("db_" + key, out var value) ? value : null;
IEnumerable<DataRow> Rows(string table) => database.Tables[table]!.Rows.Cast<DataRow>();
int Int(DataRow row, string column) => Convert.ToInt32(row[column]);
string Str(DataRow row, string column) => row[column] as string ?? "";
bool Bool(DataRow row, string column) => row[column] is bool b && b;

var disciplineRows = Rows("discipline").Where(r => Bool(r, "valid_for_benchmark")).ToDictionary(r => Int(r, "id"));
var singleRace = Rows("single_race_discipline").ToDictionary(r => Int(r, "discipline"));
var laps = Rows("single_race_laps").ToArray();
var trackTypes = Rows("track_type").ToDictionary(r => Int(r, "id"), r => Str(r, "type"));
var tracks = Rows("track").ToDictionary(r => Int(r, "id"));
var locations = Rows("location").ToDictionary(r => Int(r, "id"));
var models = Rows("vehicle_model").ToDictionary(r => Int(r, "id"));
var classes = Rows("vehicle_class").ToDictionary(r => Int(r, "id"));

var disciplines = disciplineRows.Values.OrderBy(r => Int(r, "order_index")).Select(r =>
{
    singleRace.TryGetValue(Int(r, "id"), out var sr);
    var lapsType = sr is null ? -1 : Int(sr, "laps_type_id");
    return new
    {
        id = Str(r, "file_string"),
        name = Name(r["name_string_id"]) ?? Str(r, "file_string"),
        trackType = trackTypes[Int(r, "track_type_id")],
        staggeredStart = Bool(r, "staggered_start"),
        laps = laps.Where(l => Int(l, "laps_type_id") == lapsType).OrderBy(l => Int(l, "order_index")).Select(l => Int(l, "num_laps")).Distinct().ToArray(),
    };
}).ToArray();
var disciplineIds = disciplineRows.ToDictionary(p => p.Key, p => Str(p.Value, "file_string"));

var routes = Rows("track_model")
    .Where(r => disciplineIds.ContainsKey(Int(r, "discipline_id")) && !Bool(r, "not_single_race"))
    .Where(r => Directory.Exists(Path.Combine(game, "tracks", "locations", Str(r, "folder_string"), Str(r, "file_string"), Str(r, "route_string"))))
    .OrderBy(r => Int(r, "discipline_id")).ThenBy(r => Int(r, "order_index")).ThenBy(r => Int(r, "id"))
    .Select(r =>
    {
        var track = tracks[Int(r, "track_id")];
        var location = locations[Int(track, "location_id")];
        return new
        {
            id = Int(r, "id"),
            discipline = disciplineIds[Int(r, "discipline_id")],
            country = Str(r, "folder_string"),
            track = Str(r, "file_string"),
            route = Str(r, "route_string"),
            name = Name(r["name_string_id"]),
            trackName = Name(track["name_string_id"]),
            locationName = Name(location["name_string_id"]),
            trackType = trackTypes[Int(r, "track_type_id")],
            lengthMetres = Convert.ToSingle(r["length"]),
        };
    }).ToArray();

var cars = Rows("vehicle")
    .Where(r => disciplineIds.ContainsKey(Int(r, "discipline")) && !Bool(r, "easter_egg") && !Bool(r, "swap_with_dummy"))
    .Where(r => Directory.Exists(Path.Combine(game, "cars", "models", Str(r, "file_string"))))
    .OrderBy(r => Int(r, "discipline")).ThenBy(r => Int(r, "order_index")).ThenBy(r => Int(r, "id"))
    .Select(r =>
    {
        models.TryGetValue(Int(r, "vehicle_model_id"), out var model);
        classes.TryGetValue(Int(r, "vehicle_class_id"), out var vehicleClass);
        return new
        {
            code = Str(r, "file_string"),
            name = model is null ? null : Name(model["full_name_string_id"]),
            discipline = disciplineIds[Int(r, "discipline")],
            vehicleClass = vehicleClass is null ? null : Str(vehicleClass, "file_string"),
            vehicleClassName = vehicleClass is null ? null : Name(vehicleClass["type_string_id"]),
        };
    }).ToArray();

string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
var catalog = new
{
    schema = 1,
    game = "DiRT 3 Complete Edition (Steam)",
    sourceDatabaseSha256 = Hash(databasePath),
    sourceLanguageSha256 = Hash(languagePath),
    note = "Disciplines the game marks valid_for_benchmark; routes and cars whose folders are installed. Read-only extract; regenerate with tools/event-catalog.",
    disciplines,
    routes,
    cars,
};
File.WriteAllText(args[3], JsonSerializer.Serialize(catalog, json) + "\n");
Console.WriteLine($"wrote {disciplines.Length} disciplines, {routes.Length} routes, {cars.Length} cars to {args[3]}");
return 0;
