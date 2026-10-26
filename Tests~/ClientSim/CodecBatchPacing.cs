#if UNITY_EDITOR
using System;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEngine;

// Batch Editors have no Game view to pace frames or return to Edit mode after a fixture run.
[DefaultExecutionOrder(32000)]
public class CodecBatchPacing : MonoBehaviour
{
    public string root;
    double previousFrame;
    void Start()
    {
        previousFrame = Time.realtimeSinceStartupAsDouble;
    }
    void LateUpdate()
    {
        if (!Application.isBatchMode) return;
        if (File.Exists(root + "/done.txt") || File.Exists(root + "/error.txt"))
        {
            EditorApplication.isPlaying = false;
            return;
        }
        double remaining = 1.0 / 60 - (Time.realtimeSinceStartupAsDouble - previousFrame);
        if (remaining > 0) Thread.Sleep((int)Math.Ceiling(remaining * 1000));
        previousFrame = Time.realtimeSinceStartupAsDouble;
    }
}
#endif
