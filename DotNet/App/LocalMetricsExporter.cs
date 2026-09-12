using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace ET;

// Local collector only: no wildcard binding, remote transport, or management actions.
internal sealed class LocalMetricsExporter : IDisposable
{
    private readonly HttpListener listener=new();
    private readonly byte[] tokenDigest;
    private readonly Task worker;

    internal static LocalMetricsExporter FromEnvironment()
    {
        string portText=Environment.GetEnvironmentVariable("ET_METRICS_PORT");
        string file=Environment.GetEnvironmentVariable("ET_METRICS_TOKEN_FILE");
        if(string.IsNullOrEmpty(portText)&&string.IsNullOrEmpty(file))return null;
        if(!int.TryParse(portText,out int port)||port<1024||port>65535||string.IsNullOrEmpty(file)||!Path.IsPathFullyQualified(file))
            throw new InvalidOperationException("Invalid local metrics configuration");
        var info=new FileInfo(file);
        if(!info.Exists||info.Length>128)throw new InvalidOperationException("Invalid metrics credential file");
        string token=File.ReadAllText(file).Trim();
        if(token.Length!=64)throw new InvalidOperationException("Metrics token must be 32 random bytes encoded as hex");
        foreach(char c in token)if(!Uri.IsHexDigit(c))throw new InvalidOperationException("Invalid metrics token encoding");
        return new LocalMetricsExporter(port,token);
    }

    private LocalMetricsExporter(int port,string token)
    {
        this.tokenDigest=SHA256.HashData(Encoding.ASCII.GetBytes("Bearer "+token));
        this.listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try {this.listener.Start();}
        catch {this.listener.Close();throw;}
        this.worker=Task.Run(this.Serve);
    }

    private async Task Serve()
    {
        while(this.listener.IsListening)
        {
            HttpListenerContext context;
            try {context=await this.listener.GetContextAsync();}
            catch(HttpListenerException){break;}
            catch(ObjectDisposedException){break;}
            try
            {
                context.Response.Headers["Cache-Control"]="no-store";
                string authorization=context.Request.Headers["Authorization"];
                if(context.Request.RemoteEndPoint==null||!IPAddress.IsLoopback(context.Request.RemoteEndPoint.Address)||
                    authorization==null||authorization.Length!=71||
                    !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.ASCII.GetBytes(authorization)),this.tokenDigest))
                {
                    context.Response.StatusCode=401;
                    continue;
                }
                if(context.Request.Url.AbsolutePath!="/metrics"||context.Request.Url.Query.Length!=0)
                {
                    context.Response.StatusCode=404;
                    continue;
                }
                if(context.Request.HttpMethod!="GET")
                {
                    context.Response.StatusCode=405;
                    context.Response.Headers["Allow"]="GET";
                    continue;
                }
                if(Server.OperationalMetrics.Instance==null)
                {
                    context.Response.StatusCode=503;
                    continue;
                }
                byte[] bytes=Encoding.UTF8.GetBytes(Server.OperationalMetrics.Instance.ExportPrometheus());
                context.Response.ContentType="text/plain; version=0.0.4; charset=utf-8";
                context.Response.ContentLength64=bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch(Exception)
            {
                // Never log request headers or credential-bearing configuration.
                try {context.Response.StatusCode=503;}catch(Exception) { }
            }
            finally {try {context.Response.Close();}catch(Exception) { }}
        }
    }

    public void Dispose()
    {
        this.listener.Close();
        try {this.worker.Wait(TimeSpan.FromSeconds(3));}catch(AggregateException) { }
    }
}
