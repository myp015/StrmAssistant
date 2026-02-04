using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Text;
using MediaBrowser.Model.Logging;

namespace StrmAssistant.Core
{
    /// <summary>
    /// 性能监控工具
    /// 用于跟踪和分析关键操作的性能
    /// </summary>
    public class PerformanceMonitor
    {
        private static PerformanceMonitor _instance;
        private static readonly object _lock = new object();
        
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, OperationMetrics> _metrics;
        private readonly Stopwatch _uptime;
        
        private PerformanceMonitor(ILogger logger)
        {
            _logger = logger;
            _metrics = new ConcurrentDictionary<string, OperationMetrics>();
            _uptime = Stopwatch.StartNew();
            
            _logger.Debug("PerformanceMonitor initialized");
        }
        
        public static PerformanceMonitor Instance
        {
            get
            {
                if (_instance == null)
                {
                    throw new InvalidOperationException("PerformanceMonitor not initialized. Call Initialize() first.");
                }
                return _instance;
            }
        }
        
        public static void Initialize(ILogger logger)
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    if (_instance == null)
                    {
                        _instance = new PerformanceMonitor(logger);
                    }
                }
            }
        }
        
        /// <summary>
        /// 记录操作性能
        /// </summary>
        public void RecordOperation(string operationName, long elapsedMilliseconds, bool success = true)
        {
            var metrics = _metrics.GetOrAdd(operationName, _ => new OperationMetrics(operationName));
            metrics.Record(elapsedMilliseconds, success);
        }
        
        /// <summary>
        /// 测量操作性能的便捷方法
        /// </summary>
        public IDisposable Measure(string operationName)
        {
            return new OperationTimer(this, operationName);
        }
        
        /// <summary>
        /// 获取指定操作的统计信息
        /// </summary>
        public OperationMetrics GetMetrics(string operationName)
        {
            return _metrics.TryGetValue(operationName, out var metrics) ? metrics : null;
        }
        
        /// <summary>
        /// 获取所有操作的统计信息
        /// </summary>
        public OperationMetrics[] GetAllMetrics()
        {
            return _metrics.Values.OrderByDescending(m => m.TotalCalls).ToArray();
        }
        
        /// <summary>
        /// 获取性能报告
        /// </summary>
        public string GetPerformanceReport(bool detailed = false)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Performance Monitor Report ===");
            sb.AppendLine($"Uptime: {_uptime.Elapsed:hh\\:mm\\:ss}");
            sb.AppendLine($"Tracked Operations: {_metrics.Count}");
            sb.AppendLine();
            
            var sortedMetrics = _metrics.Values
                .OrderByDescending(m => m.TotalCalls)
                .Take(detailed ? int.MaxValue : 20)
                .ToArray();
            
            if (sortedMetrics.Length == 0)
            {
                sb.AppendLine("No operations recorded yet.");
                return sb.ToString();
            }
            
            sb.AppendLine("Top Operations:");
            sb.AppendLine($"{"Operation",-40} {"Calls",10} {"Avg(ms)",10} {"Min(ms)",10} {"Max(ms)",10} {"Success%",10}");
            sb.AppendLine(new string('-', 110));
            
            foreach (var metric in sortedMetrics)
            {
                var successRate = metric.TotalCalls > 0 
                    ? (double)metric.SuccessCount / metric.TotalCalls * 100 
                    : 0;
                
                sb.AppendLine($"{metric.OperationName,-40} {metric.TotalCalls,10} {metric.AverageMs,10:F2} {metric.MinMs,10} {metric.MaxMs,10} {successRate,9:F1}%");
            }
            
            if (!detailed && _metrics.Count > 20)
            {
                sb.AppendLine($"... and {_metrics.Count - 20} more operations");
            }
            
            sb.AppendLine("================================");
            return sb.ToString();
        }
        
        /// <summary>
        /// 重置所有统计数据
        /// </summary>
        public void Reset()
        {
            _metrics.Clear();
            _uptime.Restart();
            _logger.Debug("PerformanceMonitor reset");
        }
        
        /// <summary>
        /// 获取慢操作列表（超过指定阈值）
        /// </summary>
        public OperationMetrics[] GetSlowOperations(long thresholdMs = 1000)
        {
            return _metrics.Values
                .Where(m => m.AverageMs > thresholdMs || m.MaxMs > thresholdMs * 2)
                .OrderByDescending(m => m.AverageMs)
                .ToArray();
        }
        
        private class OperationTimer : IDisposable
        {
            private readonly PerformanceMonitor _monitor;
            private readonly string _operationName;
            private readonly Stopwatch _stopwatch;
            private bool _disposed;
            
            public OperationTimer(PerformanceMonitor monitor, string operationName)
            {
                _monitor = monitor;
                _operationName = operationName;
                _stopwatch = Stopwatch.StartNew();
            }
            
            public void Dispose()
            {
                if (!_disposed)
                {
                    _stopwatch.Stop();
                    _monitor.RecordOperation(_operationName, _stopwatch.ElapsedMilliseconds);
                    _disposed = true;
                }
            }
        }
    }
    
    /// <summary>
    /// 操作性能指标
    /// </summary>
    public class OperationMetrics
    {
        private readonly object _lock = new object();
        
        public string OperationName { get; }
        public long TotalCalls { get; private set; }
        public long SuccessCount { get; private set; }
        public long FailureCount { get; private set; }
        public long TotalMs { get; private set; }
        public long MinMs { get; private set; } = long.MaxValue;
        public long MaxMs { get; private set; }
        
        public double AverageMs => TotalCalls > 0 ? (double)TotalMs / TotalCalls : 0;
        
        public OperationMetrics(string operationName)
        {
            OperationName = operationName;
        }
        
        public void Record(long elapsedMs, bool success)
        {
            lock (_lock)
            {
                TotalCalls++;
                TotalMs += elapsedMs;
                
                if (success)
                    SuccessCount++;
                else
                    FailureCount++;
                
                if (elapsedMs < MinMs)
                    MinMs = elapsedMs;
                if (elapsedMs > MaxMs)
                    MaxMs = elapsedMs;
            }
        }
        
        public override string ToString()
        {
            return $"{OperationName}: {TotalCalls} calls, Avg={AverageMs:F2}ms, Range=[{MinMs}-{MaxMs}]ms";
        }
    }
}
