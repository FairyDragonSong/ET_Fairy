using System;
using ET.RedBlue;
using UnityEditor;
using UnityEngine;

namespace ET
{
    public static class RedBlueEditorTools
    {
        [MenuItem("ET/RedBlue/Enable Demo")]
        public static void Enable() => SetEnabled(true);
        [MenuItem("ET/RedBlue/Use Original Login")]
        public static void Disable() => SetEnabled(false);
        private static void SetEnabled(bool value)
        {
            GlobalConfig config = Resources.Load<GlobalConfig>("GlobalConfig");
            config.EnableRedBlueDemo = value; EditorUtility.SetDirty(config); AssetDatabase.SaveAssets();
        }
        [MenuItem("ET/RedBlue/Validate Demo")]
        public static void Validate()
        {
            Shader shader = Resources.Load<Shader>("RedBlue/Crowd");
            if (shader == null || ShaderUtil.ShaderHasError(shader)) throw new InvalidOperationException("RedBlue shader failed import");
            const string materialPath = "Assets/Resources/RedBlue/CrowdMaterial.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                material = new Material(shader) { enableInstancing = true };
                AssetDatabase.CreateAsset(material, materialPath);
                AssetDatabase.SaveAssets();
            }
            var simulation = new CrowdSimulation(50000, 3) { ZeroDamage = true };
            for (int i = 0; i < 60; i++) simulation.Step();
            if (simulation.Alive[0] + simulation.Alive[1] != 50000) throw new InvalidOperationException("RedBlue sustained population check failed");
            Debug.Log("REDBLUE_VALIDATION_OK: shader imported; 50,000 independent units, 60 fixed steps; zero damage preserved population.");
        }
    }
}
