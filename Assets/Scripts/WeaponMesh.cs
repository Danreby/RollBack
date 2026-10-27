using System.Collections.Generic;
using System.IO;
using UnityEngine;

// Malha de uma arma gerada por Tools/AnimationImport/weapons.py (um .bytes simples: posições,
// normais, UVs e um submesh por material). A malha é montada ao carregar a cena, também no editor,
// e compartilhada entre armas iguais (as duas adagas usam a mesma).
//
// O espaço da malha é o "espaço da pegada": origem no centro do punho fechado, +Y do pomo para a
// ponta e X ao longo do fio; por isso todas as armas usam o mesmo encaixe na mão.
[ExecuteAlways]
[RequireComponent(typeof(MeshFilter))]
public class WeaponMesh : MonoBehaviour
{
    public TextAsset meshData;

    private static readonly Dictionary<TextAsset, Mesh> cache = new Dictionary<TextAsset, Mesh>();

    private void OnEnable()
    {
        Apply();
    }

    public void Apply()
    {
        Mesh mesh = Load(meshData);
        if (mesh != null)
            GetComponent<MeshFilter>().sharedMesh = mesh;
    }

    public static Mesh Load(TextAsset data)
    {
        if (data == null)
            return null;
        if (cache.TryGetValue(data, out Mesh cached) && cached != null)
            return cached;

        Mesh mesh;
        try
        {
            mesh = Read(data.bytes, data.name);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"WeaponMesh: '{data.name}' inválido ({e.Message}).", data);
            return null;
        }
        cache[data] = mesh;
        return mesh;
    }

    private static Mesh Read(byte[] bytes, string name)
    {
        using var reader = new BinaryReader(new MemoryStream(bytes));
        if (new string(reader.ReadChars(4)) != "WMSH" || reader.ReadInt32() != 1)
            throw new InvalidDataException("formato desconhecido");
        int vertexCount  = reader.ReadInt32();
        int submeshCount = reader.ReadInt32();

        var positions = new Vector3[vertexCount];
        var normals   = new Vector3[vertexCount];
        var uvs       = new Vector2[vertexCount];
        for (int i = 0; i < vertexCount; i++)
            positions[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        for (int i = 0; i < vertexCount; i++)
            normals[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        for (int i = 0; i < vertexCount; i++)
            uvs[i] = new Vector2(reader.ReadSingle(), reader.ReadSingle());

        var mesh = new Mesh { name = name, hideFlags = HideFlags.DontSave };
        if (vertexCount > 65535)
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.vertices     = positions;
        mesh.normals      = normals;
        mesh.uv           = uvs;
        mesh.subMeshCount = submeshCount;
        for (int s = 0; s < submeshCount; s++)
        {
            int count = reader.ReadInt32();
            var indices = new int[count];
            for (int i = 0; i < count; i++)
                indices[i] = reader.ReadInt32();
            mesh.SetTriangles(indices, s);
        }
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        return mesh;
    }
}
