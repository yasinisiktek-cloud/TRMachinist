using System.Numerics;
using System.Text.Json;

namespace TRMachinist.Core;

internal static class JsonRead
{
    public static string String(JsonElement element, string property, string fallback = "") =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    public static double Double(JsonElement element, string property, double fallback = 0) =>
        element.TryGetProperty(property, out var value) && value.TryGetDouble(out var result) ? result : fallback;

    public static int Int(JsonElement element, string property, int fallback = 0) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : fallback;

    public static bool Bool(JsonElement element, string property, bool fallback = false) =>
        element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    public static Vector3 Vector(JsonElement element, Vector3 fallback)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            var values = element.EnumerateArray().Take(3).Select(x => x.TryGetSingle(out var v) ? v : 0).ToArray();
            if (values.Length == 3) return new Vector3(values[0], values[1], values[2]);
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            return new Vector3(
                (float)Double(element, "x"),
                (float)Double(element, "y"),
                (float)Double(element, "z"));
        }

        return fallback;
    }

    public static Vector3 VectorProperty(JsonElement element, string property, Vector3 fallback) =>
        element.TryGetProperty(property, out var value) ? Vector(value, fallback) : fallback;

    public static Matrix4x4 Orientation(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return Matrix4x4.Identity;
        return new Matrix4x4(
            (float)Double(element, "xx", 1), (float)Double(element, "xy"), (float)Double(element, "xz"), 0,
            (float)Double(element, "yx"), (float)Double(element, "yy", 1), (float)Double(element, "yz"), 0,
            (float)Double(element, "zx"), (float)Double(element, "zy"), (float)Double(element, "zz", 1), 0,
            0, 0, 0, 1);
    }
}
