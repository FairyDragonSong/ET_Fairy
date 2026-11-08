using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ET.Client
{
    /// <summary>UI surface captures drag starts, so dragging a palette/button never paints the board.</summary>
    public sealed class PingDouInputSurface : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IDragHandler, IScrollHandler
    {
        public Action<PointerEventData> Down, Dragged;
        public Action Released;
        public Action<float, Vector2> Scrolled;
        public void OnPointerDown(PointerEventData e) => Down?.Invoke(e);
        public void OnDrag(PointerEventData e) => Dragged?.Invoke(e);
        public void OnPointerUp(PointerEventData e) => Released?.Invoke();
        public void OnScroll(PointerEventData e) => Scrolled?.Invoke(e.scrollDelta.y, e.position);
        private void OnDisable() => Released?.Invoke();
    }
}
