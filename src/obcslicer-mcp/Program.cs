namespace ObcSlicerMcp;

// stdio MCP server exposing obcslice to the ecosystem (ADR-0004).
// stdout is the protocol channel; anything diagnostic goes to stderr.
// Configuration via env: OBCSLICE_ORCA (slicer binary), OBCSLICE_STAGING
// (AdvancedStudio staging directory).
public static class Program
{
    public static int Main()
    {
        Console.Error.WriteLine(
            $"obcslicer-mcp {McpServer.StrProtocolVersion} ready (tools: slice, stage_for_studio)");
        McpServer.RunLoop(Console.In, Console.Out);
        return 0;
    }
}
