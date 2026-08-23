using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Obc3mf;

/// <summary>Writes the archive per docs/specs/MM-3MF.md: 3MF core spec geometry
/// plus Metadata/model_settings.config carrying per-object filament (extruder)
/// assignments and overrides, in the shape the fork's importer reads
/// (bbs_3mf.cpp: BBS_MODEL_CONFIG_FILE, _handle_start_config_object/_metadata).</summary>
public static class ThreeMfWriter
{
    public sealed record Part(
        string StrName,
        IndexedMesh oMesh,
        int NFilament,
        IReadOnlyDictionary<string, string> Overrides);

    public static void Write(string strOutputPath, IReadOnlyList<Part> aParts)
    {
        using FileStream oFile = File.Create(strOutputPath);
        using var oZip = new ZipArchive(oFile, ZipArchiveMode.Create);

        WriteEntry(oZip, "[Content_Types].xml", oW =>
        {
            oW.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
            WriteDefault(oW, "rels", "application/vnd.openxmlformats-package.relationships+xml");
            WriteDefault(oW, "model", "application/vnd.ms-package.3dmanufacturing-3dmodel+xml");
            oW.WriteEndElement();
        });

        WriteEntry(oZip, "_rels/.rels", oW =>
        {
            oW.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
            oW.WriteStartElement("Relationship");
            oW.WriteAttributeString("Target", "/3D/3dmodel.model");
            oW.WriteAttributeString("Id", "rel-1");
            oW.WriteAttributeString("Type",
                "http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel");
            oW.WriteEndElement();
            oW.WriteEndElement();
        });

        WriteEntry(oZip, "3D/3dmodel.model", oW =>
        {
            oW.WriteStartElement("model", "http://schemas.microsoft.com/3dmanufacturing/core/2015/02");
            oW.WriteAttributeString("unit", "millimeter");
            oW.WriteAttributeString("xml", "lang", null, "en-US");

            oW.WriteStartElement("resources");
            for (int i = 0; i < aParts.Count; i++)
            {
                IndexedMesh oMesh = aParts[i].oMesh;
                oW.WriteStartElement("object");
                oW.WriteAttributeString("id", StrInv(i + 1));
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
            }
            oW.WriteEndElement(); // resources

            oW.WriteStartElement("build");
            for (int i = 0; i < aParts.Count; i++)
            {
                oW.WriteStartElement("item");
                oW.WriteAttributeString("objectid", StrInv(i + 1));
                oW.WriteAttributeString("transform", "1 0 0 0 1 0 0 0 1 0 0 0");
                oW.WriteAttributeString("printable", "1");
                oW.WriteEndElement();
            }
            oW.WriteEndElement(); // build

            oW.WriteEndElement(); // model
        });

        WriteEntry(oZip, "Metadata/model_settings.config", oW =>
        {
            oW.WriteStartElement("config");
            for (int i = 0; i < aParts.Count; i++)
            {
                Part oPart = aParts[i];
                oW.WriteStartElement("object");
                oW.WriteAttributeString("id", StrInv(i + 1));
                WriteMeta(oW, "name", oPart.StrName);
                WriteMeta(oW, "extruder", StrInv(oPart.NFilament));
                foreach (KeyValuePair<string, string> oKv in
                         oPart.Overrides.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    WriteMeta(oW, oKv.Key, oKv.Value);
                oW.WriteEndElement();
            }
            oW.WriteEndElement();
        });
    }

    private static void WriteDefault(XmlWriter oW, string strExt, string strContentType)
    {
        oW.WriteStartElement("Default");
        oW.WriteAttributeString("Extension", strExt);
        oW.WriteAttributeString("ContentType", strContentType);
        oW.WriteEndElement();
    }

    private static void WriteMeta(XmlWriter oW, string strKey, string strValue)
    {
        oW.WriteStartElement("metadata");
        oW.WriteAttributeString("key", strKey);
        oW.WriteAttributeString("value", strValue);
        oW.WriteEndElement();
    }

    private static void WriteEntry(ZipArchive oZip, string strPath, Action<XmlWriter> oBody)
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

    private static string StrInv(int n) => n.ToString(CultureInfo.InvariantCulture);
    private static string StrInv(float f) => f.ToString("R", CultureInfo.InvariantCulture);
}
