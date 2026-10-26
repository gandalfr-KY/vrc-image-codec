#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace VrcAvif.Encoder.Editor
{
internal static class CodecMaterialSetup
{
    // Udon cannot construct Materials. Renderer.material(s) provides owned runtime instances.
    public static Renderer Configure(Renderer source, Transform parent, Material[] templates)
    {
        if (!source)
        {
            var root = new GameObject("Codec material instances");
            Undo.RegisterCreatedObjectUndo(root, "Create codec material source");
            root.transform.SetParent(parent, false);
            root.hideFlags = HideFlags.HideInHierarchy;
            source = root.AddComponent<MeshRenderer>();
        }
        source.enabled = false;
        source.sharedMaterials = templates;
        EditorUtility.SetDirty(source);
        return source;
    }
}
}
#endif
