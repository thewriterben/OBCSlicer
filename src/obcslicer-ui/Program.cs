namespace ObcSlicerUi;

// obcslicer-ui — local GUI over the pipeline. Loopback only, same trust posture
// as studio-core's default. Config (last-used paths) persists next to the repo's
// .tools; nothing physical is defaulted — the page inherits the pipeline's
// refusals and shows every degradation verbatim.
public static class Program
{
    public static int Main(string[] args)
    {
        int nPort = 8771;
        for (int i = 0; i < args.Length; i++)
            if (args[i] == "--port" && i + 1 < args.Length)
                nPort = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);

        string strHtml = Path.Combine(AppContext.BaseDirectory, "ui.html");
        if (!File.Exists(strHtml))
            throw new FileNotFoundException($"ui.html not found beside the binary: {strHtml}");

        // Config beside the repo's .tools when run from the repo; else next to the exe.
        string strConfig = Path.Combine(AppContext.BaseDirectory, "ui-config.json");
        for (DirectoryInfo? oDir = new(AppContext.BaseDirectory); oDir is not null; oDir = oDir.Parent)
            if (File.Exists(Path.Combine(oDir.FullName, "OBCSlicer.sln")))
            {
                strConfig = Path.Combine(oDir.FullName, ".tools", "ui-config.json");
                break;
            }

        new Server(strHtml, strConfig).Run(nPort);
        return 0;
    }
}
