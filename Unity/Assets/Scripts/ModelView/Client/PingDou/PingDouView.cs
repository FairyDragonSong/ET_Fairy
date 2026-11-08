using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ET.Client
{
    /// <summary>Presentation only. ET's PingDouGameComponent ticks and disposes this view.</summary>
    public sealed class PingDouView : MonoBehaviour
    {
        public PingDouAssets assets;
        public RawImage globalImage, detailImage;
        public RectTransform safeArea, viewportFrame;
        public Text title, progress, score, combo, reward, feedback, modeLabel, zoomLabel, help;
        public Button minus, plus, resetView, moveMode, phaseAction, album, sound, restart;
        public Text phaseLabel, soundLabel;
        public Transform colorsRoot;
        public GameObject modal;
        public Transform modalContent;
        public PingDouSession Session { get; private set; }
        public PingDouBoardView Board { get; private set; }

        private readonly List<Text> colorLabels = new List<Text>();
        private readonly List<Image> colorBorders = new List<Image>();
        private char selected;
        private bool moving, pointerDown, gestureBlocked, pinching, dirtyUI;
        private Vector2 lastWorld, lastPinchCenter;
        private float lastPinchDistance, saveAge, feedbackAge;
        private float pendingPoints;
        private float revealDelay;
        private RawImage resultPreview;
        private AudioSource[] voices;
        private int voiceIndex;
        private PingDouPhase lastPhase;
        private PingDouRound boundRound;
        private Camera UICamera => GetComponent<Canvas>().rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : GetComponent<Canvas>().rootCanvas.worldCamera;

        public void Initialize(PingDouSession session)
        {
            Session = session;
            Board = new PingDouBoardView(assets, globalImage, detailImage);
            Board.Settled = Session.Commit;
            Session.Changed += OnChanged;
            Session.Placed += OnPlaced;
            voices = new AudioSource[8];
            for (int i = 0; i < voices.Length; ++i)
            {
                voices[i] = gameObject.AddComponent<AudioSource>();
                voices[i].playOnAwake = false;
            }

            var input = detailImage.gameObject.AddComponent<PingDouInputSurface>();
            input.Down = PointerDown;
            input.Dragged = PointerDrag;
            input.Released = () =>
            {
                pointerDown = false;
                gestureBlocked = false;
            };
            input.Scrolled = (amount, point) => Board.ZoomAt(Mathf.Pow(1.12f, amount), point, UICamera);
            minus.onClick.AddListener(() => Zoom(.8f));
            plus.onClick.AddListener(() => Zoom(1.25f));
            resetView.onClick.AddListener(() => Board.ResetView());
            moveMode.onClick.AddListener(() =>
            {
                moving = !moving;
                RefreshHUD();
            });
            phaseAction.onClick.AddListener(PhaseAction);
            album.onClick.AddListener(OpenAlbum);
            restart.onClick.AddListener(ConfirmRestart);
            sound.onClick.AddListener(() =>
            {
                Session.Store.Profile.muted = !Session.Store.Profile.muted;
                Session.Store.SaveProfile();
                Play(assets.tap, 1);
                RefreshHUD();
            });
            BindRound();
        }

        private void OnChanged()
        {
            dirtyUI = true;
        }

        private void OnPlaced(PingDouPlacement placement)
        {
            pendingPoints += placement.Points;
            Play(assets.tap, 1 + Mathf.Min(placement.Combo, 20) * .011f);
            if (placement.ColorCompleted)
            {
                var color = Array.Find(Session.Round.Pattern.colors, x => x.key[0] == Session.Round.Key(placement.Index));
                Board.FlashColor(color.key[0]);
                Toast(color.name + "，全部归位！  +50");
                Play(assets.complete, 1.15f);
            }
            else if (placement.Combo == 5)
                Toast("COMBO 5 · 节奏开始了");
            else if (placement.Combo == 10)
                Toast("COMBO 10 · 手感来了！");
            else if (placement.Combo == 20)
            {
                Toast("COMBO 20 · 顺手模式！");
                Play(assets.complete, 1.25f);
            }
        }

        private void Play(AudioClip clip, float pitch)
        {
            if (Session.Store.Profile.muted || clip == null)
                return;
            var voice = voices[voiceIndex++ % voices.Length];
            voice.pitch = pitch;
            voice.volume = .28f;
            voice.clip = clip;
            voice.Play();
        }

        private void Toast(string text)
        {
            feedback.text = text;
            feedbackAge = 2.4f;
        }

        private void Zoom(float factor) => Board.ZoomAt(factor, RectTransformUtility.WorldToScreenPoint(UICamera, detailImage.rectTransform.position), UICamera);
        private void BindRound()
        {
            boundRound = Session.Round;
            lastPhase = boundRound.Phase;
            selected = boundRound.Pattern.colors[0].key[0];
            foreach (var c in boundRound.Pattern.colors)
                if (boundRound.Remaining(c.key[0]) > 0)
                {
                    selected = c.key[0];
                    break;
                }

            Board.Bind(boundRound, selected);
            PingDouLifetime.ClearChildren(colorsRoot);
            colorLabels.Clear();
            colorBorders.Clear();
            int count = boundRound.Pattern.colors.Length;
            for (int i = 0; i < count; ++i)
            {
                var c = boundRound.Pattern.colors[i];
                int captured = i;
                float left = (float)i / count, right = (float)(i + 1) / count;
                var box = PingDouUI.Panel(colorsRoot, c.name, new Vector2(left, 0), new Vector2(right, 1), new Vector2(5, 0), new Vector2(-5, 0), PingDouUI.Cream, assets.roundedPanel);
                var border = box.GetComponent<Image>();
                var button = box.AddComponent<Button>();
                button.targetGraphic = border;
                button.onClick.AddListener(() => SelectColor(captured));
                ColorUtility.TryParseHtmlString(c.hex, out var rgb);
                // Render the tray contents as stylised ring beads, matching the real 3D palette.
                for (int b = 0; b < 7; ++b)
                {
                    var bead = PingDouUI.Panel(box.transform, "Tray bead", new Vector2(.15f + (b % 3) * .25f, .44f + (b / 3) * .16f), new Vector2(.15f + (b % 3) * .25f, .44f + (b / 3) * .16f), new Vector2(-13, -13), new Vector2(13, 13), rgb, assets.softCircle);
                    PingDouUI.Panel(bead.transform, "Hole", new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(-5, -5), new Vector2(5, 5), Color.Lerp(rgb, Color.black, .50f), assets.softCircle);
                }

                var label = PingDouUI.Label(box.transform, "Count", "", 18, TextAnchor.MiddleCenter, assets.font, PingDouUI.Ink, new Vector2(0, .04f), new Vector2(1, .36f));
                colorLabels.Add(label);
                colorBorders.Add(border);
            }

            modal.SetActive(false);
            dirtyUI = true;
            RefreshHUD();
        }

        private void SelectColor(int index)
        {
            if (Session.Round.Phase != PingDouPhase.Placing)
                return;
            selected = Session.Round.Pattern.colors[index].key[0];
            Board.Select(selected);
            RefreshHUD();
        }

        public void Tick(float dt)
        {
            if (Session == null)
                return;
            if (boundRound != Session.Round)
                BindRound();
            UpdateSafeArea();
            ProcessTouches();
            if (pointerDown && !gestureBlocked && !moving && Session.Round.Phase == PingDouPhase.Ironing && Input.touchCount < 2 && !modal.activeSelf)
                HeatAt(lastWorld, dt);
            Board.Tick(dt);
            if (dirtyUI)
            {
                dirtyUI = false;
                Board.Sync();
                RefreshHUD();
                if (Session.Round.Phase != lastPhase)
                {
                    lastPhase = Session.Round.Phase;
                    if (lastPhase == PingDouPhase.ReadyToIron)
                    {
                        Toast("每颗豆子都归位了 · 开始熨烫吧");
                        Session.Flush();
                    }

                    if (lastPhase == PingDouPhase.ReadyToReveal)
                    {
                        Toast("熨烫 100% · 揭纸见证成品");
                        Session.Flush();
                    }

                    if (lastPhase == PingDouPhase.Complete)
                        revealDelay = .9f;
                }
            }

            saveAge += dt;
            feedbackAge -= dt;
            if (feedbackAge <= 0)
            {
                if (pendingPoints > 0)
                {
                    feedback.text = "+" + pendingPoints.ToString("0") + "  ·  轻轻落下，满满成就";
                    pendingPoints = 0;
                    feedbackAge = .6f;
                }
                else
                    feedback.text = Session.Round.Phase == PingDouPhase.Placing ? "选中颜色的空槽，都会亮起紫圈" : "慢慢来，每一步都会保存";
            }

            if (saveAge >= 1)
            {
                saveAge = 0;
                Session.Flush();
            }

            if (revealDelay > 0)
            {
                revealDelay -= dt;
                if (revealDelay <= 0) OpenResult();
            }
            if (resultPreview != null) resultPreview.texture = globalImage.texture;

            zoomLabel.text = Mathf.RoundToInt(Board.Zoom * 100) + "%";
            UpdateViewportFrame();
            Board.Render();
        }

        private void UpdateSafeArea()
        {
            if (Screen.width <= 0 || Screen.height <= 0)
                return;
            Rect r = Screen.safeArea;
            safeArea.anchorMin = new Vector2(r.xMin / Screen.width, r.yMin / Screen.height);
            safeArea.anchorMax = new Vector2(r.xMax / Screen.width, r.yMax / Screen.height);
        }

        private void UpdateViewportFrame()
        {
            Rect bounds = Board.PreviewViewportBounds();
            viewportFrame.anchorMin = bounds.min;
            viewportFrame.anchorMax = bounds.max;
            viewportFrame.offsetMin = viewportFrame.offsetMax = Vector2.zero;
        }

        private void PointerDown(PointerEventData e)
        {
            if (modal.activeSelf || Input.touchCount >= 2)
                return;
            pointerDown = true;
            gestureBlocked = false;
            lastWorld = Board.WorldAt(e.position, UICamera);
            if (e.button == PointerEventData.InputButton.Right || e.button == PointerEventData.InputButton.Middle)
                gestureBlocked = true;
            if (!moving && !gestureBlocked)
                PaintSegment(lastWorld, lastWorld);
        }

        private void PointerDrag(PointerEventData e)
        {
            if (!pointerDown || pinching || modal.activeSelf || Input.touchCount >= 2)
                return;
            Vector2 world = Board.WorldAt(e.position, UICamera);
            if (moving || gestureBlocked)
                Board.Pan(lastWorld - world);
            else
                PaintSegment(lastWorld, world);
            lastWorld = Board.WorldAt(e.position, UICamera);
        }

        private void ProcessTouches()
        {
            if (Input.touchCount < 2)
            {
                if (pinching)
                {
                    pinching = false;
                    pointerDown = false;
                    gestureBlocked = true;
                }

                return;
            }

            Touch a = Input.GetTouch(0), b = Input.GetTouch(1);
            if (modal.activeSelf || !RectTransformUtility.RectangleContainsScreenPoint(detailImage.rectTransform, a.position, UICamera) || !RectTransformUtility.RectangleContainsScreenPoint(detailImage.rectTransform, b.position, UICamera))
                return;
            Vector2 midpoint = (a.position + b.position) * .5f;
            float distance = Vector2.Distance(a.position, b.position);
            if (pinching)
            {
                Board.Pan(Board.WorldAt(lastPinchCenter, UICamera) - Board.WorldAt(midpoint, UICamera));
                if (lastPinchDistance > 1)
                    Board.ZoomAt(distance / lastPinchDistance, midpoint, UICamera);
            }

            pinching = true;
            gestureBlocked = true;
            pointerDown = false;
            lastPinchCenter = midpoint;
            lastPinchDistance = distance;
        }

        private void PaintSegment(Vector2 from, Vector2 to)
        {
            if (Session.Round.Phase != PingDouPhase.Placing)
                return;
            // Sample faster than half a cell; a fast swipe cannot skip a crossed target.
            int steps = Mathf.Clamp(Mathf.CeilToInt(Vector2.Distance(from, to) * 3), 1, 384);
            for (int i = 0; i <= steps; ++i)
                Board.Queue(Board.IndexAt(Vector2.Lerp(from, to, (float)i / steps)), selected);
        }

        private void HeatAt(Vector2 world, float dt)
        {
            if (Session.Round.Phase != PingDouPhase.Ironing)
                return;
            int size = Session.Round.Pattern.size;
            for (int i = 0; i < size * size; ++i)
                if (Session.Round.IsPlaced(i) && Vector2.Distance(world, Board.Cell(i)) < 1.9f)
                    Session.Heat(i, dt * 2.5f);
            Board.ShowIron(world);
        }

        private void PhaseAction()
        {
            switch (Session.Round.Phase)
            {
                case PingDouPhase.ReadyToIron:
                    Session.BeginIroning();
                    break;
                case PingDouPhase.Ironing:
                    ShowDialog("收尾熨烫", "辅助完成剩余区域，保留作品与分数。\n本次最高获得两颗星，可重玩挑战三星。", "帮我收尾", () =>
                    {
                        Session.FinishAssisted();
                        modal.SetActive(false);
                    });
                    break;
                case PingDouPhase.ReadyToReveal:
                    Session.Reveal();
                    break;
                case PingDouPhase.Complete:
                    OpenResult();
                    break;
            }
        }

        private void RefreshHUD()
        {
            var r = Session.Round;
            title.text = r.Pattern.name;
            progress.text = r.Placed + " / " + r.Total;
            score.text = "总分 " + r.Score;
            combo.text = "COMBO  <size=40>" + r.Placed + "</size>";
            int bonus = r.Placed >= 20 ? 10 : r.Placed >= 10 ? 5 : r.Placed >= 5 ? 2 : 0;
            reward.text = "基础 +10  " + (bonus > 0 ? "连豆 +" + bonus : "慢慢也能续连");
            modeLabel.text = moving ? "移动视野" : "铺豆模式";
            soundLabel.text = Session.Store.Profile.muted ? "静音" : "声音";
            colorsRoot.gameObject.SetActive(r.Phase == PingDouPhase.Placing);
            phaseAction.gameObject.SetActive(r.Phase != PingDouPhase.Placing);
            phaseLabel.text = r.Phase == PingDouPhase.ReadyToIron ? "拼好了 · 开始熨烫" : r.Phase == PingDouPhase.Ironing ? "熨烫 " + r.HeatPercent + "%  ·  收尾熨烫" : r.Phase == PingDouPhase.ReadyToReveal ? "熨烫 100% · 揭纸，拿起作品" : "查看作品与结算";
            help.text = r.Phase == PingDouPhase.Placing ? "单指铺豆  ·  双指移动 / 缩放  ·  松手换色不断连" : "按住拖动熨斗  ·  紫圈是尚未完成的位置";
            for (int i = 0; i < colorLabels.Count; ++i)
            {
                var c = r.Pattern.colors[i];
                int left = r.Remaining(c.key[0]);
                colorLabels[i].text = c.name + "\n" + (left == 0 ? "✓ 已完成" : left + " 颗");
                colorBorders[i].color = c.key[0] == selected ? new Color(.85f, .73f, .95f) : PingDouUI.Cream;
            }
        }

        private void ClearModal(string heading)
        {
            pointerDown = false;
            Board.CommitPending();
            PingDouLifetime.ClearChildren(modalContent);
            resultPreview = null;
            modal.SetActive(true);
            PingDouUI.ModalLabel(modalContent, heading, 34, assets, 70);
        }

        private void ModalText(string text, int height = 60) => PingDouUI.ModalLabel(modalContent, text, 23, assets, height);
        private void ModalButton(string label, Action action, bool primary = true) => PingDouUI.ModalButton(modalContent, label, action, assets, primary);
        private void ShowDialog(string heading, string body, string action, Action callback)
        {
            ClearModal(heading);
            ModalText(body, 125);
            ModalButton(action, callback);
            ModalButton("返回", () => modal.SetActive(false), false);
        }

        private void ConfirmRestart() => ShowDialog("重新拼这一幅？", "当前作品的制作进度将重置。\n图鉴中已经获得的星星和纪录会保留。", "重新开始", () => Session.Open(Session.CurrentIndex, true));
        public void OpenAlbum()
        {
            ClearModal("我的拼豆图鉴");
            int collected = 0;
            foreach (var pattern in Session.Catalog)
            {
                var record = Session.Store.Profile.records.Find(x => x.id == pattern.id);
                if (record != null)
                    ++collected;
                int index = Array.IndexOf(Session.Catalog, pattern);
                ModalButton(pattern.name + " · " + pattern.theme + "   " + (record == null ? "等待收藏" : new string ('★', record.stars)), () => Session.Open(index, false), record != null);
            }

            ModalText("已收藏  " + collected + " / " + Session.Catalog.Length + "\n每幅作品都能收藏，重玩可提升星级。", 90);
            ModalButton("继续当前作品", () => modal.SetActive(false), false);
        }

        private void OpenResult()
        {
            var r = Session.Round;
            ClearModal("作品完成 · " + r.Pattern.name);
            var artwork = new GameObject("Finished artwork", typeof(RectTransform), typeof(LayoutElement));
            artwork.transform.SetParent(modalContent, false);
            artwork.GetComponent<LayoutElement>().preferredHeight = 180;
            var artworkImage = new GameObject("Artwork image", typeof(RectTransform), typeof(RawImage));
            artworkImage.transform.SetParent(artwork.transform, false);
            var artworkRect = artworkImage.GetComponent<RectTransform>();
            artworkRect.anchorMin = artworkRect.anchorMax = new Vector2(.5f, .5f);
            artworkRect.sizeDelta = new Vector2(180f * globalImage.texture.width / globalImage.texture.height, 180);
            resultPreview = artworkImage.GetComponent<RawImage>();
            resultPreview.texture = globalImage.texture;
            resultPreview.raycastTarget = false;
            ModalText(new string ('★', r.Stars) + new string ('☆', 3 - r.Stars), 50);
            ModalText("总分  " + r.Score, 50);
            ModalText("基础分  " + r.BaseScore + "    连豆分  " + r.ComboScore + "\n颜色完成奖励  " + r.ColorScore, 85);
            ModalText("✓ 作品完整     ✓ 连豆目标 " + r.ComboTarget + "\n" + (r.Assisted ? "辅助熨烫 · 可重玩挑战三星" : "熨烫评价 " + (r.HeatEfficiency >= .55f ? "优" : "良")), 85);
            int count = 0, total = 0;
            foreach (var pattern in Session.Catalog)
                if (pattern.theme == r.Pattern.theme)
                {
                    ++total;
                    if (Session.Store.Profile.records.Exists(record => record.id == pattern.id)) ++count;
                }
            ModalText(r.Pattern.theme + "图鉴  " + count + " / " + total, 50);
            string next = Session.Catalog[Session.NextIndex].name;
            ModalButton("继续拼 · " + next, () => Session.Next());
            ModalButton("查看图鉴", OpenAlbum, false);
            ModalButton("欣赏成品", () => modal.SetActive(false), false);
        }

        public void Suspend()
        {
            if (Session == null)
                return;
            pointerDown = false;
            Board.CommitPending();
            Session.Flush();
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused)
                Suspend();
        }

        private void OnApplicationFocus(bool focused)
        {
            if (!focused)
                Suspend();
        }

        private void OnDisable() => Suspend();
        public void Shutdown()
        {
            if (Session == null)
                return;
            Suspend();
            Session.Changed -= OnChanged;
            Session.Placed -= OnPlaced;
            Board.Dispose();
            Board = null;
            Session = null;
        }

        private void OnDestroy() => Shutdown();
    }
}
