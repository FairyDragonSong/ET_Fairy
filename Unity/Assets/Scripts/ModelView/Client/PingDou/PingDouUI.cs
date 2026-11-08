using System;
using UnityEngine;
using UnityEngine.UI;

namespace ET.Client
{
    /// <summary>Prefab authoring helpers. The editor bakes this hierarchy to an editable prefab.</summary>
    public static class PingDouUI
    {
        public static readonly Color Cream = new Color(1, .96f, .90f);
        public static readonly Color Ink = new Color(.29f, .18f, .16f);
        public static readonly Color Purple = new Color(.63f, .43f, .77f);
        public static GameObject Panel(Transform parent, string name, Vector2 min, Vector2 max, Vector2 offsetMin, Vector2 offsetMax, Color color, Sprite sprite = null)
        {
            var obj = new GameObject(name, typeof(RectTransform), typeof(Image));
            obj.layer = 5;
            obj.transform.SetParent(parent, false);
            var rect = obj.GetComponent<RectTransform>();
            rect.anchorMin = min;
            rect.anchorMax = max;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
            var img = obj.GetComponent<Image>();
            img.color = color;
            img.sprite = sprite;
            img.type = sprite != null ? Image.Type.Sliced : Image.Type.Simple;
            img.raycastTarget = false;
            return obj;
        }

        public static Text Label(Transform parent, string name, string value, int size, TextAnchor alignment, Font font, Color color, Vector2 min, Vector2 max)
        {
            var obj = new GameObject(name, typeof(RectTransform), typeof(Text));
            obj.layer = 5;
            obj.transform.SetParent(parent, false);
            var rect = obj.GetComponent<RectTransform>();
            rect.anchorMin = min;
            rect.anchorMax = max;
            rect.offsetMin = new Vector2(5, 0);
            rect.offsetMax = new Vector2(-5, 0);
            var text = obj.GetComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.text = value;
            text.color = color;
            text.alignment = alignment;
            text.supportRichText = true;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            return text;
        }

        private static Button Button(Transform parent, string name, string label, Vector2 min, Vector2 max, PingDouAssets assets, out Text text, bool primary = true)
        {
            var obj = Panel(parent, name, min, max, new Vector2(4, 4), new Vector2(-4, -4), primary ? Purple : Cream, assets.roundedPanel);
            var image = obj.GetComponent<Image>();
            image.raycastTarget = true;
            var button = obj.AddComponent<Button>();
            button.targetGraphic = image;
            var colors = button.colors;
            colors.highlightedColor = new Color(.97f, .92f, 1);
            colors.pressedColor = new Color(.82f, .74f, .91f);
            button.colors = colors;
            text = Label(obj.transform, "Label", label, 23, TextAnchor.MiddleCenter, assets.font, primary ? Color.white : Ink, Vector2.zero, Vector2.one);
            return button;
        }

        public static PingDouView Build(PingDouAssets assets)
        {
            var obj = new GameObject("PingDouGame", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(PingDouView));
            obj.layer = 5;
            var canvas = obj.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 200;
            var scaler = obj.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(720, 1280);
            scaler.matchWidthOrHeight = .5f;
            var view = obj.GetComponent<PingDouView>();
            view.assets = assets;
            var background = Panel(obj.transform, "Warm tabletop", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, new Color(.85f, .70f, .56f));
            for (int i = 0; i < 24; ++i)
            {
                float y = (float)i / 24;
                Panel(background.transform, "Wood grain", new Vector2(0, y), new Vector2(1, y), new Vector2(0, 0), new Vector2(0, 1), new Color(.94f, .82f, .69f, .38f));
            }

            var safe = new GameObject("SafeArea", typeof(RectTransform));
            safe.transform.SetParent(obj.transform, false);
            view.safeArea = safe.GetComponent<RectTransform>();
            view.safeArea.anchorMin = Vector2.zero;
            view.safeArea.anchorMax = Vector2.one;
            view.safeArea.offsetMin = view.safeArea.offsetMax = Vector2.zero;
            Transform p = safe.transform;
            var header = Panel(p, "Header", new Vector2(.025f, .93f), new Vector2(.975f, .99f), Vector2.zero, Vector2.zero, Cream, assets.roundedPanel);
            view.title = Label(header.transform, "Title", "草莓甜心", 30, TextAnchor.MiddleLeft, assets.font, Ink, new Vector2(.04f, .08f), new Vector2(.47f, .94f));
            view.progress = Label(header.transform, "Progress", "0 / 272", 22, TextAnchor.MiddleCenter, assets.font, Ink, new Vector2(.48f, .08f), new Vector2(.70f, .94f));
            var scorePanel = Panel(header.transform, "Score pill", new Vector2(.71f, .16f), new Vector2(.98f, .84f), Vector2.zero, Vector2.zero, Purple, assets.roundedPanel);
            view.score = Label(scorePanel.transform, "Score", "总分 0", 23, TextAnchor.MiddleCenter, assets.font, Color.white, Vector2.zero, Vector2.one);
            var previewPanel = Panel(p, "Preview card", new Vector2(.14f, .66f), new Vector2(.86f, .92f), Vector2.zero, Vector2.zero, Cream, assets.roundedPanel);
            var preview = Panel(previewPanel.transform, "Global view", new Vector2(.04f, .08f), new Vector2(.96f, .95f), Vector2.zero, Vector2.zero, Color.white);
            UnityEngine.Object.DestroyImmediate(preview.GetComponent<Image>());
            view.globalImage = preview.AddComponent<RawImage>();
            view.globalImage.raycastTarget = false;
            var frame = Panel(preview.transform, "Detail viewport frame", new Vector2(.2f, .2f), new Vector2(.8f, .8f), Vector2.zero, Vector2.zero, new Color(0, 0, 0, 0));
            view.viewportFrame = frame.GetComponent<RectTransform>();
            Border(frame.transform, Purple, 2.5f);
            Label(p, "Preview caption", "全 局 预 览", 17, TextAnchor.MiddleCenter, assets.font, Ink, new Vector2(.32f, .642f), new Vector2(.68f, .663f));
            view.album = Button(p, "Album", "图鉴", new Vector2(.015f, .855f), new Vector2(.135f, .912f), assets, out _, false);
            view.sound = Button(p, "Sound", "声音", new Vector2(.865f, .855f), new Vector2(.985f, .912f), assets, out view.soundLabel, false);
            view.restart = Button(p, "Restart", "重玩", new Vector2(.865f, .793f), new Vector2(.985f, .85f), assets, out _, false);
            Label(p, "Side note", "一颗颗\n拼出\n小美好", 18, TextAnchor.MiddleCenter, assets.font, Ink, new Vector2(.02f, .69f), new Vector2(.13f, .82f));
            Label(p, "Side stamp", "HAND\nMADE\n✦", 19, TextAnchor.MiddleCenter, assets.font, Ink, new Vector2(.875f, .68f), new Vector2(.98f, .78f));
            var toolbar = Panel(p, "Combo and camera controls", new Vector2(.015f, .572f), new Vector2(.985f, .64f), Vector2.zero, Vector2.zero, Cream, assets.roundedPanel);
            view.combo = Label(toolbar.transform, "Combo", "COMBO 0", 23, TextAnchor.MiddleLeft, assets.font, Ink, new Vector2(.035f, .36f), new Vector2(.4f, .96f));
            view.reward = Label(toolbar.transform, "Reward tier", "基础 +10", 17, TextAnchor.MiddleLeft, assets.font, Purple, new Vector2(.035f, .03f), new Vector2(.45f, .4f));
            view.minus = Button(toolbar.transform, "Zoom out", "−", new Vector2(.46f, .12f), new Vector2(.55f, .87f), assets, out _);
            view.zoomLabel = Label(toolbar.transform, "Zoom", "200%", 22, TextAnchor.MiddleCenter, assets.font, Ink, new Vector2(.55f, .12f), new Vector2(.70f, .87f));
            view.plus = Button(toolbar.transform, "Zoom in", "+", new Vector2(.7f, .12f), new Vector2(.79f, .87f), assets, out _);
            view.moveMode = Button(toolbar.transform, "Pan mode", "移动", new Vector2(.79f, .12f), new Vector2(.90f, .87f), assets, out _, false);
            view.resetView = Button(toolbar.transform, "Reset camera", "↺", new Vector2(.90f, .12f), new Vector2(.995f, .87f), assets, out _);
            var detailPanel = Panel(p, "Detail tray", new Vector2(.018f, .17f), new Vector2(.982f, .563f), Vector2.zero, Vector2.zero, new Color(.99f, .98f, .97f), assets.roundedPanel);
            var local = Panel(detailPanel.transform, "Detail 3D view", new Vector2(.018f, .025f), new Vector2(.982f, .976f), Vector2.zero, Vector2.zero, Color.white);
            UnityEngine.Object.DestroyImmediate(local.GetComponent<Image>());
            view.detailImage = local.AddComponent<RawImage>();
            view.detailImage.raycastTarget = true;
            view.modeLabel = Label(detailPanel.transform, "Mode", "铺豆模式", 18, TextAnchor.UpperLeft, assets.font, Ink, new Vector2(.04f, .90f), new Vector2(.27f, .96f));
            var feedbackPanel = Panel(p, "Feedback", new Vector2(.055f, .144f), new Vector2(.945f, .175f), Vector2.zero, Vector2.zero, Cream, assets.roundedPanel);
            view.feedback = Label(feedbackPanel.transform, "Placement feedback", "选中颜色的空槽，都会亮起紫圈", 20, TextAnchor.MiddleCenter, assets.font, Purple, Vector2.zero, Vector2.one);
            var colors = new GameObject("Color trays", typeof(RectTransform));
            colors.transform.SetParent(p, false);
            var colorsRect = colors.GetComponent<RectTransform>();
            colorsRect.anchorMin = new Vector2(.025f, .035f);
            colorsRect.anchorMax = new Vector2(.975f, .136f);
            colorsRect.offsetMin = colorsRect.offsetMax = Vector2.zero;
            view.colorsRoot = colors.transform;
            view.phaseAction = Button(p, "Phase action", "开始熨烫", new Vector2(.07f, .05f), new Vector2(.93f, .125f), assets, out view.phaseLabel);
            view.help = Label(p, "Input hint", "单指铺豆 · 双指移动 / 缩放", 17, TextAnchor.MiddleCenter, assets.font, Ink, new Vector2(.02f, .004f), new Vector2(.98f, .03f));
            view.modal = Panel(p, "Modal backdrop", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, new Color(.17f, .10f, .22f, .68f));
            view.modal.GetComponent<Image>().raycastTarget = true;
            var card = Panel(view.modal.transform, "Modal card", new Vector2(.08f, .14f), new Vector2(.92f, .86f), Vector2.zero, Vector2.zero, Cream, assets.roundedPanel);
            var body = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup));
            body.transform.SetParent(card.transform, false);
            var bodyRect = body.GetComponent<RectTransform>();
            bodyRect.anchorMin = new Vector2(.045f, .035f);
            bodyRect.anchorMax = new Vector2(.955f, .965f);
            bodyRect.offsetMin = bodyRect.offsetMax = Vector2.zero;
            var layout = body.GetComponent<VerticalLayoutGroup>();
            layout.spacing = 9;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            layout.childAlignment = TextAnchor.MiddleCenter;
            view.modalContent = body.transform;
            view.modal.SetActive(false);
            return view;
        }

        private static void Border(Transform parent, Color color, float width)
        {
            Panel(parent, "Left", Vector2.zero, new Vector2(0, 1), Vector2.zero, new Vector2(width, 0), color);
            Panel(parent, "Right", new Vector2(1, 0), Vector2.one, new Vector2(-width, 0), Vector2.zero, color);
            Panel(parent, "Bottom", Vector2.zero, new Vector2(1, 0), Vector2.zero, new Vector2(0, width), color);
            Panel(parent, "Top", new Vector2(0, 1), Vector2.one, new Vector2(0, -width), Vector2.zero, color);
        }

        public static void ModalLabel(Transform parent, string value, int size, PingDouAssets assets, int height)
        {
            var text = Label(parent, "Text", value, size, TextAnchor.MiddleCenter, assets.font, Ink, Vector2.zero, Vector2.one);
            text.gameObject.AddComponent<LayoutElement>().preferredHeight = height;
        }

        public static void ModalButton(Transform parent, string label, Action action, PingDouAssets assets, bool primary)
        {
            var b = Button(parent, "Action", label, Vector2.zero, Vector2.one, assets, out _, primary);
            b.gameObject.AddComponent<LayoutElement>().preferredHeight = 62;
            b.onClick.AddListener(() => action());
        }
    }
}
