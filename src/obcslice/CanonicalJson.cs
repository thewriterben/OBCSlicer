using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ObcSlice;

/// <summary>
/// Canonical JSON, byte-compatible with Project BINGO's kernel
/// (`json.dumps(obj, sort_keys=True, separators=(",",":"))`, default ensure_ascii)
/// and with OpenDesignCore's implementation, from which this is ported verbatim
/// (OpenDesignCore, Apache-2.0, src/OpenDesignCore/Provenance/CanonicalJson.cs).
/// Keys sorted ordinally, no whitespace, non-ASCII escaped as \uXXXX, ASCII output.
///
/// Floating-point values are deliberately rejected — their textual form is not
/// stable across languages. Represent measured quantities as strings with the
/// unit in the key (e.g. "voxel_size_mm": "0.20").
/// </summary>
public static class CanonicalJson
{
    public static byte[] Serialize(object? oValue)
    {
        StringBuilder oSb = new();
        Append(oSb, oValue);
        return Encoding.ASCII.GetBytes(oSb.ToString());
    }

    public static string StrSha256(object? oValue)
        => Convert.ToHexString(SHA256.HashData(Serialize(oValue))).ToLowerInvariant();

    private static void Append(StringBuilder oSb, object? oValue)
    {
        switch (oValue)
        {
            case null:
                oSb.Append("null");
                break;
            case bool bValue:
                oSb.Append(bValue ? "true" : "false");
                break;
            case string strValue:
                AppendString(oSb, strValue);
                break;
            case int nValue:
                oSb.Append(nValue.ToString(CultureInfo.InvariantCulture));
                break;
            case long nValue:
                oSb.Append(nValue.ToString(CultureInfo.InvariantCulture));
                break;
            case float or double or decimal:
                throw new ArgumentException(
                    "Floating-point values are not allowed in canonical JSON; " +
                    "encode them as strings with the unit in the key.");
            case IReadOnlyDictionary<string, object?> oDict:
                oSb.Append('{');
                bool bFirst = true;
                foreach (string strKey in oDict.Keys.Order(StringComparer.Ordinal))
                {
                    if (!bFirst)
                        oSb.Append(',');
                    bFirst = false;
                    AppendString(oSb, strKey);
                    oSb.Append(':');
                    Append(oSb, oDict[strKey]);
                }
                oSb.Append('}');
                break;
            case IEnumerable<object?> oList:
                oSb.Append('[');
                bool bFirstItem = true;
                foreach (object? oItem in oList)
                {
                    if (!bFirstItem)
                        oSb.Append(',');
                    bFirstItem = false;
                    Append(oSb, oItem);
                }
                oSb.Append(']');
                break;
            default:
                throw new ArgumentException(
                    $"Unsupported type in canonical JSON: {oValue.GetType().FullName}");
        }
    }

    private static void AppendString(StringBuilder oSb, string strValue)
    {
        oSb.Append('"');
        foreach (char c in strValue)
        {
            switch (c)
            {
                case '"': oSb.Append("\\\""); break;
                case '\\': oSb.Append("\\\\"); break;
                case '\b': oSb.Append("\\b"); break;
                case '\f': oSb.Append("\\f"); break;
                case '\n': oSb.Append("\\n"); break;
                case '\r': oSb.Append("\\r"); break;
                case '\t': oSb.Append("\\t"); break;
                default:
                    if (c < 0x20 || c > 0x7E)
                        oSb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        oSb.Append(c);
                    break;
            }
        }
        oSb.Append('"');
    }
}
