using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace FsmMaster;

// UnityEngine.Color does not survive a round trip through this loader generation's settings
// serializer without help. The Modding API hand-registers converters for Vector2/Vector3 but has
// none for Color, so Color falls through to ordinary member serialization, which walks its computed
// `linear`/`gamma` properties - each of which returns another Color - and throws. The API swallows
// the error and writes the member as an empty object `{}`, which then reads back as (0, 0, 0, 0):
// every configurable color silently becomes fully transparent on the next launch. That is invisible
// in the panels (their text colors are mostly hardcoded) but fatal to the graph overlay, whose
// node chrome and transition lines are all GL-drawn in configured colors - the geometry is emitted
// correctly and draws with zero alpha, so the graph appears to render nothing but the handful of
// shapes whose colors happen to be hardcoded.
//
// Written as explicit r/g/b/a floats rather than a packed hex string so the file stays
// hand-editable, and so alpha is always stated outright instead of being implied by omission.
//
// Non-generic JsonConverter on purpose: the generic JsonConverter<T> overload was added in
// Newtonsoft.Json 11, and hk1432 ships 9.0.1 (hk1578 ships 11.0.1). This shared source has to
// compile against both.
internal sealed class ColorJsonConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) => objectType == typeof(Color);

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        WriteEntry(writer, (Color)value!);
    }

    // Split out alongside ReadEntry so the palette-array converter below writes an identical shape.
    internal static void WriteEntry(JsonWriter writer, Color color)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("r");
        writer.WriteValue(color.r);
        writer.WritePropertyName("g");
        writer.WriteValue(color.g);
        writer.WritePropertyName("b");
        writer.WriteValue(color.b);
        writer.WritePropertyName("a");
        writer.WriteValue(color.a);
        writer.WriteEndObject();
    }

    public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        // Whatever the settings object was constructed with - i.e. the property initializer's own
        // default - so a settings file written before this converter existed (every color in it an
        // empty `{}`) heals itself on load instead of pinning the defaults to transparent black and
        // requiring the player to delete the file by hand.
        Color fallback = existingValue is Color existing ? existing : default;
        return ReadEntry(JToken.Load(reader), fallback);
    }

    // Reads one entry, falling back to the value the settings object already holds for it. Split out
    // so the palette-array converter below can reuse the same per-entry rule.
    internal static Color ReadEntry(JToken token, Color fallback)
    {
        if (token.Type != JTokenType.Object)
        {
            return fallback;
        }

        var obj = (JObject)token;
        JToken? r = obj["r"];
        JToken? g = obj["g"];
        JToken? b = obj["b"];
        JToken? a = obj["a"];

        // An object carrying none of the four components is the legacy empty `{}` shape, not a color.
        if (r == null && g == null && b == null && a == null)
        {
            return fallback;
        }

        // A hand-edited entry may legitimately give only r/g/b; alpha then means opaque, matching
        // UnityEngine.Color's own three-argument constructor rather than defaulting to invisible.
        return new Color(
            r?.Value<float>() ?? 0f,
            g?.Value<float>() ?? 0f,
            b?.Value<float>() ?? 0f,
            a?.Value<float>() ?? 1f);
    }
}

// Same job as ColorJsonConverter, for the two indexed palettes. A per-item converter is not enough
// for these: Newtonsoft builds a fresh array when deserializing one, so an item converter is handed
// no existing value to fall back on, and every entry of a settings file written before this fix
// (each of them the legacy empty `{}`) would still load as transparent black. Converting the array
// as a whole keeps the palette the settings object was constructed with available as the per-index
// fallback, so those files heal on load exactly like the scalar colors do.
internal sealed class ColorPaletteJsonConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) => objectType == typeof(Color[]);

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        writer.WriteStartArray();
        foreach (Color color in (Color[])value!)
        {
            ColorJsonConverter.WriteEntry(writer, color);
        }
        writer.WriteEndArray();
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        Color[] defaults = existingValue as Color[] ?? new Color[0];

        JToken token = JToken.Load(reader);
        if (token.Type != JTokenType.Array)
        {
            return existingValue;
        }

        var array = (JArray)token;
        var result = new Color[array.Count];
        for (int i = 0; i < array.Count; i++)
        {
            // Index past the end of the built-in palette (a longer hand-written file) has no default
            // to fall back on; an unreadable entry there stays whatever it parsed to.
            Color fallback = i < defaults.Length ? defaults[i] : default;
            result[i] = ColorJsonConverter.ReadEntry(array[i], fallback);
        }

        // A file written with a shorter palette than the code expects would otherwise silently shrink
        // it, and every ColorIndex past the end would throw on lookup.
        if (result.Length < defaults.Length)
        {
            var padded = new Color[defaults.Length];
            Array.Copy(result, padded, result.Length);
            Array.Copy(defaults, result.Length, padded, result.Length, defaults.Length - result.Length);
            return padded;
        }

        return result;
    }
}
