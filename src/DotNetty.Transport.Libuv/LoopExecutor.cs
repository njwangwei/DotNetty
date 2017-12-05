// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

// ReSharper disable ConvertToAutoPropertyWhenPossible
#pragma warning disable 420
namespace DotNetty.Transport.Libuv
{
    using System;
    using System.Diagnostics;
    using System.Diagnostics.Contracts;
    using System.Threading.Tasks;
    using DotNetty.Common.Concurrency;
    using DotNetty.Transport.Channels;
    using DotNetty.Common.Internal.Logging;
    using DotNetty.Common.Internal;
    using System.Threading;
    using DotNetty.Common;
    using DotNetty.Transport.Libuv.Native;

    using Timer = DotNetty.Transport.Libuv.Native.Timer;

    class LoopExecutor : AbstractScheduledEventExecutor
    {
        const int DefaultBreakoutTime = 100;
        static readonly TimeSpan DefaultBreakoutInterval = TimeSpan.FromMilliseconds(DefaultBreakoutTime);

        protected static readonly IInternalLogger Logger = InternalLoggerFactory.GetInstance<LoopExecutor>();

        const int NotStartedState = 1;
        const int StartedState = 2;
        const int ShuttingDownState = 3;
        const int ShutdownState = 4;
        const int TerminatedState = 5;

        readonly PreciseTimeSpan preciseBreakoutInterval;
        readonly IQueue<IRunnable> taskQueue;
        readonly XThread thread;
        readonly TaskScheduler scheduler;
        readonly TaskCompletionSource terminationCompletionSource;
        readonly Loop loop;
        readonly Async asyncHandle;
        readonly Timer timerHandle;
        volatile int executionState = NotStartedState;

        PreciseTimeSpan lastExecutionTime;
        PreciseTimeSpan gracefulShutdownStartTime;
        PreciseTimeSpan gracefulShutdownQuietPeriod;
        PreciseTimeSpan gracefulShutdownTimeout;

        // Flag to indicate whether async handle should be used to wake up 
        // the loop, only accessed when InEventLoop is true
        bool wakeUp = true;

        public LoopExecutor(string threadName)
            : this(null, threadName, DefaultBreakoutInterval)
        {
        }

        public LoopExecutor(IEventLoopGroup parent, string threadName)
            : this(parent, threadName, DefaultBreakoutInterval)
        {
        }

        public LoopExecutor(IEventLoopGroup parent, string threadName, TimeSpan breakoutInterval) : base(parent)
        {
            this.preciseBreakoutInterval = PreciseTimeSpan.FromTimeSpan(breakoutInterval);
            this.terminationCompletionSource = new TaskCompletionSource();
            this.taskQueue = PlatformDependent.NewMpscQueue<IRunnable>();
            this.scheduler = new ExecutorTaskScheduler(this);

            this.loop = new Loop();
            this.asyncHandle = new Async(this.loop, OnAsync, this);
            this.timerHandle = new Timer(this.loop, OnTimer, this);
            string name = $"{nameof(LoopExecutor)}:{this.loop.Handle}";
            if (!string.IsNullOrEmpty(threadName))
            {
                name = $"{name}({threadName})";
            }
            this.thread = new XThread(Run) { Name = name };
        }

        protected internal void Start()
        {
            if (this.executionState > NotStartedState)
            {
                throw new InvalidOperationException($"Invalid state {this.executionState}");
            }
            this.thread.Start(this);
        }

        internal Loop UnsafeLoop => this.loop;

        internal int LoopThreadId => this.thread.Id;

        static void Run(object state)
        {
            var executor = (LoopExecutor)state;
            executor.SetCurrentExecutor(executor);

            Task.Factory.StartNew(() => 
                executor.RunLoop(),
                CancellationToken.None,
                TaskCreationOptions.None,
                executor.scheduler);
        }

        static void OnAsync(object state) => ((LoopExecutor)state).OnAsync();

        void OnAsync()
        {
            if (this.IsShuttingDown)
            {
                this.ShuttingDown();
            }
            else
            {
                this.RunAllTasks(this.preciseBreakoutInterval);
            }
        }

        static void OnTimer(object state) => ((LoopExecutor)state).WakeUp(true);

        /// <summary>
        /// Called before run the loop in the loop thread.
        /// </summary>
        protected virtual void Initialize()
        {
            // NOOP
        }

        /// <summary>
        /// Called before stop the loop in the loop thread.
        /// </summary>
        protected virtual void Release()
        {
            // NOOP
        }

        void RunLoop()
        {
            IntPtr handle = this.loop.Handle;
            try
            {
                this.Initialize();
                Interlocked.CompareExchange(ref this.executionState, StartedState, NotStartedState);
                this.timerHandle.RemoveReference(); // Timer should not keep the loop open
                this.UpdateLastExecutionTime();
                this.loop.Run(uv_run_mode.UV_RUN_DEFAULT);
                Logger.Info("Loop {}:{} run default finished.", this.thread.Name, handle);
                this.CleanupAndTerminate();
            }
            catch (Exception ex)
            {
                Logger.Error("Loop {}:{} run default error.", this.thread.Name, handle, ex);
                this.executionState = TerminatedState;
                this.terminationCompletionSource.TrySetException(ex);
            }
        }
        void ShuttingDown()
        {
            Debug.Assert(this.InEventLoop);

            this.CancelScheduledTasks();

            if (this.gracefulShutdownStartTime == PreciseTimeSpan.Zero)
            {
                this.gracefulShutdownStartTime = PreciseTimeSpan.FromStart;
            }

            bool immediate = this.gracefulShutdownQuietPeriod == PreciseTimeSpan.Zero;
            if (this.RunAllTasks())
            {
                if (!immediate)
                {
                    this.WakeUp(true);
                    return;
                }
            }

            if (!immediate)
            {
                PreciseTimeSpan nanoTime = PreciseTimeSpan.FromStart;
                if (nanoTime - this.gracefulShutdownStartTime <= this.gracefulShutdownTimeout 
                    && nanoTime - this.lastExecutionTime <= this.gracefulShutdownQuietPeriod)
                {
                    this.timerHandle.Start(100, 0); // Wait for 100ms
                    return;
                }
            }

            try
            {
                // Drop out from the loop so that it can be safely disposed,
                // other active handles will be closed by loop.Close()
                if (this.loop.IsAlive)
                {
                    this.loop.Stop();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("{}: shutting down loop error", ex);
            }
        }

        void CleanupAndTerminate()
        {
            try
            {
                this.Cleanup();
            }
            finally
            {
                Interlocked.Exchange(ref this.executionState, TerminatedState);
                if (!this.taskQueue.IsEmpty)
                {
                    Logger.Warn($"{nameof(LoopExecutor)} terminated with non-empty task queue ({this.taskQueue.Count})");
                }
                this.terminationCompletionSource.TryComplete();
            }
        }

        void Cleanup()
        {
            IntPtr handle = this.loop.Handle;

            try
            {
                this.Release();
            }
            catch (Exception ex)
            {
                Logger.Warn("Loop {}:{} release error {}", this.thread.Name, handle, ex);
            }

            try
            {
                this.timerHandle.Stop();
                this.asyncHandle.RemoveReference();
                this.loop.Dispose();
                Logger.Info("Loop {}:{} disposed.", this.thread.Name, handle);
            }
            catch (Exception ex)
            {
                Logger.Warn("Loop {}:{} dispose error {}", this.thread.Name, handle, ex);
            }
        }

        protected void UpdateLastExecutionTime() => this.lastExecutionTime = PreciseTimeSpan.FromStart;

        void RunAllTasks(PreciseTimeSpan timeout)
        {
            this.wakeUp = false;
            this.FetchFromScheduledTaskQueue();
            IRunnable task = this.PollTask();
            if (task == null)
            {
                this.wakeUp = true;
                return;
            }

            PreciseTimeSpan deadline = PreciseTimeSpan.Deadline(timeout);
            long runTasks = 0;
            PreciseTimeSpan executionTime;
            for (;;)
            {
                SafeExecute(task);

                runTasks++;

                // Check timeout every 64 tasks because nanoTime() is relatively expensive.
                // XXX: Hard-coded value - will make it configurable if it is really a problem.
                if ((runTasks & 0x3F) == 0)
                {
                    executionTime = PreciseTimeSpan.FromStart;
                    if (executionTime >= deadline)
                    {
                        break;
                    }
                }

                task = this.PollTask();
                if (task == null)
                {
                    executionTime = PreciseTimeSpan.FromStart;
                    break;
                }
            }
            this.wakeUp = true;
            this.lastExecutionTime = executionTime;

            if (this.taskQueue.IsEmpty)
            {
                long nextTimeout = this.loop.GetBackendTimeout();

                // This means the loop will block, ensure any scheduled
                // task will break out.
                if (nextTimeout == -1)
                {
                    IScheduledRunnable nextScheduledTask = this.ScheduledTaskQueue.Peek();
                    if (nextScheduledTask != null)
                    {
                        PreciseTimeSpan wakeUpTimeout = nextScheduledTask.Deadline - PreciseTimeSpan.FromStart;
                        if (wakeUpTimeout.Ticks > 0)
                        {
                            nextTimeout = (long)wakeUpTimeout.ToTimeSpan().TotalMilliseconds;
                            // The loop time must be updated because run all tasks in this
                            // iteration potentially takes a while.
                            this.loop.UpdateTime();
                            this.timerHandle.Start(nextTimeout, 0);
                        }
                    }
                }
                // No need to stop the timer because the timer callback
                // happens exactly once, e.g. repeat = 0
            }
            else
            {
                // Left over tasks, schedule the timer to continue
                this.timerHandle.Start(DefaultBreakoutTime, 0);
            }
        }

        bool FetchFromScheduledTaskQueue()
        {
            PreciseTimeSpan nanoTime = PreciseTimeSpan.FromStart;
            IScheduledRunnable scheduledTask = this.PollScheduledTask(nanoTime);
            while (scheduledTask != null)
            {
                if (!this.taskQueue.TryEnqueue(scheduledTask))
                {
                    // No space left in the task queue add it back to the scheduledTaskQueue so we pick it up again.
                    this.ScheduledTaskQueue.Enqueue(scheduledTask);
                    return false;
                }
                scheduledTask = this.PollScheduledTask(nanoTime);
            }
            return true;
        }

        IRunnable PollTask() => !this.IsShuttingDown ? PollTaskFrom(this.taskQueue) : null;

        bool RunAllTasks()
        {
            bool fetchedAll;
            bool ranAtLeastOne = false;
            do
            {
                fetchedAll = this.FetchFromScheduledTaskQueue();
                if (RunAllTasksFrom(this.taskQueue))
                {
                    ranAtLeastOne = true;
                }
            }
            while (!fetchedAll); // keep on processing until we fetched all scheduled tasks.
            if (ranAtLeastOne)
            {
                this.lastExecutionTime = PreciseTimeSpan.FromStart;
            }
            return ranAtLeastOne;
        }

        static bool RunAllTasksFrom(IQueue<IRunnable> taskQueue)
        {
            IRunnable task = PollTaskFrom(taskQueue);
            if (task == null)
            {
                return false;
            }
            for (;;)
            {
                SafeExecute(task);
                task = PollTaskFrom(taskQueue);
                if (task == null)
                {
                    return true;
                }
            }
        }

        static IRunnable PollTaskFrom(IQueue<IRunnable> taskQueue) =>
            taskQueue.TryDequeue(out IRunnable task) ? task : null;

        public override bool IsShuttingDown => this.executionState >= ShuttingDownState;

        public override Task TerminationCompletion => this.terminationCompletionSource.Task;

        public override bool IsShutdown => this.executionState >= ShutdownState;

        public override bool IsTerminated => this.executionState == TerminatedState;

        public override bool IsInEventLoop(XThread t) => this.thread == t;

        void WakeUp(bool inEventLoop)
        {
            // If the executor is not in the event loop, wake up the loop by async handle immediately.
            //
            // If the executor is in the event loop and in the middle of RunAllTasks, no need to 
            // wake up the loop again. Otherwise, this is called by libuv thread and the loop has
            // queued tasks to process later by RunAllTasks.
            if (!inEventLoop || this.wakeUp)
            {
                this.asyncHandle.Send();
            }
        }

        public override void Execute(IRunnable task)
        {
            Contract.Requires(task != null);

            bool inEventLoop = this.InEventLoop;
            if (inEventLoop)
            {
                this.AddTask(task);
            }
            else
            {
                this.AddTask(task);
                if (this.IsShutdown)
                {
                    Reject($"{nameof(LoopExecutor)} terminated");
                }
            }
            this.WakeUp(inEventLoop);
        }

        void AddTask(IRunnable task)
        {
            if (this.IsShutdown)
            {
                Reject($"{nameof(LoopExecutor)} already shutdown");
            }
            if (!this.taskQueue.TryEnqueue(task))
            {
                Reject($"{nameof(LoopExecutor)} queue task failed");
            }
        }

        static void Reject(string message) => throw new RejectedExecutionException(message);

        public override Task ShutdownGracefullyAsync(TimeSpan quietPeriod, TimeSpan timeout)
        {
            Contract.Requires(quietPeriod >= TimeSpan.Zero);
            Contract.Requires(timeout >= quietPeriod);

            if (this.IsShuttingDown)
            {
                return this.TerminationCompletion;
            }

            bool inEventLoop = this.InEventLoop;
            bool wakeUpLoop;
            for (;;)
            {
                if (this.IsShuttingDown)
                {
                    return this.TerminationCompletion;
                }
                int newState;
                wakeUpLoop = true;
                int oldState = this.executionState;
                if (inEventLoop)
                {
                    newState = ShuttingDownState;
                }
                else
                {
                    switch (oldState)
                    {
                        case NotStartedState:
                        case StartedState:
                            newState = ShuttingDownState;
                            break;
                        default:
                            newState = oldState;
                            wakeUpLoop = false;
                            break;
                    }
                }
                if (Interlocked.CompareExchange(ref this.executionState, newState, oldState) == oldState)
                {
                    break;
                }
            }

            this.gracefulShutdownQuietPeriod = PreciseTimeSpan.FromTimeSpan(quietPeriod);
            this.gracefulShutdownTimeout = PreciseTimeSpan.FromTimeSpan(timeout);

            if (this.executionState == NotStartedState)
            {
                // If the loop is not yet running, close all handles directly
                // because wake up callback will not be executed.
                this.CleanupAndTerminate();
            }
            else
            {
                if (wakeUpLoop)
                {
                    this.WakeUp(inEventLoop);
                }
            }

            return this.TerminationCompletion;
        }
    }
}
