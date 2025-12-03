using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MediaBrowser.Model.Logging;
using StrmAssistant.Mod;

namespace StrmAssistant.Core
{
    /// <summary>
    /// 健康检查和自诊断系统
    /// </summary>
    public class HealthCheck
    {
        private static HealthCheck _instance;
        private static readonly object _lock = new object();
        
        private readonly ILogger _logger;
        
        private HealthCheck(ILogger logger)
        {
            _logger = logger;
        }
        
        public static HealthCheck Instance
        {
            get
            {
                if (_instance == null)
                {
                    throw new InvalidOperationException("HealthCheck not initialized. Call Initialize() first.");
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
                        _instance = new HealthCheck(logger);
                    }
                }
            }
        }
        
        /// <summary>
        /// 执行完整的健康检查
        /// </summary>
        public HealthCheckResult PerformHealthCheck()
        {
            var result = new HealthCheckResult();
            
            try
            {
                // 1. 检查核心组件
                result.AddCheck("EmbyVersionAdapter", CheckEmbyVersionAdapter());
                result.AddCheck("FastReflection", CheckFastReflection());
                result.AddCheck("PerformanceMonitor", CheckPerformanceMonitor());
                result.AddCheck("ServiceLocator", CheckServiceLocator());
                
                // 2. 检查Harmony补丁状态
                result.AddCheck("HarmonyPatches", CheckHarmonyPatches());
                
                // 3. 检查性能状态
                result.AddCheck("Performance", CheckPerformance());
                
                // 4. 检查内存使用
                result.AddCheck("Memory", CheckMemory());
                
                _logger.Debug("Health check completed");
            }
            catch (Exception ex)
            {
                _logger.Error($"Health check failed: {ex.Message}");
                result.OverallStatus = HealthStatus.Critical;
                result.AddIssue($"Health check exception: {ex.Message}");
            }
            
            return result;
        }
        
        private HealthStatus CheckEmbyVersionAdapter()
        {
            try
            {
                var version = EmbyVersionAdapter.Instance.CurrentVersion;
                var apiVersion = EmbyVersionAdapter.Instance.ApiVersion;
                return version != null && apiVersion != EmbyApiVersion.Unknown 
                    ? HealthStatus.Healthy 
                    : HealthStatus.Degraded;
            }
            catch
            {
                return HealthStatus.Unhealthy;
            }
        }
        
        private HealthStatus CheckFastReflection()
        {
            try
            {
                var stats = FastReflection.Instance.GetPerformanceStats();
                // 如果缓存命中率低于50%，可能存在问题
                if (stats.TotalInvocations > 100 && stats.CacheHitRate < 50)
                {
                    return HealthStatus.Degraded;
                }
                return HealthStatus.Healthy;
            }
            catch
            {
                return HealthStatus.Unhealthy;
            }
        }
        
        private HealthStatus CheckPerformanceMonitor()
        {
            try
            {
                var slowOps = PerformanceMonitor.Instance.GetSlowOperations(2000);
                if (slowOps.Length > 10)
                {
                    return HealthStatus.Degraded;
                }
                return HealthStatus.Healthy;
            }
            catch
            {
                return HealthStatus.Unhealthy;
            }
        }
        
        private HealthStatus CheckServiceLocator()
        {
            try
            {
                // 简单检查ServiceLocator是否可访问
                _ = ServiceLocator.Instance;
                return HealthStatus.Healthy;
            }
            catch
            {
                return HealthStatus.Unhealthy;
            }
        }
        
        private HealthStatus CheckHarmonyPatches()
        {
            try
            {
                var supportedPatches = PatchManager.PatchTrackerList.Where(p => p.IsSupported).ToList();
                var runningPatches = supportedPatches.Where(p => p.IsRunning).ToList();
                
                if (runningPatches.Count == 0 && supportedPatches.Count > 0)
                {
                    return HealthStatus.Unhealthy;
                }
                
                var failedCorePatches = supportedPatches
                    .Where(p => p.IsCoreFeature && !p.IsRunning)
                    .ToList();
                
                if (failedCorePatches.Any())
                {
                    return HealthStatus.Degraded;
                }
                
                return HealthStatus.Healthy;
            }
            catch
            {
                return HealthStatus.Unknown;
            }
        }
        
        private HealthStatus CheckPerformance()
        {
            try
            {
                var slowOps = PerformanceMonitor.Instance.GetSlowOperations(5000);
                if (slowOps.Length > 5)
                {
                    return HealthStatus.Degraded;
                }
                
                // 检查FastReflection性能
                var stats = FastReflection.Instance.GetPerformanceStats();
                if (stats.TotalInvocations > 1000 && stats.CacheHitRate < 70)
                {
                    return HealthStatus.Degraded;
                }
                
                return HealthStatus.Healthy;
            }
            catch
            {
                return HealthStatus.Unknown;
            }
        }
        
        private HealthStatus CheckMemory()
        {
            try
            {
                var currentMemory = GC.GetTotalMemory(false) / 1024 / 1024; // MB
                
                // 如果内存使用超过500MB，可能有问题
                if (currentMemory > 500)
                {
                    return HealthStatus.Degraded;
                }
                
                return HealthStatus.Healthy;
            }
            catch
            {
                return HealthStatus.Unknown;
            }
        }
        
        /// <summary>
        /// 生成诊断报告
        /// </summary>
        public string GenerateDiagnosticReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== StrmAssistant Diagnostic Report ===");
            sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            
            // 健康检查结果
            var healthCheck = PerformHealthCheck();
            sb.AppendLine(healthCheck.ToString());
            sb.AppendLine();
            
            // 性能统计
            try
            {
                sb.AppendLine("=== Performance Statistics ===");
                sb.AppendLine(PerformanceMonitor.Instance.GetPerformanceReport(detailed: false));
                sb.AppendLine();
                
                sb.AppendLine("=== FastReflection Statistics ===");
                var stats = FastReflection.Instance.GetPerformanceStats();
                sb.AppendLine(stats.ToString());
                sb.AppendLine();
            }
            catch (Exception ex)
            {
                sb.AppendLine($"Failed to get performance stats: {ex.Message}");
            }
            
            // Harmony补丁状态
            try
            {
                sb.AppendLine(PatchManager.GetDiagnosticReport());
            }
            catch (Exception ex)
            {
                sb.AppendLine($"Failed to get patch report: {ex.Message}");
            }
            
            sb.AppendLine("======================================");
            return sb.ToString();
        }
    }
    
    /// <summary>
    /// 健康状态枚举
    /// </summary>
    public enum HealthStatus
    {
        Unknown = 0,
        Healthy = 1,
        Degraded = 2,
        Unhealthy = 3,
        Critical = 4
    }
    
    /// <summary>
    /// 健康检查结果
    /// </summary>
    public class HealthCheckResult
    {
        private readonly Dictionary<string, HealthStatus> _componentStatus;
        private readonly List<string> _issues;
        
        public HealthCheckResult()
        {
            _componentStatus = new Dictionary<string, HealthStatus>();
            _issues = new List<string>();
            OverallStatus = HealthStatus.Healthy;
        }
        
        public HealthStatus OverallStatus { get; set; }
        public IReadOnlyDictionary<string, HealthStatus> ComponentStatus => _componentStatus;
        public IReadOnlyList<string> Issues => _issues;
        
        public void AddCheck(string componentName, HealthStatus status)
        {
            _componentStatus[componentName] = status;
            
            // 更新整体状态
            if ((int)status > (int)OverallStatus)
            {
                OverallStatus = status;
            }
            
            // 记录问题
            if (status != HealthStatus.Healthy)
            {
                _issues.Add($"{componentName}: {status}");
            }
        }
        
        public void AddIssue(string issue)
        {
            _issues.Add(issue);
        }
        
        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== Health Check Result: {OverallStatus} ===");
            
            foreach (var component in _componentStatus)
            {
                var icon = component.Value switch
                {
                    HealthStatus.Healthy => "✓",
                    HealthStatus.Degraded => "⚠",
                    HealthStatus.Unhealthy => "✗",
                    HealthStatus.Critical => "✗✗",
                    _ => "?"
                };
                sb.AppendLine($"  {icon} {component.Key}: {component.Value}");
            }
            
            if (_issues.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Issues:");
                foreach (var issue in _issues)
                {
                    sb.AppendLine($"  - {issue}");
                }
            }
            
            return sb.ToString();
        }
    }
}
