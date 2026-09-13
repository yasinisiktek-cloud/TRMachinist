using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;

namespace TRMachinist.Core;

public static class StlMeshReader
{
    public static TriangleMeshData Load(string path)
    {
        using var stream = File.OpenRead(path);
        return LooksBinary(stream) ? ReadBinary(stream) : ReadAscii(stream);
    }

    private static bool LooksBinary(FileStream stream)
    {
        if (stream.Length < 84) return false;
        Span<byte> header = stackalloc byte[84];
        stream.ReadExactly(header);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(header[80..84]);
        stream.Position = 0;
        return 84L + (50L * count) == stream.Length;
    }

    private static TriangleMeshData ReadBinary(Stream stream)
    {
        using var reader = new BinaryReader(stream);
        reader.ReadBytes(80);
        var count = reader.ReadUInt32();
        if (count > 20_000_000) throw new InvalidDataException("STL üçgen sayısı güvenli sınırı aşıyor.");
        var positions = new float[count * 9];
        var normals = new float[count * 9];
        var indices = new int[count * 3];
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);

        for (var triangle = 0; triangle < count; triangle++)
        {
            var normal = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            for (var vertex = 0; vertex < 3; vertex++)
            {
                var point = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                var p = ((int)triangle * 9) + (vertex * 3);
                positions[p] = point.X; positions[p + 1] = point.Y; positions[p + 2] = point.Z;
                normals[p] = normal.X; normals[p + 1] = normal.Y; normals[p + 2] = normal.Z;
                indices[((int)triangle * 3) + vertex] = ((int)triangle * 3) + vertex;
                min = Vector3.Min(min, point);
                max = Vector3.Max(max, point);
            }
            reader.ReadUInt16();
        }

        return new TriangleMeshData { Positions = positions, Normals = normals, Indices = indices, Bounds = new Bounds3(min, max) };
    }

    private static TriangleMeshData ReadAscii(Stream stream)
    {
        using var reader = new StreamReader(stream);
        var vertices = new List<Vector3>();
        var normalsPerVertex = new List<Vector3>();
        var currentNormal = Vector3.UnitZ;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var text = line.Trim();
            if (text.StartsWith("facet normal ", StringComparison.OrdinalIgnoreCase))
                currentNormal = ParseVector(text[13..]);
            else if (text.StartsWith("vertex ", StringComparison.OrdinalIgnoreCase))
            {
                vertices.Add(ParseVector(text[7..]));
                normalsPerVertex.Add(currentNormal);
            }
        }
        if (vertices.Count == 0 || vertices.Count % 3 != 0) throw new InvalidDataException("ASCII STL üçgen verisi geçersiz.");
        var positions = new float[vertices.Count * 3];
        var normals = new float[vertices.Count * 3];
        var indices = Enumerable.Range(0, vertices.Count).ToArray();
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        for (var i = 0; i < vertices.Count; i++)
        {
            var p = i * 3;
            positions[p] = vertices[i].X; positions[p + 1] = vertices[i].Y; positions[p + 2] = vertices[i].Z;
            normals[p] = normalsPerVertex[i].X; normals[p + 1] = normalsPerVertex[i].Y; normals[p + 2] = normalsPerVertex[i].Z;
            min = Vector3.Min(min, vertices[i]); max = Vector3.Max(max, vertices[i]);
        }
        return new TriangleMeshData { Positions = positions, Normals = normals, Indices = indices, Bounds = new Bounds3(min, max) };
    }

    private static Vector3 ParseVector(string value)
    {
        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) throw new InvalidDataException("STL vektörü geçersiz.");
        return new Vector3(
            float.Parse(parts[0], CultureInfo.InvariantCulture),
            float.Parse(parts[1], CultureInfo.InvariantCulture),
            float.Parse(parts[2], CultureInfo.InvariantCulture));
    }
}
