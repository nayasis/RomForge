using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Input;

namespace RomForge.Cli;

internal static class CliValues
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static PropertyInfo Property(Type type, string name) => type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
        .FirstOrDefault(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unknown property '{name}' on {type.Name}.");

    public static object? Get(object root, string path)
    {
        object? value = root;
        foreach (var token in Tokens(path))
        {
            if (value is null)
                throw new ArgumentException($"'{path}' crosses a null value.");
            value = ReadToken(value, token);
        }
        return value;
    }

    public static void Set(object root, string path, JsonElement json, Func<string, object?> reference)
    {
        var tokens = Tokens(path);
        object owner = root;
        foreach (var token in tokens[..^1])
            owner = ReadToken(owner, token) ?? throw new ArgumentException($"'{path}' crosses a null value.");

        string last = tokens[^1];
        if (int.TryParse(last, out int index) && owner is IList list)
        {
            var elementType = list[index]?.GetType() ?? owner.GetType().GetGenericArguments()[0];
            list[index] = Convert(json, elementType, reference);
            return;
        }

        var property = Property(owner.GetType(), last);
        if (property.SetMethod?.IsPublic == true)
            property.SetValue(owner, Convert(json, property.PropertyType, reference));
        else if (property.GetValue(owner) is IList items && json.ValueKind == JsonValueKind.Array)
        {
            Type elementType = items.GetType().GetGenericArguments()[0];
            var replacements = json.EnumerateArray().Select(item => Convert(item, elementType, reference)).ToList();
            items.Clear();
            foreach (var item in replacements)
                items.Add(item);
        }
        else
            throw new ArgumentException($"Property '{path}' is read-only.");
    }

    public static JsonElement Parse(string value)
    {
        try { return JsonSerializer.Deserialize<JsonElement>(value); }
        catch (JsonException) { return JsonSerializer.SerializeToElement(value); }
    }

    public static object? Convert(JsonElement value, Type type, Func<string, object?> reference)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("$ref", out var path))
        {
            var result = reference(path.GetString()!);
            if (result is not null && !type.IsInstanceOfType(result))
                throw new ArgumentException($"Reference '{path}' cannot be assigned to {type.Name}.");
            return result;
        }
        if (type == typeof(byte[]) && value.ValueKind == JsonValueKind.Object && value.TryGetProperty("$file", out var file))
            return File.ReadAllBytes(file.GetString()!);
        if (value.ValueKind == JsonValueKind.Array && type != typeof(byte[])
            && (type.IsArray || type.IsGenericType && typeof(IEnumerable).IsAssignableFrom(type)))
        {
            Type elementType = type.IsArray ? type.GetElementType()! : type.GetGenericArguments()[0];
            var converted = value.EnumerateArray().Select(item => Convert(item, elementType, reference)).ToArray();
            if (type.IsArray)
            {
                var array = Array.CreateInstance(elementType, converted.Length);
                for (int index = 0; index < converted.Length; index++) array.SetValue(converted[index], index);
                return array;
            }
            Type collectionType = type.IsInterface ? typeof(List<>).MakeGenericType(elementType) : type;
            var list = (IList)(Activator.CreateInstance(collectionType) ?? throw new ArgumentException($"Cannot construct {type.Name}."));
            foreach (object? item in converted) list.Add(item);
            return list;
        }
        if (type == typeof(CancellationToken)) return CancellationToken.None;
        if (value.ValueKind == JsonValueKind.Null) return null;
        Type actual = Nullable.GetUnderlyingType(type) ?? type;
        if (actual.IsEnum && value.ValueKind == JsonValueKind.String)
            return Enum.Parse(actual, value.GetString()!, true);
        if (actual == typeof(string))
            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        return JsonSerializer.Deserialize(value.GetRawText(), type, JsonOptions);
    }

    public static object? Snapshot(object? value, int depth = 0)
    {
        if (value is null) return null;
        var type = value.GetType();
        if (type.IsEnum) return value.ToString();
        if (type.IsPrimitive || value is string or decimal or DateTime or Guid) return value;
        if (depth >= 6 || value is byte[] or Delegate or ICommand) return null;
        if (type.Namespace?.StartsWith("System.Windows", StringComparison.Ordinal) == true) return null;
        if (value is IEnumerable items)
            return items.Cast<object?>().Select(item => Snapshot(item, depth + 1)).ToArray();

        var state = new Dictionary<string, object?>();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || property.GetMethod is null
                || property.Name is "Tools" or "LogEntries" || property.Name.EndsWith("Visibility")
                || property.Name.EndsWith("Command") || property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                object? result = Snapshot(property.GetValue(value), depth + 1);
                if (result is not null) state[property.Name] = result;
            }
            catch (Exception) { /* UI-only computed properties are not part of CLI state. */ }
        }
        return state;
    }

    public static string Describe(Type type)
    {
        Type actual = Nullable.GetUnderlyingType(type) ?? type;
        if (actual.IsEnum) return string.Join("|", Enum.GetNames(actual));
        if (actual.IsGenericType) return $"{actual.Name.Split('`')[0]}<{string.Join(",", actual.GetGenericArguments().Select(Describe))}>";
        return actual.Name;
    }

    private static string[] Tokens(string path)
    {
        var tokens = Regex.Matches(path, @"[^.\[\]]+").Select(match => match.Value).ToArray();
        if (tokens.Length == 0) throw new ArgumentException("Property path cannot be empty.");
        return tokens;
    }

    private static object? ReadToken(object value, string token) => value is IList list && int.TryParse(token, out int index)
        ? list[index]
        : Property(value.GetType(), token).GetValue(value);
}
