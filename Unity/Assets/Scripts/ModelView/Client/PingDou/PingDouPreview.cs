using UnityEngine;

namespace ET.Client
{
    /// <summary>Art and input sandbox only. Production entry uses ET and YIUI, never this MonoBehaviour.</summary>
    public sealed class PingDouPreview : MonoBehaviour
    {
        public PingDouView view;
        private void Start()
        {
            var instance = Instantiate(view);
            view = instance;
            view.Initialize(new PingDouSession(PingDouCatalog.Load(view.assets.catalog), new PingDouSaveStore()));
        }

        private void Update()
        {
            if (view != null && view.Session != null)
                view.Tick(Time.unscaledDeltaTime);
        }
    }
}
