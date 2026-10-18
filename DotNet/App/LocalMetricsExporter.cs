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
    private readonly bool requireManagedRoot;
    private readonly bool acceptPreviousToken;
    private readonly Task worker;

    // 从环境变量读取受控配置并创建实例。
    internal static LocalMetricsExporter FromEnvironment()
    {
        string portText=Environment.GetEnvironmentVariable("ET_METRICS_PORT");
        string file=Environment.GetEnvironmentVariable("ET_METRICS_TOKEN_FILE");
        if(string.IsNullOrEmpty(portText)&&string.IsNullOrEmpty(file))return null;
        if(!int.TryParse(portText,out int port)||port<1024||port>65535||string.IsNullOrEmpty(file)||!Path.IsPathFullyQualified(file))
            throw new InvalidOperationException("Invalid local metrics configuration");
        bool production=Options.Instance?.Develop==0;
        string token=ProductionSecretPolicy.ReadMetricsToken("ET_METRICS_TOKEN_FILE",production);
        string previousFile=Environment.GetEnvironmentVariable("ET_METRICS_PREVIOUS_TOKEN_FILE");
        string previous=string.IsNullOrWhiteSpace(previousFile)?null:ProductionSecretPolicy.ReadMetricsToken("ET_METRICS_PREVIOUS_TOKEN_FILE",production);
        if(previous==token)throw new InvalidOperationException("Metrics rotation credentials must be distinct");
        return new LocalMetricsExporter(port,production,previous!=null);
    }

    // 固化监听端口与令牌轮换策略，实例创建后不再读取可变环境配置。
    private LocalMetricsExporter(int port,bool requireManagedRoot,bool acceptPreviousToken)
    {
        this.requireManagedRoot=requireManagedRoot;
        this.acceptPreviousToken=acceptPreviousToken;
        this.listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try {this.listener.Start();}
        catch {this.listener.Close();throw;}
        this.worker=Task.Run(this.Serve);
    }

    // 持续接收并处理本地指标请求。
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
                    !this.IsAuthorized(authorization))
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

    // 使用固定时间比较校验请求凭据。
    private bool IsAuthorized(string authorization)
    {
        try
        {
            byte[] presented=SHA256.HashData(Encoding.ASCII.GetBytes(authorization));
            string current=ProductionSecretPolicy.ReadMetricsToken("ET_METRICS_TOKEN_FILE",this.requireManagedRoot);
            bool accepted=CryptographicOperations.FixedTimeEquals(presented,SHA256.HashData(Encoding.ASCII.GetBytes("Bearer "+current)));
            if(this.acceptPreviousToken)
            {
                string previous=ProductionSecretPolicy.ReadMetricsToken("ET_METRICS_PREVIOUS_TOKEN_FILE",this.requireManagedRoot);
                accepted|=CryptographicOperations.FixedTimeEquals(presented,SHA256.HashData(Encoding.ASCII.GetBytes("Bearer "+previous)));
            }
            return accepted;
        }
        catch(Exception)
        {
            return false;
        }
    }

    // 释放当前实例占用的资源。
    public void Dispose()
    {
        this.listener.Close();
        try {this.worker.Wait(TimeSpan.FromSeconds(3));}catch(AggregateException) { }
    }
}
