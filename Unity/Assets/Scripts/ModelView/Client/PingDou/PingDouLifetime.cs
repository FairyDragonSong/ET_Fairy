using System.Collections.Generic;
using UnityEngine;

namespace ET.Client
{
    public static class PingDouLifetime
    {
        public static void Release(UnityEngine.Object value)
        {
            if (value == null)
                return;
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(value);
            else
                UnityEngine.Object.DestroyImmediate(value);
        }

        public static void ClearChildren(Transform parent)
        {
            var children = new List<GameObject>();
            foreach (Transform child in parent)
                children.Add(child.gameObject);
            foreach (var child in children)
            {
                child.SetActive(false);
                Release(child);
            }
        }
    }
}
