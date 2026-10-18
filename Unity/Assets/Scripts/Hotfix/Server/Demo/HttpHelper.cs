using System.Net;
using System.Text;

namespace ET.Server
{
    public static partial class HttpHelper
    {
        // 写入 JSON HTTP 响应并设置状态码。
        public static void Response(HttpListenerContext context, object response, bool setSuccessStatus = true)
        {
            if (setSuccessStatus)
            {
                context.Response.StatusCode = 200;
            }
            TextResponse(context, MongoHelper.ToJson(response), "application/json; charset=utf-8");
        }

        // 写入指定内容类型的文本响应。
        public static void TextResponse(HttpListenerContext context, string response, string contentType)
        {
            byte[] bytes = response.ToUtf8();
            context.Response.ContentType = contentType;
            context.Response.ContentEncoding = Encoding.UTF8;
            context.Response.ContentLength64 = bytes.Length;
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }
    }
}
