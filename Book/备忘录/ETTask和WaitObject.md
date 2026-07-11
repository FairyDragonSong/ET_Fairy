一、先搞懂一个最基本的事:async/await 到底干了啥

  你写这样一段代码:

  async ETTask DoSomething()
  {
      Log("A");
      await WaitOneSecond();   // 等 1 秒
      Log("B");
  }

  直觉上像是"线程睡了 1 秒"。但其实根本没有线程在睡。

  实际发生的事是:编译器把这个方法切成两半:

  第 1 段:  Log("A");  开始等待
  第 2 段:  Log("B");  结束

  执行流程像这样:

  执行 Log("A")
  ↓
  开始等 1 秒, 但此处不阻塞, 直接 return 走人
  ↓
  （主线程跑别的事去了）
  ↓
  1 秒后, 有人喊"时间到!"
  ↓
  从第 2 段(Log("B"))接着执行

  关键点:整段代码都跑在同一个主线程上,没有任何线程被卡住。

  await 的真正含义是:"这里先记一个书签,我退出去;等条件满足时,从书签处接着跑。"

  ---
  二、那"书签"是啥?——状态机

  C# 编译器看到 async 方法,会偷偷把它改写成一个对象(叫状态机),大致长这样:

  State = 0
  执行第 0 段:   Log("A");
                开始等待;
                注册一个回调"等结束后调我"
                return

  ← 等待中 ←

  外面调用回调 → State = 1, 重新进入 →

  执行第 1 段:  Log("B"); 完成

  这个"状态机对象"就是那个书签——它记着"我现在跑到第几段了"。

  await 时:注册回调,然后退出。
  被唤醒时:重新进入,跳到下一段。

  ---
  三、那"回调"挂在谁身上?——Awaiter

  await xxx;

  这个 xxx 必须是个能"被等"的东西,术语叫 Awaiter。Awaiter 至少要能回答 3 件事:

  1. "你完事没?" → IsCompleted
  2. "完事了把这个方法叫一下" → OnCompleted(callback)
  3. "完事了告诉我结果" → GetResult()

  整个流程:

  await xxx
    ↓
  问 xxx.IsCompleted
    ↓
  没完成 → xxx.OnCompleted(我的"接着跑"方法), 然后 return
  完成了 → 直接 xxx.GetResult() 拿结果, 继续跑

  所以 await 本质就是:把"接着跑"的回调挂在 Awaiter 身上,等它喊一声。

  ---
  四、ETTask 是啥?

  ETTask 就是 ET 自己写的一个 Awaiter,用来替代 C# 自带的 Task。

  它内部就 3 个核心字段:

  状态:    Pending(等着) / Succeeded(成了) / Faulted(出错了)
  回调:    一个 Action(就是"接着跑"的方法)
  结果值:  T value (如果有返回值的话)

  工作起来就两个动作:

  动作 A:有人来 await 它

  OnCompleted(回调) {
      if (已经完成) 立即调用回调;
      else          this.callback = 回调;   // 存起来
  }

  动作 B:有人调用 SetResult

  SetResult(值) {
      this.state = Succeeded;
      this.value = 值;
      this.callback?.Invoke();   // 立刻同步调用之前存的回调
  }

  就这么简单。一个 SetResult,等待方就被立刻唤醒了。

  ---
  五、回到 ObjectWait

  现在再看 ObjectWait,你会发现它就是个手动版的 ETTask 管理员:

  // 协程 B 调用 Wait
  async ETTask<T> Wait<T>() {
      var tcs = new ETTask<T>();      // 造一个 ETTask
      把 tcs 存到字典里;                 // 按类型分类
      var ret = await tcs;            // 卡在这里, 注册回调
      return ret;
  }

  // 协程 A 调用 Notify
  void Notify<T>(T obj) {
      从字典拿出所有等 T 的 tcs;
      foreach (var tcs in 列表)
          tcs.SetResult(obj);          // 一调用, 等待方立刻醒
  }

  整个故事:

  协程 B:  await Wait<Wait_SceneChangeFinish>()
                ↓
                造个 tcs, 存进 ObjectWait 的字典
                ↓
                await tcs → 把"接着跑"的回调挂到 tcs 上
                ↓
                退出, 主线程去干别的事

  协程 A:  Notify(new Wait_SceneChangeFinish())
                ↓
                从字典拿出 tcs
                ↓
                tcs.SetResult(obj)
                ↓
                tcs 内部立刻调用之前挂的回调
                ↓
                协程 B 醒过来, 接着往下跑

  ---
  六、为啥 ET 不用系统 Task,要自己造一个?

  三个理由,大白话:

  1. 省内存:系统 Task 每次 await 都会偷偷分配一堆对象,游戏一帧调几百次就 GC 卡顿了。ETTask 用对象池,几乎不产生垃圾。
  2. 单线程更安全:游戏逻辑只跑在主线程。系统 Task 的回调可能跑到别的线程上,操作 Unity 对象会崩。ETTask 写死同步调用,SetResult 时直接在当前线程跳到下一段,没有线程切换。
  3. 异常不丢:async void 写错了异常会让进程直接挂掉,系统 Task 写错了又可能把异常吃掉。ETTask 的 ETVoid 顶层方法会把异常交给统一的处理器记日志,不崩也不丢。

  ---
  七、一句话总结

  ▎ await xxx = 把"我下面要跑的代码"打包成回调,挂到 xxx 身上,然后退出。等 xxx 哪天调用 SetResult,这个回调就被立刻执行,你的代码就接着跑了。
  ▎
  ▎ ETTask 就是个轻量、池化、单线程版的"可被 await 的对象"。ObjectWait 就是用 ETTask 做了一个按类型分桶的"信号注册中心"。





  > objectwait 的 Notify<T> 有一个setresult ，看起来是传了一个参数？其他wait的地方如何获取这参数

● 好问题,这正是 ETTask 的精髓。我们一步步看代码就能看懂。

  ---
  一、先看 Notify 这一行

  // ObjectWait.cs:166
  ((ResultCallback<T>) tcs).SetResult(obj);

  这里的 obj 就是你 Notify(new Wait_XXX { ... }) 传进来的那个 struct。

  ---
  二、再看 ResultCallback<T>.SetResult

  // ObjectWait.cs:71-76
  public void SetResult(K k)
  {
      var t = tcs;
      this.tcs = null;
      t.SetResult(k);    // ← 把 k 传给了内部的 ETTask<K>
  }

  它把 k 转手交给了内部那个 ETTask<K>。

  ---
  三、再看 ETTask<T>.SetResult

  // ETTask.cs:282-296
  public void SetResult(T result)
  {
      this.state = AwaiterStatus.Succeeded;
      this.value = result;             // ← 存到字段里!
      Action c = this.callback as Action;
      this.callback = null;
      c?.Invoke();                     // 唤醒等待方
  }

  重点在 this.value = result:这个值被存到 ETTask 自己的 value 字段里。

  然后调用 c.Invoke() 唤醒等待方,让等待方"接着跑"。

  ---
  四、等待方接着跑后,怎么拿到这个值?

  回忆一下第三课讲的 await 三件事:

  1. IsCompleted   -- 完事没?
  2. OnCompleted   -- 没完成就挂回调
  3. GetResult     -- 完成后取结果   ← 就是这个!

  编译器把 var ret = await tcs; 翻译成大致这样:

  if (!tcs.IsCompleted) {
      tcs.OnCompleted(继续跑的回调);
      return;
  }
  // 被唤醒后从这里继续:
  var ret = tcs.GetResult();    // ← 取值就在这一步!

  那 GetResult 干了啥?

  // ETTask.cs:234-251
  public T GetResult()
  {
      switch (this.state)
      {
          case AwaiterStatus.Succeeded:
              T v = this.value;       // ← 从字段把值取出来
              this.Recycle();
              return v;               // ← 返回给等待方
          ...
      }
  }

  把刚才存进 value 的那个 obj 取出来返回。

  ---
  五、串起来看

  // 协程 B
  var ret = await self.Wait<Wait_XXX>();
  //        ↑
  //        这个 ret 是怎么来的:

  1. 协程 A:   Notify(new Wait_XXX { Error=0, SomeData=42 })
                  ↓
  2. SetResult(obj)        把 obj 存进 ETTask.value 字段
                  ↓
  3. callback.Invoke()     唤醒协程 B 的状态机
                  ↓
  4. 协程 B 重新进入        编译器自动调用 GetResult()
                  ↓
  5. GetResult() 返回 value  → 就是当初那个 obj
                  ↓
  6. ret = obj              协程 B 拿到了!

  ---
  六、实际例子

  // 协程 A (生产者)
  root.GetComponent<ObjectWait>().Notify(new Wait_SceneChangeFinish
  {
      Error = 0,
      // 假设 struct 里还有别的字段, 比如 SceneId = 5
  });

  // 协程 B (消费者)
  var result = await root.GetComponent<ObjectWait>().Wait<Wait_SceneChangeFinish>();
  // 此时 result.Error == 0
  // 此时 result.SceneId == 5      (如果有这字段的话)

  A 传进 Notify 的整个 struct,完整地出现在 B 的 await 返回值里——包括 Error、SceneId、所有字段。

  ---
  七、一句话

  ▎ Notify 传进去的 struct → 沿着 SetResult → 存进 ETTask.value → 唤醒 await 方 → 编译器自动调 GetResult() 取出来 → 成为 await 表达式的返回值。

  所以 ET 里很多 Wait_XXX struct 不止有 Error 字段,还会塞业务数据(房间 id、单位 id、消息体等),通知方写进去,等待方就能直接拿到。