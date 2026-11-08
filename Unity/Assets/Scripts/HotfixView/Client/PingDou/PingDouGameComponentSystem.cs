using UnityEngine;

namespace ET.Client
{
    [EntitySystemOf(typeof(PingDouGameComponent))]
    [FriendOf(typeof(PingDouGameComponent))]
    public static partial class PingDouGameComponentSystem
    {
        [EntitySystem]
        private static void Awake(this PingDouGameComponent self)
        {
        }

        public static void Mount(this PingDouGameComponent self, Transform parent)
        {
            if (self.View != null)
                return;
            var prefab = Resources.Load<GameObject>("PingDou/PingDouGame");
            if (prefab == null)
                throw new System.InvalidOperationException("Generate PingDou assets using ET/PingDou/Rebuild assets first.");
            self.View = UnityEngine.Object.Instantiate(prefab, parent, false).GetComponent<PingDouView>();
            var rect = (RectTransform)self.View.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            var canvas = self.View.GetComponent<Canvas>().rootCanvas;
            var scaler = canvas.GetComponent<UnityEngine.UI.CanvasScaler>();
            scaler.referenceResolution = new Vector2(720, 1280);
            scaler.matchWidthOrHeight = .5f;
            self.Session = new PingDouSession(PingDouCatalog.Load(self.View.assets.catalog), new PingDouSaveStore());
            self.View.Initialize(self.Session);
        }

        [EntitySystem]
        private static void Update(this PingDouGameComponent self)
        {
            if (self.View != null && self.View.isActiveAndEnabled)
                self.View.Tick(Time.unscaledDeltaTime);
        }

        [EntitySystem]
        private static void Destroy(this PingDouGameComponent self)
        {
            if (self.View != null)
            {
                self.View.Shutdown();
                UnityEngine.Object.Destroy(self.View.gameObject);
            }

            self.View = null;
            self.Session = null;
        }
    }
}
