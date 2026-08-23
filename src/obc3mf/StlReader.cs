using System.Globalization;
using System.Text;

namespace Obc3mf;

/// <summary>Indexed triangle mesh in millimetres. Vertices are welded on
/// bitwise-equal coordinates only — no tolerance parameter is smuggled in here;
/// meshes needing tolerant repair are upstream work, not silent fixing.</summary>
public sealed class IndexedMesh
{
    public required IReadOnlyList<(float X, float Y, float Z)> Vertices { get; init; }
    public required IReadOnlyList<(int A, int B, int C)> Triangles { get; init; }

    /// <summary>Closed 2-manifold check: every undirected edge used exactly twice.
    /// Returns the number of violating edges (0 = closed).</summary>
    public int NOpenEdges()
    {
        Dictionary<(int, int), int> oEdgeUse = new();
        foreach ((int a, int b, int c) in Triangles)
        {
            foreach ((int u, int v) in new[] { (a, b), (b, c), (c, a) })
            {
                (int, int) oKey = u < v ? (u, v) : (v, u);
                oEdgeUse[oKey] = oEdgeUse.GetValueOrDefault(oKey) + 1;
            }
        }
        return oEdgeUse.Values.Count(n => n != 2);
    }
}

/// <summary>Binary and ASCII STL. STL carries no units; per MM-3MF.md the
/// contract is millimetres, declared by the manifest, never inferred.</summary>
public static class StlReader
{
    public static IndexedMesh Read(string strPath)
    {
        byte[] abData = File.ReadAllBytes(strPath);
        if (abData.Length < 15)
            throw new InvalidDataException($"{strPath}: too small to be an STL.");

        // Binary STL: 80-byte header + uint32 count + 50 bytes/triangle, exact length.
        if (abData.Length >= 84)
        {
            uint nCount = BitConverter.ToUInt32(abData, 80);
            if (abData.Length == 84 + (long)nCount * 50)
                return Weld(EnumerateBinary(abData, nCount));
        }
        string strHead = Encoding.ASCII.GetString(abData, 0, Math.Min(abData.Length, 6));
        if (strHead.StartsWith("solid", StringComparison.OrdinalIgnoreCase))
            return Weld(EnumerateAscii(abData, strPath));
        throw new InvalidDataException(
            $"{strPath}: neither a well-formed binary STL (length mismatch) nor ASCII STL.");
    }

    private static IEnumerable<(float, float, float)> EnumerateBinary(byte[] abData, uint nCount)
    {
        for (uint i = 0; i < nCount; i++)
        {
            int nBase = 84 + (int)(i * 50) + 12; // skip normal
            for (int v = 0; v < 3; v++)
            {
                int n = nBase + v * 12;
                yield return (BitConverter.ToSingle(abData, n),
                    BitConverter.ToSingle(abData, n + 4), BitConverter.ToSingle(abData, n + 8));
            }
        }
    }

    private static IEnumerable<(float, float, float)> EnumerateAscii(byte[] abData, string strPath)
    {
        using var oReader = new StreamReader(new MemoryStream(abData), Encoding.ASCII);
        string? strLine;
        var aoVertex = new List<(float, float, float)>(3);
        while ((strLine = oReader.ReadLine()) is not null)
        {
            string[] astr = strLine.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (astr.Length == 4 && astr[0] == "vertex")
            {
                aoVertex.Add((
                    float.Parse(astr[1], CultureInfo.InvariantCulture),
                    float.Parse(astr[2], CultureInfo.InvariantCulture),
                    float.Parse(astr[3], CultureInfo.InvariantCulture)));
                if (aoVertex.Count == 3)
                {
                    foreach (var o in aoVertex)
                        yield return o;
                    aoVertex.Clear();
                }
            }
        }
        if (aoVertex.Count != 0)
            throw new InvalidDataException($"{strPath}: ASCII STL ends mid-facet.");
    }

    private static IndexedMesh Weld(IEnumerable<(float X, float Y, float Z)> oCorners)
    {
        var oIndex = new Dictionary<(float, float, float), int>();
        var aoVertices = new List<(float, float, float)>();
        var aoTriangles = new List<(int, int, int)>();
        var anTri = new int[3];
        int nCorner = 0;
        foreach ((float, float, float) oV in oCorners)
        {
            if (!oIndex.TryGetValue(oV, out int nId))
            {
                nId = aoVertices.Count;
                aoVertices.Add(oV);
                oIndex[oV] = nId;
            }
            anTri[nCorner++] = nId;
            if (nCorner == 3)
            {
                nCorner = 0;
                // Degenerate (repeated-vertex) triangles are dropped; they carry no surface.
                if (anTri[0] != anTri[1] && anTri[1] != anTri[2] && anTri[2] != anTri[0])
                    aoTriangles.Add((anTri[0], anTri[1], anTri[2]));
            }
        }
        if (aoTriangles.Count == 0)
            throw new InvalidDataException("Mesh has no non-degenerate triangles.");
        return new IndexedMesh { Vertices = aoVertices, Triangles = aoTriangles };
    }
}
