using System;
using CommandLine;
using System.Threading;
using UnityEngine;

namespace ET
{
	public class Init: MonoBehaviour
	{
		private SynchronizationContext unityContext;

		private void Start()
		{
			this.unityContext = SynchronizationContext.Current;
			try { this.StartAsync().Coroutine(); }
			finally { SynchronizationContext.SetSynchronizationContext(this.unityContext); }
		}
		
		private async ETTask StartAsync()
		{
			DontDestroyOnLoad(gameObject);
			
			AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
			{
				Log.Error(e.ExceptionObject.ToString());
			};

			// 命令行参数
			string[] args = "".Split(" ");
			Parser.Default.ParseArguments<Options>(args)
				.WithNotParsed(error => throw new Exception($"命令行格式错误! {error}"))
				.WithParsed((o)=>World.Instance.AddSingleton(o));
			Options.Instance.StartConfig = $"StartConfig/Localhost";
			
			World.Instance.AddSingleton<Logger>().Log = new UnityLogger();
			ETTask.ExceptionHandler += Log.Error;
			
			World.Instance.AddSingleton<TimeInfo>();
			World.Instance.AddSingleton<FiberManager>();

			await World.Instance.AddSingleton<ResourcesComponent>().CreatePackageAsync("DefaultPackage", true);
			
			CodeLoader codeLoader = World.Instance.AddSingleton<CodeLoader>();
			await codeLoader.DownloadAsync();
			
			codeLoader.Start();
		}

		private void Update()
		{
			try
			{
				TimeInfo.Instance.Update();
				FiberManager.Instance.Update();
			}
			finally
			{
				// ET uses its own context during fiber work; restore Unity's before
				// editor tools and other engine callbacks resume.
				SynchronizationContext.SetSynchronizationContext(this.unityContext);
			}
		}

		private void LateUpdate()
		{
			try { FiberManager.Instance.LateUpdate(); }
			finally { SynchronizationContext.SetSynchronizationContext(this.unityContext); }
		}

		private void OnApplicationQuit()
		{
			try { World.Instance.Dispose(); }
			finally { SynchronizationContext.SetSynchronizationContext(this.unityContext); }
		}
	}
	
	
}
