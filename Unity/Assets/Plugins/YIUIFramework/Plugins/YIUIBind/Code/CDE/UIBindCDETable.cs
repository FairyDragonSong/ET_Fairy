//------------------------------------------------------------
// Author: 亦亦
// Mail: 379338943@qq.com
// Data: 2023年2月12日
//------------------------------------------------------------

using System;
using System.Collections.Generic;
using ET;
using ET.Client;
using Sirenix.OdinInspector;
using Sirenix.Serialization;

using UnityEngine;

namespace YIUIFramework
{
    //[DetailedInfoBox("UI CDE总表 点击展开详细介绍", @"李胜扬")]
    [Serializable]
    [LabelText("UI CDE总表")]
    [AddComponentMenu("YIUIBind/★★★★★UI CDE Table 总表★★★★★")]
    public sealed partial class UIBindCDETable : SerializedMonoBehaviour
    {
        // 配置字段需在编辑器与玩家程序集保持一致，避免 Unity 序列化布局校验失败。
        [LabelText("组件类型")]
        [OnValueChanged("OnValueChangedEUICodeType")]
        [ReadOnly]
        public EUICodeType UICodeType = EUICodeType.Common;

        [BoxGroup("配置", true, true)]
        [HideIf("UICodeType", EUICodeType.Common)]
        [LabelText("窗口选项")]
        [GUIColor(0, 1, 1)]
        [EnableIf("@UIOperationHelper.CommonShowIf()")]
        public EWindowOption WindowOption = EWindowOption.None;

        [ShowIf("UICodeType", EUICodeType.Panel)]
        [BoxGroup("配置", true, true)]
        [OnValueChanged("OnValueChangedEPanelLayer")]
        [GUIColor(0, 1, 1)]
        [EnableIf("@UIOperationHelper.CommonShowIf()")]
        public EPanelLayer PanelLayer = EPanelLayer.Panel;

        [ShowIf("UICodeType", EUICodeType.Panel)]
        [BoxGroup("配置", true, true)]
        [GUIColor(0, 1, 1)]
        [EnableIf("@UIOperationHelper.CommonShowIf()")]
        public EPanelOption PanelOption = EPanelOption.None;

        [ShowIf("UICodeType", EUICodeType.Panel)]
        [BoxGroup("配置", true, true)]
        [GUIColor(0, 1, 1)]
        [EnableIf("@UIOperationHelper.CommonShowIf()")]
        public EPanelStackOption PanelStackOption = EPanelStackOption.VisibleTween;

        [ShowIf("UICodeType", EUICodeType.View)]
        [BoxGroup("配置", true, true)]
        [GUIColor(0, 1, 1)]
        [EnableIf("@UIOperationHelper.CommonShowIf()")]
        public EViewWindowType ViewWindowType = EViewWindowType.View;

        [ShowIf("UICodeType", EUICodeType.View)]
        [BoxGroup("配置", true, true)]
        [GUIColor(0, 1, 1)]
        [EnableIf("@UIOperationHelper.CommonShowIf()")]
        public EViewStackOption ViewStackOption = EViewStackOption.VisibleTween;

        [ShowIf("ShowCachePanelTime", EUICodeType.Panel)]
        [BoxGroup("配置", true, true)]
        [GUIColor(0, 1, 1)]
        [LabelText("缓存时间")]
        [EnableIf("@UIOperationHelper.CommonShowIf()")]
        public float CachePanelTime = 10;

        [LabelText("同层级时 优先级高的在前面")]
        [ShowIf("UICodeType", EUICodeType.Panel)]
        [BoxGroup("配置", true, true)]
        [GUIColor(0, 1, 1)]
        [EnableIf("@UIOperationHelper.CommonShowIf()")]
        public int Priority;

        #if UNITY_EDITOR
        [InlineButton("AddComponentTable", "Add")]
        [EnableIf("@UIOperationHelper.CommonShowIf()")]
        #endif
        public UIBindComponentTable ComponentTable;

        #if UNITY_EDITOR
        [InlineButton("AddDataTable", "Add")]
        [EnableIf("@UIOperationHelper.CommonShowIf()")]
        #endif
        public UIBindDataTable DataTable;

        #if UNITY_EDITOR
        [InlineButton("AddEventTable", "Add")]
        [EnableIf("@UIOperationHelper.CommonShowIf()")]
        #endif
        public UIBindEventTable EventTable;

        [LabelText("UI包名")]
        [ReadOnly]
        public string PkgName;

        [LabelText("UI资源名")]
        [ReadOnly]
        public string ResName;

        #region 关联
        
        [OdinSerialize]
        [LabelText("编辑时所有公共组件")]
        [ReadOnly]
        [PropertyOrder(1000)] //生成UI类时使用
        #if UNITY_EDITOR
        [ShowIf("@UIOperationHelper.CommonShowIf()")]
        #endif
        internal List<UIBindCDETable> AllChildCdeTable = new List<UIBindCDETable>();

        [OdinSerialize]
        [NonSerialized]
        [ShowInInspector]
        [ReadOnly]
        [PropertyOrder(1000)]
        [LabelText("运行时所有公共组件")] //动态生成后的子类(公共组件) 运行时使用
        #if UNITY_EDITOR
        [HideIf("@UIOperationHelper.CommonShowIf()")]
        #endif
        private Dictionary<string, Entity> m_AllChildUIOwner = new Dictionary<string, Entity>();

        internal void AddUIOwner(string uiName, Entity uiBase)
        {
            if (this.m_AllChildUIOwner.ContainsKey(uiName))
            {
                Debug.LogError($"{name} 已存在 {uiName} 请检查为何重复添加 是否存在同名组件");
                return;
            }

            this.m_AllChildUIOwner.Add(uiName, uiBase);
        }

        public Entity FindUIOwner(string uiName)
        {
            if (!this.m_AllChildUIOwner.ContainsKey(uiName))
            {
                Debug.LogError($"{name} 不存在 {uiName} 请检查");
                return null;
            }

            return this.m_AllChildUIOwner[uiName];
        }

        public T FindUIOwner<T>(string uiName) where T : Entity
        {
            return (T)this.FindUIOwner(uiName);
        }

        #endregion
    }
}
