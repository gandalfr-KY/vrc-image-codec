#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UdonSharp;
using VrcImageCodec;

namespace VrcAvif.Encoder.Editor
{
// Editor configuration must not resize or rewrite another codec's generated assets.
internal static class CodecAssetOwnership
{
    public static void DetachSharedAssets(UdonSharpBehaviour component, string directory)
    {
        var peers = Resources.FindObjectsOfTypeAll(component.GetType());
        var prefabSource = PrefabUtility.GetCorrespondingObjectFromSource(component);
        foreach (var field in component.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var resource = field.GetValue(component) as UnityEngine.Object;
            bool mutable =
                resource is Material || resource is RenderTexture ||
                component is AvifGpuEntropy && field.Name == nameof(AvifGpuEntropy.containerHeaders);
            if (!mutable || !EditorUtility.IsPersistent(resource)) continue;
            if ((!prefabSource || field.GetValue(prefabSource) as UnityEngine.Object != resource) &&
                !peers.Any(peer =>
                               peer != component && field.GetValue(peer) as UnityEngine.Object == resource))
                continue;
            Directory.CreateDirectory(directory);
            string source = AssetDatabase.GetAssetPath(resource);
            string destination =
                AssetDatabase.GenerateUniqueAssetPath(directory + "/" + Path.GetFileName(source));
            if (!AssetDatabase.CopyAsset(source, destination))
                throw new InvalidOperationException("Could not create an independent codec asset: " + source);
            Undo.RecordObject(component, "Separate codec resources");
            field.SetValue(component, AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(destination));
            EditorUtility.SetDirty(component);
        }
    }
}
}
#endif
