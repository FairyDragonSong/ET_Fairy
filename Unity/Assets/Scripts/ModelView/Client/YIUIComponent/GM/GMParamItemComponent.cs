using System.Collections.Generic;
using TMPro;
using YIUIFramework;

namespace ET.Client
{
    public partial class GMParamItemComponent: Entity
    {
        public GMParamInfo                   ParamInfo;
        [System.NonSerialized]
        public List<TMP_Dropdown.OptionData> OptionList;
        public Dictionary<string, string>    OptionDic;
    }
}
