using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace DiskImagerX.Disk;

/// <summary>
/// Minimal Apple plist (XML) reader — enough to walk the output of
/// <c>diskutil list -plist</c> / <c>diskutil info -plist</c>. Returns nested
/// Dictionary&lt;string,object&gt; / List&lt;object&gt; / string / long / bool.
/// </summary>
internal static class PlistParser
{
    public static object? Parse(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var root = doc.Root;                    // <plist>
            var first = root?.Elements().FirstOrDefault();
            return first is null ? null : ParseNode(first);
        }
        catch { return null; }
    }

    private static object? ParseNode(XElement el)
    {
        switch (el.Name.LocalName)
        {
            case "dict":
                var d = new Dictionary<string, object?>(StringComparer.Ordinal);
                var kids = el.Elements().ToList();
                for (int i = 0; i + 1 < kids.Count; i += 2)
                {
                    if (kids[i].Name.LocalName != "key") { i--; continue; }
                    d[kids[i].Value] = ParseNode(kids[i + 1]);
                }
                return d;
            case "array":
                var list = new List<object?>();
                foreach (var c in el.Elements()) list.Add(ParseNode(c));
                return list;
            case "string":  return el.Value;
            case "integer": return long.TryParse(el.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0L;
            case "real":    return double.TryParse(el.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : 0d;
            case "true":    return true;
            case "false":   return false;
            default:        return el.Value;
        }
    }

    // convenience accessors ---------------------------------------------------
    public static object? Get(object? node, string key)
        => node is Dictionary<string, object?> d && d.TryGetValue(key, out var v) ? v : null;

    public static long GetLong(object? node, string key)
        => Get(node, key) is long l ? l : 0L;

    public static string GetString(object? node, string key)
        => Get(node, key) as string ?? "";

    public static bool GetBool(object? node, string key)
        => Get(node, key) is bool b && b;

    public static List<object?> GetArray(object? node, string key)
        => Get(node, key) as List<object?> ?? new List<object?>();
}
