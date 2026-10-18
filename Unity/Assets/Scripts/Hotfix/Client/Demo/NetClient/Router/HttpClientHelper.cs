using System;
using System.IO;
using System.Net.Http;
using System.Threading;

namespace ET.Client
{
    public static partial class HttpClientHelper
    {
        public static async ETTask<string> Get(string link)
        {
            // F6 编译小游戏热更 DLL 时使用运行时网络请求实现。
#if ET_MINIGAME_HOTFIX || ((UNITY_WEBGL || UNITY_WEIXINMINIGAME || TUANJIE_WEIXINMINIGAME) && !UNITY_EDITOR)
            return await CoroutineHelper.HttpGet(link);
#else
            try
            {
                using HttpClient httpClient = new();
                HttpResponseMessage response =  await httpClient.GetAsync(link);
                string result = await response.Content.ReadAsStringAsync();
                return result;
            }
            catch (Exception e)
            {
                throw new Exception($"http request fail: {link.Substring(0,link.IndexOf('?'))}\n{e}");
            }
#endif
        }
    }
}
