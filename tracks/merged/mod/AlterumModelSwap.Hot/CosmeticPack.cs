using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// Minimal JSON reader. The hot assembly must not depend on anything that might be missing from
// the runtime directory, and the pack manifest only needs objects/arrays/strings/numbers/bools.
internal static class MiniJson
{
    internal static object Parse(string text)
    {
        int i = 0;
        var value = ParseValue(text, ref i);
        SkipWhitespace(text, ref i);
        if (i != text.Length) throw new FormatException($"trailing data at {i}");
        return value;
    }

    private static void SkipWhitespace(string s, ref int i)
    {
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
    }

    private static object ParseValue(string s, ref int i)
    {
        SkipWhitespace(s, ref i);
        if (i >= s.Length) throw new FormatException("unexpected end");
        char c = s[i];
        switch (c)
        {
            case '{': return ParseObject(s, ref i);
            case '[': return ParseArray(s, ref i);
            case '"': return ParseString(s, ref i);
            case 't': Expect(s, ref i, "true"); return true;
            case 'f': Expect(s, ref i, "false"); return false;
            case 'n': Expect(s, ref i, "null"); return null;
            default: return ParseNumber(s, ref i);
        }
    }

    private static void Expect(string s, ref int i, string token)
    {
        if (i + token.Length > s.Length || string.CompareOrdinal(s, i, token, 0, token.Length) != 0)
            throw new FormatException($"expected {token} at {i}");
        i += token.Length;
    }

    private static Dictionary<string, object> ParseObject(string s, ref int i)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        i++; // {
        SkipWhitespace(s, ref i);
        if (i < s.Length && s[i] == '}') { i++; return result; }
        while (true)
        {
            SkipWhitespace(s, ref i);
            string key = ParseString(s, ref i);
            SkipWhitespace(s, ref i);
            if (i >= s.Length || s[i] != ':') throw new FormatException($"expected ':' at {i}");
            i++;
            result[key] = ParseValue(s, ref i);
            SkipWhitespace(s, ref i);
            if (i >= s.Length) throw new FormatException("unterminated object");
            if (s[i] == ',') { i++; continue; }
            if (s[i] == '}') { i++; return result; }
            throw new FormatException($"expected ',' or '}}' at {i}");
        }
    }

    private static List<object> ParseArray(string s, ref int i)
    {
        var result = new List<object>();
        i++; // [
        SkipWhitespace(s, ref i);
        if (i < s.Length && s[i] == ']') { i++; return result; }
        while (true)
        {
            result.Add(ParseValue(s, ref i));
            SkipWhitespace(s, ref i);
            if (i >= s.Length) throw new FormatException("unterminated array");
            if (s[i] == ',') { i++; continue; }
            if (s[i] == ']') { i++; return result; }
            throw new FormatException($"expected ',' or ']' at {i}");
        }
    }

    private static string ParseString(string s, ref int i)
    {
        if (s[i] != '"') throw new FormatException($"expected string at {i}");
        i++;
        var sb = new StringBuilder();
        while (i < s.Length)
        {
            char c = s[i++];
            if (c == '"') return sb.ToString();
            if (c != '\\') { sb.Append(c); continue; }
            if (i >= s.Length) break;
            char e = s[i++];
            switch (e)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                    i += 4;
                    break;
                default: throw new FormatException($"bad escape \\{e}");
            }
        }
        throw new FormatException("unterminated string");
    }

    private static double ParseNumber(string s, ref int i)
    {
        int start = i;
        if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '-' || s[i] == '+')) i++;
        if (i == start) throw new FormatException($"bad number at {start}");
        return double.Parse(s.Substring(start, i - start), CultureInfo.InvariantCulture);
    }

    internal static float F(object o) => o == null ? 0f : Convert.ToSingle(o, CultureInfo.InvariantCulture);
    internal static int I(object o) => o == null ? 0 : Convert.ToInt32(o, CultureInfo.InvariantCulture);
    internal static string S(object o) => o as string;
    internal static bool B(object o) => o is bool b && b;
    internal static List<object> A(object o) => o as List<object>;
    internal static Dictionary<string, object> O(object o) => o as Dictionary<string, object>;
    internal static float[] F3(object o)
    {
        var a = A(o);
        return new[] { F(a[0]), F(a[1]), F(a[2]) };
    }
    internal static Vector3 V3(object o)
    {
        var a = F3(o);
        return new Vector3(a[0], a[1], a[2]);
    }
    internal static Quaternion Q(object o)
    {
        var a = A(o);
        return new Quaternion(F(a[0]), F(a[1]), F(a[2]), F(a[3]));
    }
}

// The extracted, engine-neutral cosmetic pack. Every buffer is a range inside `Bytes`; nothing
// here touches UnityEngine except the plain math structs used for node TRS values.
internal sealed class CosmeticPack
{
    internal sealed class Blob
    {
        public int Off, Len;
        public Blob(int off, int len) { Off = off; Len = len; }
    }

    internal sealed class TexDef
    {
        public string Name;
        public int W, H, Filter, WrapU, WrapV, SourceFormat;
        public Blob Data;
    }

    internal sealed class MatDef
    {
        public string Name;
        public Color BaseColor;
        public float Metallic, Smoothness;
        public bool Transparent, DoubleSided;
        public int BaseMap = -1, NormalMap = -1, MaskMap = -1;
    }

    internal sealed class SubMesh
    {
        public int IndexStart, IndexCount;
    }

    internal sealed class MeshDef
    {
        public string Name;
        public int VertexCount, IndexFormat;
        public bool HasNormals, HasTangents, HasUv;
        public Blob Verts, Norms, Tangs, Uvs, Indices;
        public SubMesh[] Subs;
        public Vector3 BoundsMin, BoundsMax;
    }

    internal sealed class NodeDef
    {
        public string Name;
        public bool Active;
        public Vector3 Pos;
        public Quaternion Rot;
        public Vector3 Scale;
        public int Mesh = -1;
        public int[] Materials = Array.Empty<int>();
        public NodeDef[] Children = Array.Empty<NodeDef>();
    }

    internal sealed class CosmeticDef
    {
        public string Id;
        public int Type;
        public int Icon = -1;
        // Authored scale of the prefab root (rootAuthoring.scale). MoreCompany multiplies it into
        // the runtime scale (transform.localScale *= 0.38f), it never replaces it, so the geometry
        // below this root is authored 1.2x bigger on four cosmetics. The runtime folds it into the
        // fitted root scale exactly once.
        public Vector3 RootScale = Vector3.one;
        public NodeDef[] Children = Array.Empty<NodeDef>();
    }

    // Source head box in *authoring* units (see extract_cosmetics.py: authoringUnitScale).
    internal sealed class HeadRef
    {
        public float Width, Top, CenterX, CenterZ;
        public float WidthPerNeckToHead, TopPerNeckToHead;
        public float AuthoringUnitScale = 1f;
        public Quaternion RotRelModel = Quaternion.identity;
    }

    public byte[] Bytes;
    public readonly Dictionary<int, TexDef> Textures = new();
    public readonly Dictionary<int, MatDef> Materials = new();
    public readonly Dictionary<int, MeshDef> Meshes = new();
    public readonly Dictionary<string, CosmeticDef> Cosmetics = new(StringComparer.Ordinal);
    public readonly HeadRef SourceHead = new();
    public string Generator = "";

    private static readonly byte[] Magic = { (byte)'A', (byte)'C', (byte)'P', (byte)'K', (byte)'1', 0, 0, 0 };

    internal static CosmeticPack Load(byte[] bytes)
    {
        if (bytes.Length < 16) throw new FormatException("pack too small");
        for (int i = 0; i < Magic.Length; i++)
            if (bytes[i] != Magic[i]) throw new FormatException("bad pack magic");
        int manifestLength = BitConverter.ToInt32(bytes, 8);
        if (manifestLength <= 0 || 12 + manifestLength > bytes.Length) throw new FormatException("bad manifest length");
        int blobStart = 12 + manifestLength;
        while (blobStart % 4 != 0) blobStart++;

        var pack = new CosmeticPack { Bytes = bytes };
        var manifest = MiniJson.O(MiniJson.Parse(Encoding.UTF8.GetString(bytes, 12, manifestLength)))
                       ?? throw new FormatException("manifest is not an object");

        pack.Generator = MiniJson.S(manifest.TryGetValue("generator", out var gen) ? gen : null) ?? "";
        if (manifest.TryGetValue("coordinate", out var coord) && MiniJson.O(coord) is { } co &&
            co.TryGetValue("headRotationRelativeToModelRoot", out var rr))
            pack.SourceHead.RotRelModel = MiniJson.Q(rr);

        if (manifest.TryGetValue("sourceHead", out var sh) && MiniJson.O(sh) is { } head)
        {
            pack.SourceHead.Width = MiniJson.F(head["width"]);
            pack.SourceHead.Top = MiniJson.F(head["top"]);
            pack.SourceHead.CenterX = MiniJson.F(head["centerX"]);
            pack.SourceHead.CenterZ = MiniJson.F(head["centerZ"]);
            if (head.TryGetValue("widthPerNeckToHead", out var wpr)) pack.SourceHead.WidthPerNeckToHead = MiniJson.F(wpr);
            if (head.TryGetValue("topPerNeckToHead", out var tpr)) pack.SourceHead.TopPerNeckToHead = MiniJson.F(tpr);
            if (head.TryGetValue("authoringUnitScale", out var aus)) pack.SourceHead.AuthoringUnitScale = MiniJson.F(aus);
        }

        foreach (var kv in MiniJson.O(manifest["textures"]))
        {
            var d = MiniJson.O(kv.Value);
            pack.Textures[int.Parse(kv.Key, CultureInfo.InvariantCulture)] = new TexDef
            {
                Name = MiniJson.S(d["name"]) ?? "",
                W = MiniJson.I(d["w"]),
                H = MiniJson.I(d["h"]),
                Filter = MiniJson.I(d["filter"]),
                WrapU = MiniJson.I(d["wrapU"]),
                WrapV = MiniJson.I(d["wrapV"]),
                SourceFormat = MiniJson.I(d["sourceFormat"]),
                Data = BlobAt(blobStart, d),
            };
        }

        foreach (var kv in MiniJson.O(manifest["materials"]))
        {
            var d = MiniJson.O(kv.Value);
            var c = MiniJson.A(d["baseColor"]);
            pack.Materials[int.Parse(kv.Key, CultureInfo.InvariantCulture)] = new MatDef
            {
                Name = MiniJson.S(d["name"]) ?? "",
                BaseColor = new Color(MiniJson.F(c[0]), MiniJson.F(c[1]), MiniJson.F(c[2]), MiniJson.F(c[3])),
                Metallic = MiniJson.F(d["metallic"]),
                Smoothness = MiniJson.F(d["smoothness"]),
                Transparent = MiniJson.B(d["transparent"]),
                DoubleSided = MiniJson.B(d["doubleSided"]),
                BaseMap = Optional(d, "baseMap"),
                NormalMap = Optional(d, "normalMap"),
                MaskMap = Optional(d, "maskMap"),
            };
        }

        foreach (var kv in MiniJson.O(manifest["meshes"]))
        {
            var d = MiniJson.O(kv.Value);
            var subs = MiniJson.A(d["subMeshes"]);
            var subList = new SubMesh[subs.Count];
            for (int i = 0; i < subs.Count; i++)
            {
                var sd = MiniJson.O(subs[i]);
                subList[i] = new SubMesh { IndexStart = MiniJson.I(sd["indexStart"]), IndexCount = MiniJson.I(sd["indexCount"]) };
            }
            pack.Meshes[int.Parse(kv.Key, CultureInfo.InvariantCulture)] = new MeshDef
            {
                Name = MiniJson.S(d["name"]) ?? "",
                VertexCount = MiniJson.I(d["vertexCount"]),
                IndexFormat = MiniJson.I(d["indexFormat"]),
                HasNormals = MiniJson.B(d["hasNormals"]),
                HasTangents = MiniJson.B(d["hasTangents"]),
                HasUv = MiniJson.B(d["hasUV0"]),
                Verts = BlobAt(blobStart, MiniJson.O(d["vertices"])),
                Norms = d.TryGetValue("normals", out var nrm) ? BlobAt(blobStart, MiniJson.O(nrm)) : null,
                Tangs = d.TryGetValue("tangents", out var tan) ? BlobAt(blobStart, MiniJson.O(tan)) : null,
                Uvs = d.TryGetValue("uv0", out var uv) ? BlobAt(blobStart, MiniJson.O(uv)) : null,
                Indices = BlobAt(blobStart, MiniJson.O(d["indices"])),
                Subs = subList,
                BoundsMin = MiniJson.V3(d["boundsMin"]),
                BoundsMax = MiniJson.V3(d["boundsMax"]),
            };
        }

        foreach (var entry in MiniJson.A(manifest["cosmetics"]))
        {
            var d = MiniJson.O(entry);
            var children = MiniJson.A(d["children"]);
            var nodes = new NodeDef[children.Count];
            for (int i = 0; i < children.Count; i++) nodes[i] = ParseNode(MiniJson.O(children[i]));
            var def = new CosmeticDef
            {
                Id = MiniJson.S(d["id"]) ?? throw new FormatException("cosmetic without id"),
                Type = MiniJson.I(d["type"]),
                Icon = Optional(d, "icon"),
                Children = nodes,
            };
            if (d.TryGetValue("rootAuthoring", out var root) && MiniJson.O(root) is { } rootObj &&
                rootObj.TryGetValue("scale", out var rootScale))
            {
                var scale = MiniJson.V3(rootScale);
                // A zero/NaN authored scale would collapse the instance; keep identity in that case
                // (the defect here is a missing factor, not a sanitizer).
                if (scale.x > 1e-4f && scale.y > 1e-4f && scale.z > 1e-4f) def.RootScale = scale;
            }
            pack.Cosmetics[def.Id] = def;
        }
        return pack;
    }

    private static int Optional(Dictionary<string, object> d, string key)
    {
        if (!d.TryGetValue(key, out var v) || v == null) return -1;
        if (v is double) return MiniJson.I(v);
        return -1;
    }

    private static NodeDef ParseNode(Dictionary<string, object> d)
    {
        var children = MiniJson.A(d["children"]);
        var kids = new NodeDef[children.Count];
        for (int i = 0; i < children.Count; i++) kids[i] = ParseNode(MiniJson.O(children[i]));
        var mats = MiniJson.A(d["materials"]);
        var matsOut = new int[mats.Count];
        for (int i = 0; i < mats.Count; i++) matsOut[i] = MiniJson.I(mats[i]);
        int mesh = -1;
        if (d.TryGetValue("mesh", out var mv) && mv is double) mesh = MiniJson.I(mv);
        return new NodeDef
        {
            Name = MiniJson.S(d["name"]) ?? "",
            Active = MiniJson.B(d["active"]),
            Pos = MiniJson.V3(d["pos"]),
            Rot = MiniJson.Q(d["rot"]),
            Scale = MiniJson.V3(d["scale"]),
            Mesh = mesh,
            Materials = matsOut,
            Children = kids,
        };
    }

    private static Blob BlobAt(int blobStart, Dictionary<string, object> d)
        => new Blob(blobStart + MiniJson.I(d["off"]), MiniJson.I(d["len"]));

    internal float F32(int offset) => BitConverter.ToSingle(Bytes, offset);
    internal int I32(int offset) => BitConverter.ToInt32(Bytes, offset);
}
