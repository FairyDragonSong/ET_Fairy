using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEventSystem = UnityEngine.EventSystems.EventSystem;

namespace ET.Client
{
    [EntitySystemOf(typeof(OperaComponent))]
    [FriendOf(typeof(OperaComponent))]
    public static partial class OperaComponentSystem
    {
        [EntitySystem]
        private static void Awake(this OperaComponent self)
        {
            self.mapMask = LayerMask.GetMask("Map");
        }

        [EntitySystem]
        private static void Update(this OperaComponent self)
        {
            bool rightClick = Input.GetMouseButtonDown(1);
            bool leftClick = Input.GetMouseButtonDown(0);
            // 微信小游戏的 UI 触摸走 inputOverride，不一定出现在 UnityEngine.Input 中。
            BaseInput touchInput = UnityEventSystem.current?.currentInputModule?.inputOverride;
            bool overrideTouchBegan = touchInput != null && touchInput.touchCount > 0
                    && touchInput.GetTouch(0).phase == TouchPhase.Began;
            bool unityTouchBegan = Input.touchCount > 0 && Input.GetTouch(0).phase == TouchPhase.Began;
            bool touchBegan = overrideTouchBegan || unityTouchBegan;
            if (rightClick || leftClick || touchBegan)
            {
                Vector3 screenPosition = overrideTouchBegan ? touchInput.GetTouch(0).position
                        : unityTouchBegan ? Input.GetTouch(0).position : Input.mousePosition;
                bool blocked = !rightClick && IsBlockingUI(screenPosition);
                if (!blocked)
                {
                    Camera camera = Camera.main;
                    RaycastHit hit = default;
                    bool mapHit = camera != null && Physics.Raycast(camera.ScreenPointToRay(screenPosition), out hit, 1000, self.mapMask);
                    if (mapHit)
                    {
                        C2M_PathfindingResult c2MPathfindingResult = C2M_PathfindingResult.Create();
                        c2MPathfindingResult.Position = hit.point;
                        self.Root().GetComponent<ClientSenderComponent>().Send(c2MPathfindingResult);
                    }
                }
            }
            
            if (Input.GetKeyDown(KeyCode.Q))
            {
                self.Test1().Coroutine();
            }
                
            if (Input.GetKeyDown(KeyCode.W))
            {
                self.Test2().Coroutine();
            }

            if (Input.GetKeyDown(KeyCode.R))
            {
                CodeLoader.Instance.Reload();
                return;
            }

            if (Input.GetKeyDown(KeyCode.T))
            {
                C2M_TransferMap c2MTransferMap = C2M_TransferMap.Create();
                self.Root().GetComponent<ClientSenderComponent>().Call(c2MTransferMap).Coroutine();
            }
        }

        private static bool IsBlockingUI(Vector2 screenPosition)
        {
            UnityEventSystem eventSystem = UnityEventSystem.current;
            if (eventSystem == null)
            {
                return false;
            }

            PointerEventData pointer = new(eventSystem) { position = screenPosition };
            List<RaycastResult> hits = new();
            eventSystem.RaycastAll(pointer, hits);
            if (hits.Count == 0)
            {
                return false;
            }

            // 地图帮助面板的全屏底板允许点击穿透，其他 UI 继续拦截移动。
            Transform hit = hits[0].gameObject.transform;
            return hit.name != "UIBlockBG" || hit.parent == null
                    || (hit.parent.name != "MainPanel" && hit.parent.name != "MainPanel(Clone)");
        }
        
        private static async ETTask Test1(this OperaComponent self)
        {
            Log.Debug($"Croutine 1 start1 ");
            using (await self.Root().GetComponent<CoroutineLockComponent>().Wait(1, 20000, 3000))
            {
                await self.Root().GetComponent<TimerComponent>().WaitAsync(6000);
            }

            Log.Debug($"Croutine 1 end1");
        }
            
        private static async ETTask Test2(this OperaComponent self)
        {
            Log.Debug($"Croutine 2 start2");
            using (await self.Root().GetComponent<CoroutineLockComponent>().Wait(1, 20000, 3000))
            {
                await self.Root().GetComponent<TimerComponent>().WaitAsync(1000);
            }
            Log.Debug($"Croutine 2 end2");
        }
    }
}
