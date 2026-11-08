using System;
using UnityEngine;

namespace ET.Client
{
    [CreateAssetMenu(menuName = "PingDou/Assets")]
    public sealed class PingDouAssets : ScriptableObject
    {
        public TextAsset catalog;
        public Font font;
        public Material plastic;
        public Material flat;
        public Material translucent;
        public Mesh[] beadHeatStages;
        public Mesh peg;
        public Mesh ring;
        public Mesh targetFill;
        public Mesh pen;
        public Mesh iron;
        public AudioClip tap;
        public AudioClip complete;
        public Sprite roundedPanel;
        public Sprite softCircle;
    }

    [Serializable]
    public sealed class PingDouCatalog
    {
        public PingDouPattern[] patterns;
        public static PingDouPattern[] Load(TextAsset asset)
        {
            var data = JsonUtility.FromJson<PingDouCatalog>(asset.text);
            if (data?.patterns == null || data.patterns.Length == 0)
                throw new InvalidOperationException("PingDou catalog is empty.");
            foreach (var pattern in data.patterns)
                pattern.Validate();
            return data.patterns;
        }
    }
}
