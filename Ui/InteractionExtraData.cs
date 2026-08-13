using System.Text.Json;

namespace Mezube.Ui;

/// <summary>Parse Mezon button ExtraData that carries radio selections.</summary>
public static class InteractionExtraData
{
    public static IReadOnlyList<string> ParseSelectedValues(string? extraData)
    {
        if (string.IsNullOrWhiteSpace(extraData))
        {
            return [];
        }

        var raw = extraData.Trim();
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            switch (root.ValueKind)
            {
                case JsonValueKind.Array:
                    return ReadStringArray(root);
                case JsonValueKind.String:
                    {
                        var s = root.GetString();
                        return string.IsNullOrWhiteSpace(s) ? [] : [s];
                    }
                case JsonValueKind.Object:
                    foreach (var name in new[] { "values", "value", "selected", "options", "radio", "data" })
                    {
                        if (!root.TryGetProperty(name, out var prop))
                        {
                            continue;
                        }

                        var fromProp = Extract(prop);
                        if (fromProp.Count > 0)
                        {
                            return fromProp;
                        }
                    }

                    // Object keyed by component id → value or array.
                    var collected = new List<string>();
                    foreach (var prop in root.EnumerateObject())
                    {
                        collected.AddRange(Extract(prop.Value));
                    }

                    return Dedup(collected);
            }
        }
        catch (JsonException)
        {
            // fall through to plain text
        }

        if (raw.Contains(',', StringComparison.Ordinal))
        {
            return Dedup(raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return [raw];
    }

    private static IReadOnlyList<string> Extract(JsonElement el)
        => el.ValueKind switch
        {
            JsonValueKind.Array => ReadStringArray(el),
            JsonValueKind.String => string.IsNullOrWhiteSpace(el.GetString()) ? [] : [el.GetString()!],
            JsonValueKind.Object when el.TryGetProperty("value", out var v) => Extract(v),
            JsonValueKind.Object when el.TryGetProperty("values", out var vs) => Extract(vs),
            _ => [],
        };

    private static IReadOnlyList<string> ReadStringArray(JsonElement arr)
    {
        var list = new List<string>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
            {
                list.Add(item.GetString()!);
            }
            else if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("value", out var v)
                     && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
            {
                list.Add(v.GetString()!);
            }
        }

        return Dedup(list);
    }

    private static IReadOnlyList<string> Dedup(IEnumerable<string> values)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var v in values)
        {
            var t = v.Trim();
            if (t.Length == 0 || !set.Add(t))
            {
                continue;
            }

            list.Add(t);
        }

        return list;
    }
}
