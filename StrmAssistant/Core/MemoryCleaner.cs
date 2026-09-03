using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using MediaBrowser.Model.Logging;

namespace StrmAssistant.Core
{
    /// <summary>
    /// 定期内存清理器
    /// 每隔指定时间（默认 5 分钟）执行一次完整 GC，并把已释放的内存归还给操作系统：
    /// - Windows: SetProcessWorkingSetSize(-1, -1) 收缩进程工作集；
    /// - Linux (含 Docker, 基于 glibc)：调用 libc malloc_trim(0)，把 glibc 分配器
    ///   缓存的空闲内存返还内核，避免 RSS 持续增长；
    /// - 其他平台 (musl/Alpine、macOS) 仅执行 GC，不调用平台 API。
    /// </summary>
    public class MemoryCleaner : IDisposable
    {
        private static MemoryCleaner _instance;
        private static readonly object _lock = new object();

        private readonly ILogger _logger;
        private int _intervalMinutes;
        private Timer _timer;
        private int _running;
        private bool _disposed;

        // 批次后手动清理的最小间隔，防止短时间内被多次触发
        private static readonly TimeSpan MinManualInterval = TimeSpan.FromSeconds(60);
        private long _lastTickTicks;
        private int _manualPending;

        // Windows: 收缩工作集
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr min, IntPtr max);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        // Linux glibc: 把分配器空闲页归还内核（musl 没有此符号，会抛 EntryPointNotFoundException）
        [DllImport("libc", EntryPoint = "malloc_trim", SetLastError = true)]
        private static extern int malloc_trim_libc(UIntPtr pad);

        // 标记当前 Linux 是否支持 malloc_trim，避免每次都 try/catch P/Invoke 异常
        private static int _mallocTrimSupported = -1; // -1 未知, 0 不支持, 1 支持

        private MemoryCleaner(ILogger logger, int intervalMinutes)
        {
            _logger = logger;
            _intervalMinutes = intervalMinutes;
        }

        public static MemoryCleaner Instance => _instance;

        public static void Initialize(ILogger logger, int intervalMinutes = 5)
        {
            if (_instance != null) return;

            lock (_lock)
            {
                if (_instance != null) return;

                _instance = new MemoryCleaner(logger, intervalMinutes);
                _instance.Start();
            }
        }

        public static void DisposeInstance()
        {
            lock (_lock)
            {
                _instance?.Dispose();
                _instance = null;
            }
        }

        /// <summary>
        /// 应用最新配置：根据 enabled / intervalMinutes 启动、停止或重启清理器。
        /// </summary>
        public static void ApplySettings(ILogger logger, bool enabled, int intervalMinutes)
        {
            if (intervalMinutes < 1) intervalMinutes = 1;
            if (intervalMinutes > 120) intervalMinutes = 120;

            lock (_lock)
            {
                if (!enabled)
                {
                    if (_instance != null)
                    {
                        _instance.Dispose();
                        _instance = null;
                    }
                    return;
                }

                if (_instance == null)
                {
                    _instance = new MemoryCleaner(logger, intervalMinutes);
                    _instance.Start();
                }
                else if (_instance._intervalMinutes != intervalMinutes)
                {
                    _instance.Reschedule(intervalMinutes);
                }
            }
        }

        private void Reschedule(int intervalMinutes)
        {
            _intervalMinutes = intervalMinutes;
            var interval = TimeSpan.FromMinutes(intervalMinutes);
            _timer?.Change(interval, interval);
            _logger.Info($"MemoryCleaner rescheduled - Cleanup every {_intervalMinutes} minutes");
        }

        public void Start()
        {
            if (_timer != null)
            {
                _logger.Warn("MemoryCleaner already started");
                return;
            }

            var interval = TimeSpan.FromMinutes(_intervalMinutes);
            // 首次延迟 1 分钟，避免和插件启动期任务争抢
            _timer = new Timer(OnTick, null, TimeSpan.FromMinutes(1), interval);

            _logger.Info($"MemoryCleaner started - Cleanup every {_intervalMinutes} minutes");
        }

        public void Stop()
        {
            if (_timer != null)
            {
                _timer.Dispose();
                _timer = null;
                _logger.Info("MemoryCleaner stopped");
            }
        }

        /// <summary>
        /// 立即执行一次内存清理（同步阻塞）。
        /// </summary>
        public void CleanupNow()
        {
            OnTick(null);
        }

        /// <summary>
        /// 在后台线程触发一次内存清理，并按 <see cref="MinManualInterval"/> 节流。
        /// 适合在批量刮削/刷新任务结束后调用，把堆积的临时对象立刻释放，
        /// 避免 RSS 在两次定期 tick 之间持续累积。
        /// </summary>
        public static void RequestCleanup(string reason = null)
        {
            var inst = _instance;
            if (inst == null || inst._disposed || inst._timer == null) return;

            // 节流：距离上一次 tick 不足 MinManualInterval 则跳过
            var lastTicks = Interlocked.Read(ref inst._lastTickTicks);
            if (lastTicks != 0)
            {
                var elapsed = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - lastTicks);
                if (elapsed < MinManualInterval) return;
            }

            // 同一时刻只允许一次 pending 的手动清理
            if (Interlocked.CompareExchange(ref inst._manualPending, 1, 0) != 0) return;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (!string.IsNullOrEmpty(reason))
                        inst._logger.Debug($"MemoryCleaner: post-batch cleanup requested ({reason})");
                    inst.OnTick(null);
                }
                finally
                {
                    Interlocked.Exchange(ref inst._manualPending, 0);
                }
            });
        }

        private void OnTick(object state)
        {
            // 防止上一轮还没执行完又被触发
            if (Interlocked.Exchange(ref _running, 1) == 1) return;

            try
            {
                long managedBefore = GC.GetTotalMemory(false);
                long workingSetBefore = 0;
                try
                {
                    using (var p = Process.GetCurrentProcess())
                    {
                        workingSetBefore = p.WorkingSet64;
                    }
                }
                catch
                {
                    // ignore
                }

                // 1) 强制完整 GC，并尝试压缩 LOH（需 .NET 4.5.1+）
                try
                {
                    GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                }
                catch
                {
                    // ignore - 某些运行时不支持
                }

                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);

                // 2) 把已释放的内存真正归还给操作系统
                long workingSetAfter = workingSetBefore;
                string nativeAction = "none";

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    try
                    {
                        if (SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1)))
                            nativeAction = "SetProcessWorkingSetSize";
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug($"SetProcessWorkingSetSize failed: {ex.Message}");
                    }
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    // 在 Linux/Docker 下，.NET 释放的对象内存通常仍被 glibc 的 ptmalloc
                    // 缓存在分配器 arena 中，导致容器/系统看到的 RSS 一直涨。malloc_trim(0)
                    // 会扫描各 arena 把空闲整页归还内核 (madvise/munmap)。
                    if (TryMallocTrim())
                    {
                        nativeAction = "malloc_trim";
                    }
                }
                // macOS 与 musl libc (Alpine 等) 没有等价稳定接口，留给 GC 自身处理。

                long managedAfter = GC.GetTotalMemory(false);
                try
                {
                    using (var p = Process.GetCurrentProcess())
                    {
                        workingSetAfter = p.WorkingSet64;
                    }
                }
                catch
                {
                    // ignore
                }

                long managedFreedMB = (managedBefore - managedAfter) / 1024 / 1024;
                long workingSetFreedMB = (workingSetBefore - workingSetAfter) / 1024 / 1024;
                long managedNowMB = managedAfter / 1024 / 1024;
                long workingSetNowMB = workingSetAfter / 1024 / 1024;

                _logger.Info(
                    $"MemoryCleaner [{nativeAction}]: Managed {managedNowMB} MB (freed {managedFreedMB} MB), " +
                    $"RSS {workingSetNowMB} MB (freed {workingSetFreedMB} MB)");
            }
            catch (Exception ex)
            {
                _logger.Warn($"MemoryCleaner tick failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _lastTickTicks, DateTime.UtcNow.Ticks);
                Interlocked.Exchange(ref _running, 0);
            }
        }

        private bool TryMallocTrim()
        {
            // 已知不支持，直接跳过
            if (_mallocTrimSupported == 0) return false;

            try
            {
                // pad=0：尽量归还所有可归还的空闲内存
                malloc_trim_libc(UIntPtr.Zero);
                if (_mallocTrimSupported == -1)
                {
                    _mallocTrimSupported = 1;
                    _logger.Info("MemoryCleaner: glibc malloc_trim is available; will release freed memory back to OS each cycle.");
                }
                return true;
            }
            catch (DllNotFoundException ex)
            {
                _mallocTrimSupported = 0;
                _logger.Info($"MemoryCleaner: libc not found ({ex.Message}); skipping malloc_trim. " +
                             "Likely musl libc (Alpine) - GC alone will be used.");
                return false;
            }
            catch (EntryPointNotFoundException ex)
            {
                _mallocTrimSupported = 0;
                _logger.Info($"MemoryCleaner: malloc_trim not exported by libc ({ex.Message}); " +
                             "likely musl libc - GC alone will be used.");
                return false;
            }
            catch (Exception ex)
            {
                _mallocTrimSupported = 0;
                _logger.Debug($"MemoryCleaner: malloc_trim invocation failed: {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            Stop();
            _disposed = true;
        }
    }
}
