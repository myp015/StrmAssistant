using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace StrmAssistant.Core
{
    /// <summary>
    /// 定期性能报告器
    /// 在后台定期生成性能和健康报告
    /// </summary>
    public class PerformanceReporter : IDisposable
    {
        private static PerformanceReporter _instance;
        private static readonly object _lock = new object();
        
        private readonly ILogger _logger;
        private Timer _reportTimer;
        private readonly int _intervalMinutes;
        private bool _disposed;
        
        private PerformanceReporter(ILogger logger, int intervalMinutes = 60)
        {
            _logger = logger;
            _intervalMinutes = intervalMinutes;
        }
        
        public static PerformanceReporter Instance
        {
            get
            {
                if (_instance == null)
                {
                    throw new InvalidOperationException("PerformanceReporter not initialized. Call Initialize() first.");
                }
                return _instance;
            }
        }
        
        public static void Initialize(ILogger logger, int intervalMinutes = 60)
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    if (_instance == null)
                    {
                        _instance = new PerformanceReporter(logger, intervalMinutes);
                        _instance.Start();
                    }
                }
            }
        }
        
        /// <summary>
        /// 启动定期报告
        /// </summary>
        public void Start()
        {
            if (_reportTimer != null)
            {
                _logger.Warn("PerformanceReporter already started");
                return;
            }
            
            var interval = TimeSpan.FromMinutes(_intervalMinutes);
            _reportTimer = new Timer(GenerateReport, null, interval, interval);
            
            _logger.Info($"PerformanceReporter started - Reports every {_intervalMinutes} minutes");
        }
        
        /// <summary>
        /// 停止定期报告
        /// </summary>
        public void Stop()
        {
            if (_reportTimer != null)
            {
                _reportTimer.Dispose();
                _reportTimer = null;
                _logger.Info("PerformanceReporter stopped");
            }
        }
        
        /// <summary>
        /// 立即生成报告
        /// </summary>
        public void GenerateReportNow()
        {
            GenerateReport(null);
        }
        
        private void GenerateReport(object state)
        {
            try
            {
                _logger.Info("=== Periodic Performance Report ===");
                
                // 1. FastReflection统计
                try
                {
                    var reflectionStats = FastReflection.Instance.GetPerformanceStats();
                    _logger.Info($"FastReflection: {reflectionStats}");
                    
                    if (reflectionStats.CacheHitRate < 70 && reflectionStats.TotalInvocations > 100)
                    {
                        _logger.Warn($"Low FastReflection cache hit rate: {reflectionStats.CacheHitRate:F2}% - Consider optimization");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug($"Failed to get FastReflection stats: {ex.Message}");
                }
                
                // 2. 性能监控 - Top 10操作
                try
                {
                    var allMetrics = PerformanceMonitor.Instance.GetAllMetrics();
                    if (allMetrics.Length > 0)
                    {
                        _logger.Info("Top 10 Operations:");
                        var count = Math.Min(10, allMetrics.Length);
                        for (int i = 0; i < count; i++)
                        {
                            var metric = allMetrics[i];
                            _logger.Info($"  {i + 1}. {metric.OperationName}: {metric.TotalCalls} calls, Avg={metric.AverageMs:F2}ms");
                        }
                    }
                    
                    // 检查慢操作
                    var slowOps = PerformanceMonitor.Instance.GetSlowOperations(1000);
                    if (slowOps.Length > 0)
                    {
                        _logger.Warn($"Detected {slowOps.Length} slow operations (>1s):");
                        foreach (var op in slowOps)
                        {
                            _logger.Warn($"  - {op.OperationName}: Avg={op.AverageMs:F2}ms, Max={op.MaxMs}ms");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug($"Failed to get performance stats: {ex.Message}");
                }
                
                // 3. 健康检查
                try
                {
                    var health = HealthCheck.Instance.PerformHealthCheck();
                    _logger.Info($"System Health: {health.OverallStatus}");
                    
                    if (health.OverallStatus != HealthStatus.Healthy)
                    {
                        _logger.Warn("Health issues detected:");
                        foreach (var issue in health.Issues)
                        {
                            _logger.Warn($"  - {issue}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug($"Failed to perform health check: {ex.Message}");
                }
                
                // 4. 内存使用
                try
                {
                    var memoryMB = GC.GetTotalMemory(false) / 1024 / 1024;
                    _logger.Info($"Memory Usage: {memoryMB} MB");
                    
                    if (memoryMB > 500)
                    {
                        _logger.Warn($"High memory usage detected: {memoryMB} MB");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug($"Failed to get memory stats: {ex.Message}");
                }
                
                _logger.Info("====================================");
            }
            catch (Exception ex)
            {
                _logger.Error($"Performance report generation failed: {ex.Message}");
            }
        }
        
        public void Dispose()
        {
            if (!_disposed)
            {
                Stop();
                _disposed = true;
            }
        }
    }
}
