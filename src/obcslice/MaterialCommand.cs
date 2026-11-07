using System.Security.Cryptography;

namespace ObcSlice;

/// <summary>`obcslice material pin`: fetch (or take from files) an OFD filament
/// record and its manifest, verify, and write the canonical obc-material/0.1 record.</summary>
public static class MaterialCommand
{
    public const string StrUsage = """
        obcslice material pin --brand ID --material ID --filament SLUG --out FILE
                              [--from-file FILAMENT.json --manifest-file MANIFEST.json]

        Pins one Open Filament Database filament as <FILE> (canonical JSON,
        obc-material/0.1). Without --from-file both the record and manifest.json
        are fetched from api.openfilamentdatabase.org; the bytes must match the
        manifest's sha256 for the dataset version or nothing is written.
        Exit codes: 0 ok · 2 refused (not in manifest, hash mismatch, missing field, bad args).
        """;

    public static int Run(string[] args)
    {
        if (args.Length < 2 || args[1] is "--help" or "-h")
        {
            Console.WriteLine(StrUsage);
            return args.Length < 2 ? 2 : 0;
        }
        if (args[1] != "pin")
            throw new ArgumentException($"Unknown material command '{args[1]}'. Only 'pin' exists.");

        string? strBrand = null, strMaterial = null, strFilament = null, strOut = null;
        string? strFromFile = null, strManifestFile = null;
        for (int i = 2; i < args.Length; i++)
        {
            string StrNext(string strFlag) => i + 1 < args.Length
                ? args[++i] : throw new ArgumentException($"{strFlag} needs a value.");
            switch (args[i])
            {
                case "--brand": strBrand = StrNext("--brand"); break;
                case "--material": strMaterial = StrNext("--material"); break;
                case "--filament": strFilament = StrNext("--filament"); break;
                case "--out": strOut = StrNext("--out"); break;
                case "--from-file": strFromFile = StrNext("--from-file"); break;
                case "--manifest-file": strManifestFile = StrNext("--manifest-file"); break;
                default: throw new ArgumentException($"Unknown argument '{args[i]}'.");
            }
        }
        if (strBrand is null || strMaterial is null || strFilament is null || strOut is null)
            throw new ArgumentException("--brand, --material, --filament and --out are all required.");
        if ((strFromFile is null) != (strManifestFile is null))
            throw new ArgumentException("--from-file and --manifest-file go together.");

        MaterialPin.ValidateSegments(strBrand, strMaterial, strFilament);
        string strPath = MaterialPin.StrManifestPath(strBrand, strMaterial, strFilament);
        byte[] abFilament, abManifest;
        if (strFromFile is not null)
        {
            if (!File.Exists(strFromFile)) throw new FileNotFoundException($"not found: {strFromFile}");
            if (!File.Exists(strManifestFile)) throw new FileNotFoundException($"not found: {strManifestFile}");
            abFilament = File.ReadAllBytes(strFromFile);
            abManifest = File.ReadAllBytes(strManifestFile!);
        }
        else
        {
            using var oHttp = new HttpClient();
            oHttp.DefaultRequestHeaders.UserAgent.ParseAdd("obcslice/0.1.0");
            byte[] AbGet(string strRel)
            {
                try { return oHttp.GetByteArrayAsync(MaterialPin.StrBaseUrl + strRel).GetAwaiter().GetResult(); }
                catch (HttpRequestException e) { throw new InvalidDataException($"fetch {strRel} failed: {e.Message}"); }
                catch (TaskCanceledException) { throw new InvalidDataException($"fetch {strRel} timed out."); }
            }
            abManifest = AbGet("manifest.json");
            // Refuse an unknown filament before asking the server for it.
            if (!MaterialPin.BInManifest(abManifest, strPath))
                throw new InvalidDataException(
                    $"{strPath} is not in the OFD manifest: brand/material/filament does not exist in that dataset.");
            abFilament = AbGet(strPath);
        }

        byte[] abRecord = MaterialPin.Build(
            strBrand, strMaterial, strFilament, abFilament, abManifest, DateTime.UtcNow);
        string? strDir = Path.GetDirectoryName(Path.GetFullPath(strOut));
        if (strDir is not null) Directory.CreateDirectory(strDir);
        File.WriteAllBytes(strOut, abRecord);
        Console.WriteLine($"material record: {strOut}");
        Console.WriteLine($"  sha256 {Convert.ToHexString(SHA256.HashData(abRecord)).ToLowerInvariant()}");
        return 0;
    }
}
