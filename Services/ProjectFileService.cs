using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CircuitForge.Models;

namespace CircuitForge.Services;

public sealed class ProjectFileService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public void Save(string path, ProjectDocument project) =>
        File.WriteAllText(path, JsonSerializer.Serialize(project, Options));

    public ProjectDocument Load(string path)
    {
        var doc = JsonSerializer.Deserialize<ProjectDocument>(File.ReadAllText(path), Options)
                  ?? throw new InvalidDataException("Пустой или неверный файл проекта.");
        // Инициализируем Waypoints для проводов из старых проектов
        foreach (var w in doc.Wires)
            w.Waypoints ??= new();
        return doc;
    }
}
