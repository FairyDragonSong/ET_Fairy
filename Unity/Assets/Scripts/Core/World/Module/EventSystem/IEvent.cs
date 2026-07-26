using System;

namespace ET
{
	public interface IEvent
	{
		Type Type { get; }
	}
	
	public abstract class AEvent<S, A>: IEvent where S: class, IScene where A: struct
	{
		public Type Type
		{
			get
			{
				return typeof (A);
			}
		}

		protected abstract ETTask Run(S scene, A a);

		public ETTask Handle(S scene, A a)
		{
			// Do not make this generic method async. IL2CPP may not generate the
			// constructor for AEvent<S, A>.<Handle>d__*, which makes HybridCLR
			// fail when a hot-update event first invokes it.
			return EventSystem.HandleEvent(Run(scene, a));
		}
	}
}
