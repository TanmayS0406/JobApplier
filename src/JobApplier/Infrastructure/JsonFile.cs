using System.Text.Json;
using System.Text.Json.Serialization;

namespace JobApplier.Infrastructure;

/// <summary>Small helpers for JSON files that must never be left half-written.</summary>
public static class JsonFile
{
    /// <summary>
    /// Shared by the job store, the profile and the Agent Framework's typed results (<c>RunAsync&lt;T&gt;</c>). It is frozen at
    /// creation: an options instance that is still mutable can be changed by one component while another is first using it,
    /// which showed up as an intermittent "JsonSerializerOptions instance is read-only" error when jobs ran in parallel.
    /// </summary>
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public static T? Read<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes to a temp file and swaps it in, so a crash never leaves a truncated file.</summary>
    public static void Write<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = path + "." + Guid.NewGuid().ToString("N")[..6] + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best effort */ }
            throw;
        }
    }
}
