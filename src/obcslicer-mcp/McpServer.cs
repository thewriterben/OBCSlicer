using System.Text.Json;

namespace ObcSlicerMcp;

/// <summary>Minimal MCP stdio server: newline-delimited JSON-RPC 2.0, methods
/// initialize / notifications/* / tools/list / tools/call / ping. HandleMessage
/// is pure string→string so the protocol is testable without a process.</summary>
public static class McpServer
{
    public const string StrProtocolVersion = "2025-06-18";
    private const string StrServerVersion = "0.1.0";

    public static void RunLoop(TextReader oIn, TextWriter oOut)
    {
        string? strLine;
        while ((strLine = oIn.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(strLine))
                continue;
            string? strResponse = StrHandleMessage(strLine);
            if (strResponse is not null)
            {
                oOut.WriteLine(strResponse);
                oOut.Flush();
            }
        }
    }

    /// <summary>Returns the serialized response, or null for notifications.</summary>
    public static string? StrHandleMessage(string strMessage)
    {
        JsonDocument oDoc;
        try
        {
            oDoc = JsonDocument.Parse(strMessage);
        }
        catch (JsonException)
        {
            return StrError(null, -32700, "Parse error");
        }

        using (oDoc)
        {
            JsonElement oRoot = oDoc.RootElement;
            object? oId = null;
            bool bHasId = oRoot.TryGetProperty("id", out JsonElement oIdEl);
            if (bHasId)
                oId = oIdEl.ValueKind switch
                {
                    JsonValueKind.Number => oIdEl.GetInt64(),
                    JsonValueKind.String => oIdEl.GetString(),
                    _ => null,
                };

            string strMethod = oRoot.TryGetProperty("method", out JsonElement oMethod)
                ? oMethod.GetString() ?? "" : "";

            if (strMethod.StartsWith("notifications/", StringComparison.Ordinal))
                return null;

            JsonElement oParams = default;
            bool bHasParams = oRoot.TryGetProperty("params", out oParams);

            switch (strMethod)
            {
                case "initialize":
                    return StrResult(oId, new Dictionary<string, object?>
                    {
                        ["protocolVersion"] = bHasParams
                            && oParams.TryGetProperty("protocolVersion", out JsonElement oPv)
                            && oPv.GetString() is string strPv ? strPv : StrProtocolVersion,
                        ["capabilities"] = new Dictionary<string, object?>
                        {
                            ["tools"] = new Dictionary<string, object?>(),
                        },
                        ["serverInfo"] = new Dictionary<string, object?>
                        {
                            ["name"] = "obcslicer-mcp",
                            ["version"] = StrServerVersion,
                        },
                    });

                case "ping":
                    return StrResult(oId, new Dictionary<string, object?>());

                case "tools/list":
                    return StrResult(oId, new Dictionary<string, object?>
                    {
                        ["tools"] = Tools.ToolDefinitions(),
                    });

                case "tools/call":
                {
                    if (!bHasParams || !oParams.TryGetProperty("name", out JsonElement oName)
                        || oName.GetString() is not string strName)
                        return StrError(oId, -32602, "tools/call requires params.name");
                    JsonElement oArgs = oParams.TryGetProperty("arguments", out JsonElement oA)
                        ? oA : JsonDocument.Parse("{}").RootElement;
                    try
                    {
                        string strText = Tools.StrCall(strName, oArgs);
                        return StrResult(oId, OContent(strText, bError: false));
                    }
                    catch (Tools.ToolError e)
                    {
                        return StrResult(oId, OContent(e.Message, bError: true));
                    }
                }

                default:
                    return StrError(oId, -32601, $"Method not found: {strMethod}");
            }
        }
    }

    private static Dictionary<string, object?> OContent(string strText, bool bError) => new()
    {
        ["content"] = new List<object?>
        {
            new Dictionary<string, object?> { ["type"] = "text", ["text"] = strText },
        },
        ["isError"] = bError,
    };

    private static string StrResult(object? oId, object oResult)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0", ["id"] = oId, ["result"] = oResult,
        });

    private static string StrError(object? oId, int nCode, string strMessage)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0", ["id"] = oId,
            ["error"] = new Dictionary<string, object?>
            {
                ["code"] = nCode, ["message"] = strMessage,
            },
        });
}
