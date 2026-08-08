using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace HistoryJanus.Views;

/// <summary>Restores typed Janus contracts when a command result crossed the AppShell web boundary.</summary>
internal static class ModuleResultData
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static bool TryRead<T>(object? data, [NotNullWhen(true)] out T? value)
    {
        if (data is T typed)
        {
            value = typed;
            return true;
        }

        if (data is JsonElement element)
        {
            try
            {
                value = element.Deserialize<T>(JsonOptions);
                return value is not null;
            }
            catch (JsonException)
            {
            }
            catch (NotSupportedException)
            {
            }
        }

        value = default;
        return false;
    }
}
