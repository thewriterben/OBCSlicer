using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Obc3mf;

/// <summary>
/// Writes an OrcaSlicer/BambuStudio-style *project* 3MF per docs/specs/MM-3MF.md.
///
/// Plain core-spec 3MF is not enough: Orca honors per-object filament (extruder)
/// assignments only for files carrying its project structure, and the minimal
/// sufficient structure was established empirically against OrcaSlicer V2.4.2
/// (GUI-saved reference + delta-debugging; see MM-3MF.md "validated minimal
/// project structure"): production-extension root model with component-wrapper
/// objects referencing per-object model files, model_settings.config with
/// wrapper-id objects + plate/assemble blocks, and a 22-key
/// project_settings.config whose machine facts come from the manifest — never
/// invented here. UUIDs are deterministic so output is byte-stable.
/// </summary>
public static class ThreeMfWriter
{
    public sealed record Part(
        string StrName,
        IndexedMesh oMesh,
        int NFilament,
        IReadOnlyDictionary<string, string> Overrides);

    /// <summary>Machine facts required for project emission. All values are
    /// caller-supplied (manifest) — refusing defaults is deliberate.</summary>
    public sealed record PrinterSpec(
        string StrNozzleDiameterMm,
        int NPrintableWidthMm,
        int NPrintableDepthMm,
        int NPrintableHeightMm);

    /// <summary>Display colors per filament slot, cosmetic only (Orca shows them;
    /// physical filament properties come from profiles at slice time).</summary>
    private static readonly string[] Palette =
    {
        "#FFFFFF", "#000000", "#FF0000", "#00FF00", "#0000FF", "#FFFF00",
        "#FF00FF", "#00FFFF", "#FF8000", "#8000FF", "#008080", "#808000",
    };

    private static string StrUuid(int n) =>
        $"0000{n.ToString("d4", CultureInfo.InvariantCulture)}-0000-4000-8000-000000000000";

    /// <summary>bAssembly=false: one printable object per part (side-by-side parts).
    /// bAssembly=true: ONE object whose volumes are the parts, each with its own
    /// extruder — the representation for regions that overlap in XY (e.g. stacked
    /// field bands), which as separate objects would refuse to slice.</summary>
    public static void Write(string strOutputPath, IReadOnlyList<Part> aParts, PrinterSpec oPrinter,
        bool bAssembly = false)
    {
        int nFilaments = aParts.Max(p => p.NFilament);

        // Plate placement: meshes stay in their own coordinates (bands reference
        // that frame); the BUILD transform moves the combined bounding box to the
        // plate center with its floor at Z=0 — identity transforms print parts
        // literally at their mesh coordinates, i.e. in the bed's corner.
        float fMinX = float.MaxValue, fMinY = float.MaxValue, fMinZ = float.MaxValue;
        float fMaxX = float.MinValue, fMaxY = float.MinValue;
        foreach (Part oP in aParts)
            foreach ((float x, float y, float z) in oP.oMesh.Vertices)
            {
                fMinX = MathF.Min(fMinX, x); fMaxX = MathF.Max(fMaxX, x);
                fMinY = MathF.Min(fMinY, y); fMaxY = MathF.Max(fMaxY, y);
                fMinZ = MathF.Min(fMinZ, z);
            }
        float fTx = oPrinter.NPrintableWidthMm / 2f - (fMinX + fMaxX) / 2f;
        float fTy = oPrinter.NPrintableDepthMm / 2f - (fMinY + fMaxY) / 2f;
        float fTz = -fMinZ;
        // 0.001mm placement precision: swallows voxel-derived float noise and -0,
        // and keeps the emitted text deterministic and readable.
        string StrT(float f)
        {
            float fRounded = MathF.Round(f, 3);
            if (fRounded == 0) fRounded = 0f;
            return fRounded.ToString("0.###", CultureInfo.InvariantCulture);
        }
        string strPlacement = $"1 0 0 0 1 0 0 0 1 {StrT(fTx)} {StrT(fTy)} {StrT(fTz)}";

        using FileStream oFile = File.Create(strOutputPath);
        using var oZip = new ZipArchive(oFile, ZipArchiveMode.Create);

        WriteXmlEntry(oZip, "[Content_Types].xml", oW =>
        {
            oW.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
            WriteDefault(oW, "rels", "application/vnd.openxmlformats-package.relationships+xml");
            WriteDefault(oW, "model", "application/vnd.ms-package.3dmanufacturing-3dmodel+xml");
            oW.WriteEndElement();
        });

        WriteXmlEntry(oZip, "_rels/.rels", oW =>
        {
            oW.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
            WriteRel(oW, "/3D/3dmodel.model", "rel-1");
            oW.WriteEndElement();
        });

        // Per-object model files. Mesh object ids are odd (1,3,…), wrapper ids even (2,4,…).
        var astrObjectFiles = new List<string>();
        for (int i = 0; i < aParts.Count; i++)
        {
            int nMeshId = 2 * i + 1;
            string strFile = $"3D/Objects/{StrSafeName(aParts[i].StrName)}_{nMeshId}.model";
            astrObjectFiles.Add(strFile);
            IndexedMesh oMesh = aParts[i].oMesh;
            WriteXmlEntry(oZip, strFile, oW =>
            {
                WriteModelOpen(oW);
                WriteBbsVersionMeta(oW);
                oW.WriteStartElement("resources");
                oW.WriteStartElement("object");
                oW.WriteAttributeString("id", StrInv(nMeshId));
                oW.WriteAttributeString("UUID", "http://schemas.microsoft.com/3dmanufacturing/production/2015/06",
                    StrUuid(300 + i));
                oW.WriteAttributeString("type", "model");
                oW.WriteStartElement("mesh");
                oW.WriteStartElement("vertices");
                foreach ((float x, float y, float z) in oMesh.Vertices)
                {
                    oW.WriteStartElement("vertex");
                    oW.WriteAttributeString("x", StrInv(x));
                    oW.WriteAttributeString("y", StrInv(y));
                    oW.WriteAttributeString("z", StrInv(z));
                    oW.WriteEndElement();
                }
                oW.WriteEndElement();
                oW.WriteStartElement("triangles");
                foreach ((int a, int b, int c) in oMesh.Triangles)
                {
                    oW.WriteStartElement("triangle");
                    oW.WriteAttributeString("v1", StrInv(a));
                    oW.WriteAttributeString("v2", StrInv(b));
                    oW.WriteAttributeString("v3", StrInv(c));
                    oW.WriteEndElement();
                }
                oW.WriteEndElement();
                oW.WriteEndElement(); // mesh
                oW.WriteEndElement(); // object
                oW.WriteEndElement(); // resources
                oW.WriteStartElement("build");
                oW.WriteEndElement();
                oW.WriteEndElement(); // model
            });
        }

        WriteXmlEntry(oZip, "3D/_rels/3dmodel.model.rels", oW =>
        {
            oW.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
            for (int i = 0; i < astrObjectFiles.Count; i++)
                WriteRel(oW, "/" + astrObjectFiles[i], $"rel-{i + 1}");
            oW.WriteEndElement();
        });

        // Wrapper objects: one per part normally; a single wrapper holding all
        // meshes as components in assembly mode. Wrapper ids follow the mesh ids.
        int nWrappers = bAssembly ? 1 : aParts.Count;
        int NWrapperId(int nWrapper) => bAssembly ? 2 * aParts.Count : 2 * nWrapper + 2;

        WriteXmlEntry(oZip, "3D/3dmodel.model", oW =>
        {
            WriteModelOpen(oW);
            WriteMeta(oW, "Application", "BambuStudio-02.06.00.51");
            WriteMeta(oW, "OrcaSlicer", "2.4.2");
            WriteBbsVersionMeta(oW);

            oW.WriteStartElement("resources");
            for (int w = 0; w < nWrappers; w++)
            {
                oW.WriteStartElement("object");
                oW.WriteAttributeString("id", StrInv(NWrapperId(w)));
                oW.WriteAttributeString("UUID", "http://schemas.microsoft.com/3dmanufacturing/production/2015/06",
                    StrUuid(1 + w));
                oW.WriteAttributeString("type", "model");
                oW.WriteStartElement("components");
                foreach (int i in bAssembly ? Enumerable.Range(0, aParts.Count) : new[] { w })
                {
                    oW.WriteStartElement("component");
                    oW.WriteAttributeString("path", "http://schemas.microsoft.com/3dmanufacturing/production/2015/06",
                        "/" + astrObjectFiles[i]);
                    oW.WriteAttributeString("objectid", StrInv(2 * i + 1));
                    oW.WriteAttributeString("UUID", "http://schemas.microsoft.com/3dmanufacturing/production/2015/06",
                        StrUuid(11 + i));
                    oW.WriteAttributeString("transform", "1 0 0 0 1 0 0 0 1 0 0 0");
                    oW.WriteEndElement();
                }
                oW.WriteEndElement();
                oW.WriteEndElement();
            }
            oW.WriteEndElement(); // resources

            oW.WriteStartElement("build");
            oW.WriteAttributeString("UUID", "http://schemas.microsoft.com/3dmanufacturing/production/2015/06",
                StrUuid(99));
            for (int w = 0; w < nWrappers; w++)
            {
                oW.WriteStartElement("item");
                oW.WriteAttributeString("objectid", StrInv(NWrapperId(w)));
                oW.WriteAttributeString("UUID", "http://schemas.microsoft.com/3dmanufacturing/production/2015/06",
                    StrUuid(21 + w));
                oW.WriteAttributeString("transform", strPlacement);
                oW.WriteAttributeString("printable", "1");
                oW.WriteAttributeString("auto_drop", "1");
                oW.WriteEndElement();
            }
            oW.WriteEndElement(); // build
            oW.WriteEndElement(); // model
        });

        WriteXmlEntry(oZip, "Metadata/model_settings.config", oW =>
        {
            oW.WriteStartElement("config");
            for (int w = 0; w < nWrappers; w++)
            {
                oW.WriteStartElement("object");
                oW.WriteAttributeString("id", StrInv(NWrapperId(w)));
                if (bAssembly)
                {
                    // One object; each volume (<part>) carries its own extruder —
                    // the fork importer reads per-part configs into ModelVolume::config.
                    WriteKv(oW, "name", "assembly");
                    WriteKv(oW, "extruder", StrInv(aParts[0].NFilament));
                    for (int i = 0; i < aParts.Count; i++)
                        WritePartElement(oW, aParts[i], 2 * i + 1, bWithExtruder: true);
                }
                else
                {
                    Part oPart = aParts[w];
                    WriteKv(oW, "name", oPart.StrName);
                    WriteKv(oW, "extruder", StrInv(oPart.NFilament));
                    foreach (KeyValuePair<string, string> oKv in
                             oPart.Overrides.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                        WriteKv(oW, oKv.Key, oKv.Value);
                    WritePartElement(oW, oPart, 2 * w + 1, bWithExtruder: false);
                }
                oW.WriteEndElement();
            }

            oW.WriteStartElement("plate");
            WriteKv(oW, "plater_id", "1");
            WriteKv(oW, "plater_name", "");
            WriteKv(oW, "locked", "false");
            WriteKv(oW, "filament_map_mode", "Auto For Flush");
            WriteKv(oW, "filament_maps", string.Join(' ', Enumerable.Repeat("1", nFilaments)));
            for (int w = 0; w < nWrappers; w++)
            {
                oW.WriteStartElement("model_instance");
                WriteKv(oW, "object_id", StrInv(NWrapperId(w)));
                WriteKv(oW, "instance_id", "0");
                WriteKv(oW, "identify_id", StrInv(w + 1));
                oW.WriteEndElement();
            }
            oW.WriteEndElement(); // plate

            oW.WriteStartElement("assemble");
            for (int w = 0; w < nWrappers; w++)
            {
                oW.WriteStartElement("assemble_item");
                oW.WriteAttributeString("object_id", StrInv(NWrapperId(w)));
                oW.WriteAttributeString("instance_id", "0");
                oW.WriteAttributeString("transform", strPlacement);
                oW.WriteAttributeString("offset", "0 0 0");
                oW.WriteEndElement();
            }
            oW.WriteEndElement(); // assemble
            oW.WriteEndElement(); // config
        });

        // The empirically minimal project_settings (22 keys). Filament arrays are
        // sized to the slot count with inert values — the real filament data is
        // loaded from profiles at slice time; machine facts come from the manifest.
        WriteRawEntry(oZip, "Metadata/project_settings.config",
            StrProjectSettings(nFilaments, oPrinter));
    }

    private static string StrProjectSettings(int nFilaments, PrinterSpec oPrinter)
    {
        string StrArr(string strValue) =>
            "[" + string.Join(',', Enumerable.Repeat('"' + strValue + '"', nFilaments)) + "]";
        string strColours = "[" + string.Join(',',
            Enumerable.Range(0, nFilaments).Select(i => '"' + Palette[i % Palette.Length] + '"')) + "]";
        int w = oPrinter.NPrintableWidthMm, d = oPrinter.NPrintableDepthMm;
        string strArea = $"[\"0x0\",\"{w}x0\",\"{w}x{d}\",\"0x{d}\"]";
        var oSb = new StringBuilder("{");
        void Kv(string strKey, string strJsonValue) =>
            oSb.Append('"').Append(strKey).Append("\":").Append(strJsonValue).Append(',');
        Kv("filament_colour", strColours);
        Kv("filament_colour_type", StrArr("1"));
        Kv("filament_deretraction_speed", StrArr("nil"));
        Kv("filament_diameter", StrArr("1.75"));
        Kv("filament_ironing_inset", StrArr("nil"));
        Kv("filament_ironing_spacing", StrArr("nil"));
        Kv("filament_ironing_speed", StrArr("nil"));
        Kv("filament_is_support", StrArr("0"));
        Kv("filament_settings_id", StrArr(""));
        Kv("filament_shrink", StrArr("100%"));
        Kv("filament_shrinkage_compensation_z", StrArr("100%"));
        Kv("filament_tower_interface_pre_extrusion_length", StrArr("0"));
        Kv("notes", "\"\"");
        Kv("nozzle_diameter", $"[\"{oPrinter.StrNozzleDiameterMm}\"]");
        Kv("print_sequence", "\"by layer\"");
        Kv("print_settings_id", "\"\"");
        Kv("printable_area", strArea);
        Kv("printable_height",
            $"\"{oPrinter.NPrintableHeightMm.ToString(CultureInfo.InvariantCulture)}\"");
        Kv("printer_agent", "\"\"");
        Kv("printer_extruder_id", "[\"1\"]");
        Kv("printer_settings_id", "\"\"");
        oSb.Append("\"printer_structure\":\"undefine\"}");
        return oSb.ToString();
    }

    private static void WritePartElement(XmlWriter oW, Part oPart, int nPartId, bool bWithExtruder)
    {
        oW.WriteStartElement("part");
        oW.WriteAttributeString("id", StrInv(nPartId));
        oW.WriteAttributeString("subtype", "normal_part");
        WriteKv(oW, "name", oPart.StrName);
        WriteKv(oW, "matrix", "1 0 0 0 0 1 0 0 0 0 1 0 0 0 0 1");
        if (bWithExtruder)
        {
            WriteKv(oW, "extruder", StrInv(oPart.NFilament));
            foreach (KeyValuePair<string, string> oKv in
                     oPart.Overrides.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                WriteKv(oW, oKv.Key, oKv.Value);
        }
        oW.WriteEndElement();
    }

    private static string StrSafeName(string strName)
    {
        var oSb = new StringBuilder();
        foreach (char c in strName)
            oSb.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        return oSb.Length == 0 ? "part" : oSb.ToString();
    }

    private static void WriteModelOpen(XmlWriter oW)
    {
        oW.WriteStartElement("model", "http://schemas.microsoft.com/3dmanufacturing/core/2015/02");
        oW.WriteAttributeString("unit", "millimeter");
        oW.WriteAttributeString("xml", "lang", null, "en-US");
        oW.WriteAttributeString("xmlns", "BambuStudio", null, "http://schemas.bambulab.com/package/2021");
        oW.WriteAttributeString("xmlns", "p", null, "http://schemas.microsoft.com/3dmanufacturing/production/2015/06");
        oW.WriteAttributeString("requiredextensions", "p");
    }

    private static void WriteBbsVersionMeta(XmlWriter oW)
        => WriteMeta(oW, "BambuStudio:3mfVersion", "1");

    private static void WriteMeta(XmlWriter oW, string strName, string strValue)
    {
        oW.WriteStartElement("metadata");
        oW.WriteAttributeString("name", strName);
        oW.WriteString(strValue);
        oW.WriteEndElement();
    }

    private static void WriteKv(XmlWriter oW, string strKey, string strValue)
    {
        oW.WriteStartElement("metadata");
        oW.WriteAttributeString("key", strKey);
        oW.WriteAttributeString("value", strValue);
        oW.WriteEndElement();
    }

    private static void WriteRel(XmlWriter oW, string strTarget, string strId)
    {
        oW.WriteStartElement("Relationship");
        oW.WriteAttributeString("Target", strTarget);
        oW.WriteAttributeString("Id", strId);
        oW.WriteAttributeString("Type", "http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel");
        oW.WriteEndElement();
    }

    private static void WriteDefault(XmlWriter oW, string strExt, string strContentType)
    {
        oW.WriteStartElement("Default");
        oW.WriteAttributeString("Extension", strExt);
        oW.WriteAttributeString("ContentType", strContentType);
        oW.WriteEndElement();
    }

    private static void WriteXmlEntry(ZipArchive oZip, string strPath, Action<XmlWriter> oBody)
    {
        ZipArchiveEntry oEntry = oZip.CreateEntry(strPath, CompressionLevel.Optimal);
        using Stream oStream = oEntry.Open();
        using XmlWriter oW = XmlWriter.Create(oStream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = true,
        });
        oW.WriteStartDocument();
        oBody(oW);
        oW.WriteEndDocument();
    }

    private static void WriteRawEntry(ZipArchive oZip, string strPath, string strContent)
    {
        ZipArchiveEntry oEntry = oZip.CreateEntry(strPath, CompressionLevel.Optimal);
        using Stream oStream = oEntry.Open();
        byte[] ab = Encoding.UTF8.GetBytes(strContent);
        oStream.Write(ab, 0, ab.Length);
    }

    private static string StrInv(int n) => n.ToString(CultureInfo.InvariantCulture);
    private static string StrInv(float f) => f.ToString("R", CultureInfo.InvariantCulture);
}
